# Deploying SpMS with the Azure portal

This is the simple setup: one database, one API and the front end. There is no Entra ID and
no OpenFGA server.

- **Sign-in:** staff sign in with an email and password that SpMS stores itself (`Auth:Local`).
- **Permissions:** decided inside the API from the same model and the same tables that OpenFGA
  would use (`Authorization:Mode = Local`).

Everything is done in the Azure portal, Visual Studio and VS Code. No CLI and no CI/CD are needed.

| Piece | Azure resource |
|---|---|
| Front end (Angular) | Web App **spms** (exists), on plan **aarfid-app-plan** (Windows) |
| API (.NET 8) | Web App **spms-api**, new, on the same plan. Published from Visual Studio |
| Migrate and nightly housekeeping | WebJobs on **spms-api**, published with it. You click **Run** |
| Database | PostgreSQL flexible server **16**, with a database `spms`. You can reuse an existing 16.x server |

Keep **aarfid-app-plan** at **1 instance** (Scale out → Manual → 1). The live board pushes
updates from inside the API process, so a second instance would miss them.

---

## 1. Database

Reusing an existing server is fine if its version is **16** or later: Overview → *PostgreSQL
version*. On that server:

1. **Databases → + Add:** create `spms`.
2. **Server parameters:** search `azure.extensions`. Keep what is already ticked, add **BTREE_GIST** and
   **PG_TRGM**, then **Save**.
3. **Networking:** tick **Allow public access from any Azure service within Azure to this server**.

You need the server's **admin login and password** for the first run only. The first run
creates two logins for SpMS (`spms_owner_login`, `spms_app_login`). After that, the admin
connection is removed from the API.

For a new server instead: **Create a resource → Azure Database for PostgreSQL flexible
server**, version **16**, Burstable **B1ms**, *PostgreSQL authentication only*, public access
with *allow Azure services*. Then do steps 1 to 3.

---

## 2. The API web app

1. **Create a resource → Web App.**
   - **Basics:** resource group `aarfid`, name `spms-api`, publish **Code**, **.NET 8 (LTS)**, **Windows**, region Central India, plan **aarfid-app-plan**.
   - **Monitoring:** Application Insights on (reuse `spms` or create one).
   - Create it. Copy the **Default domain**. This is `<api-default-domain>`.
2. **Settings → Configuration → General settings**, then **Save**:
   - Platform **64 Bit**
   - **Always On: On**
   - **SCM Basic Auth Publishing Credentials: On** (Visual Studio publishes with it)
   - **HTTPS Only: On**, minimum TLS 1.2
3. **Settings → Health check:** enable it with path `/health/ready`.
4. **API → CORS:** leave it **empty**. SpMS answers CORS itself; setting it here too makes the browser refuse every call.
5. **Settings → Environment variables → App settings → Advanced edit.** Paste the block from
   `spms-api-settings.txt`, which Claude prepared outside the repo. Every setting is listed
   below. Then **OK → Apply → Confirm**.

**Connection strings**

| Name | Value |
|---|---|
| `ConnectionStrings__Spms` | `Host=<pg-host>;Port=5432;Database=spms;SSL Mode=Require;Username=spms_app_login;Password=<PG_APP_PASSWORD>;Maximum Pool Size=50` |
| `ConnectionStrings__SpmsOwner` | `…;Username=spms_owner_login;Password=<PG_OWNER_PASSWORD>` |
| `ConnectionStrings__SpmsAdmin` | `…;Username=<server admin>;Password=<admin password>`. **First run only.** |

**Database roles and logins**

| Name | Value |
|---|---|
| `Database__RuntimeRole` / `Database__MigrationRole` | `spms_app` / `spms_owner` |
| `Database__Logins__Owner__Name` | `spms_owner_login` |
| `Database__Logins__App__Name` | `spms_app_login` |
| `Database__Logins__Owner__Password` | `PG_OWNER_PASSWORD`. **First run only.** |
| `Database__Logins__App__Password` | `PG_APP_PASSWORD`. **First run only.** |

**Sign-in and permissions**

| Name | Value |
|---|---|
| `Auth__Local__Enabled` | `true` |
| `Auth__Local__SigningKey` | `LOCAL_SIGNING_KEY` (base64, 32 bytes) |
| `Auth__Local__AllowRegistration` | `true` for the Register page, where sign-ups wait for approval; `false` to hide it |
| `Auth__GuestSigningKey` | `GUEST_SIGNING_KEY` |
| `Authorization__Mode` | `Local` |

**Keys for stored guest data**

These encrypt and index guest contact details. **Never change them once real data exists.**

| Name | Value |
|---|---|
| `Protection__ActiveKey` | `k1` |
| `Protection__Keys__k1` | `PROTECTION_K1` |
| `Protection__LookupKey` | `LOOKUP_KEY` |

**Web, jobs and monitoring**

| Name | Value |
|---|---|
| `Cors__AllowedOrigins__0` and `Guest__Links__BaseUrl` | `https://spms-gjcrasbdatg3fbb9.centralindia-01.azurewebsites.net` |
| `Jobs__Enabled` | `true` |
| `Observability__MetricsKey` | `METRICS_KEY` |
| `Observability__LogFormat` | `json` |
| `ASPNETCORE_ENVIRONMENT` | `Production` |

**First tenant, property and admins**

| Name | Value |
|---|---|
| `Provision__Tenant__Code` / `__Name` / `__Currency` | `aarfid` / `AARFID` / `INR` |
| `Provision__Properties__0__Code` / `__Name` / `__Timezone` | `main` / your spa's name / `Asia/Kolkata` |
| `Provision__Admins__0__Name` / `__Email` / `__Password` | first admin: name, email, and a temporary password |
| `Provision__Admins__1__Name` / `__Email` / `__Password` | second admin, the same fields |

You need two admins: every role and settings change is proposed by one and approved by the
other. Each admin's temporary password must be changed at first sign-in. Passwords are at
least 10 characters and must not contain the email's name part.

6. **Publish from Visual Studio 2022.**
   1. Open `backend\Spms.sln`, right-click **Spms.Host → Publish → Azure → Azure App Service (Windows) → spms-api → Finish**.
   2. Under **Show all settings**, choose Release, net8.0, *Framework-dependent*, *Portable*, and tick **Remove additional files at destination**.
   3. Click **Publish**.

   The site shows an error page until step 3 has run. That is expected.

---

## 3. First run

1. Open **spms-api → Settings → WebJobs → spms-migrate → Run**, then open **Logs**. A good run ends with:
   - `Applied N migration(s)`
   - `Provisioning for tenant aarfid (…): tenant aarfid; property main; admin …; admin …`
2. Delete the three *first run only* settings, then click **Apply**:
   - `ConnectionStrings__SpmsAdmin`
   - `Database__Logins__Owner__Password`
   - `Database__Logins__App__Password`
3. On **Overview**, click **Restart**. Then `https://<api-default-domain>/health/ready` should return `{"status":"ok",…}`.

`spms-maintain` runs nightly by itself. `spms-fga-sync` is only for the OpenFGA setup, so you can ignore it.

---

## 4. Switch the front end to the real API

1. In `frontend\deploy\config.production.js`, replace `<api-default-domain>`.
2. Run `frontend\deploy\build-for-azure.ps1`.
3. Zip the **contents** of `frontend\dist\web\browser`, then deploy it:
   **spms → Advanced Tools → Go → Tools → Zip Push Deploy**, and drag the zip onto the file list.

---

## 5. Use it

1. **Sign in.** Open the site, sign in as an admin with the temporary password, and choose your own password.
2. **Setup:** add rooms and services.
3. **Staff:**
   - Add a person. On **Profile → Sign-in**, enter their email and click **Give sign-in**.
   - You are shown a temporary password once. Hand it over in person.
   - On **Roles**, propose their role. The *other* admin approves it.
4. **Sign-ups** from the Register page appear at the top of **Staff**. **Approve** them, then give them a role the same way.
5. **Forgotten password:** Staff → the person → **Reset password**. This gives a new temporary password and signs out their other sessions.

## Releasing an update

- **API:**
  1. Visual Studio → Publish.
  2. **WebJobs → spms-migrate → Run.** This is safe every time.
  3. **Restart.**
- **Front end:** run `build-for-azure.ps1`, then Zip Push Deploy.

## When something is wrong

| Symptom | Cause |
|---|---|
| The API won't start | A setting is missing; **Log stream** names it. Or step 3 has not run yet. |
| "The email or password is not right" | Wrong password. After 5 wrong tries the account locks for 15 minutes; an admin reset unlocks it at once. |
| "Your account is active but has no role yet" | Propose a role, and have the other admin approve it. |
| Calls fail as CORS in the browser | Origins are also set in the portal's CORS blade, or `Cors__AllowedOrigins__0` is wrong. |
| The board never shows **Live** | The plan has more than one instance, or Always On is off. |
| Migrate fails on roles or BYPASSRLS | The server is not PostgreSQL 16. |
| Migrate fails on an extension | `azure.extensions` in step 1 was not saved. |

## Later: Entra ID and OpenFGA

Both are still in the code, switched off by settings.

- **Entra ID:** set `Auth__Authority` and `Auth__Audience`, and in `config.js` use `authMode: 'entra'` with an `entra` block. Local accounts can stay on alongside.
- **OpenFGA:** run the server (see `infra/openfga.bicep`), set `Authorization__Mode=OpenFga`, `Authorization__OpenFga__ApiUrl` and `Authorization__OpenFga__ApiToken`, and run the **spms-fga-sync** WebJob once.

The same model decides in both modes. The backend test suite runs the model's tests through the in-process decider.
