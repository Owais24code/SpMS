# Deploying SpMS with the Azure portal

Everything here is done in the Azure portal, Visual Studio and VS Code. No Azure CLI and
no CI/CD are needed.

## What you end up with

All resources are in resource group **aarfid**, in **Central India**.

| Piece | Azure resource | Notes |
|---|---|---|
| Front end (Angular) | Web App **spms** (exists), on plan **aarfid-app-plan** (Windows) | Static files plus `web.config` |
| API (.NET 8) | Web App **spms-api**, new, on the same plan | Deployed with Visual Studio → Publish |
| Migrate / nightly maintain / FGA sync | WebJobs on **spms-api** | Published with the API; you click **Run** |
| Database | Azure Database for PostgreSQL flexible server, **version 16** | Holds two databases: `spms` and `openfga` |
| Authorization | Container App **spms-openfga** plus job **spms-openfga-migrate** | Uses the public image `openfga/openfga:v1.10.2` |
| Sign-in | Two Entra ID app registrations: **SpMS API** and **SpMS web** | |

**Why OpenFGA is on Container Apps:** it ships as a container, and your plan is Windows, so it
can't run there. It is reachable over HTTPS, but refuses any call without its preshared key.
You also restrict it to the API's outbound IP addresses (step 7).

**One instance only.** The live board pushes updates from inside the API process. Keep
**aarfid-app-plan** at **1 instance** (Scale out → Manual → 1) until that moves to a shared bus.

---

## 0. Before you start

1. **Check the plan size.** Open **aarfid-app-plan**, then **Scale up**. It needs **Basic (B1)
   or higher**: *Always On* and scheduled WebJobs don't exist on Free or Shared.
2. **Make the secrets.** Run this once in Windows PowerShell. Paste the output into a password
   manager, not a file in the repo.

   ```powershell
   function New-Key      { $b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b) }
   function New-Password { $b = New-Object byte[] 24; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); ([BitConverter]::ToString($b) -replace '-','') + 'spms' }
   "PG_ADMIN_PASSWORD  = $(New-Password)"
   "PG_OWNER_PASSWORD  = $(New-Password)"
   "PG_APP_PASSWORD    = $(New-Password)"
   "FGA_KEY            = $(New-Password)"
   "PROTECTION_K1      = $(New-Key)"
   "LOOKUP_KEY         = $(New-Key)"
   "GUEST_SIGNING_KEY  = $(New-Key)"
   "METRICS_KEY        = $(New-Password)"
   ```

   The passwords are letters and digits only, so they need no escaping in connection strings
   or URLs.

   **Never change `PROTECTION_K1` or `LOOKUP_KEY` once data exists.** They encrypt and index
   guest contact details, and losing them makes that data unreadable.

---

## 1. PostgreSQL

1. **Create a resource.**
   - Open **Create a resource → Azure Database for PostgreSQL flexible server**.
   - **Basics:**
     - Resource group **aarfid**, server name e.g. `spms-pg-aarfid` (must be unique), region **Central India**.
     - **PostgreSQL version 16.** It must be 16, not 15: the roles SpMS creates need PG16's
       BYPASSRLS behaviour on Azure.
     - Workload *Development*. Compute **Burstable B1ms**, storage 32 GiB, high availability off.
   - **Authentication:** *PostgreSQL authentication only*. Admin `spmsadmin`, password `PG_ADMIN_PASSWORD`.
   - **Networking:**
     - Choose *Public access*.
     - Tick **Allow public access from any Azure service within Azure to this server**.
     - Add your current client IP if you want to connect with pgAdmin.
   - Review + create.
2. **Settings → Server parameters.** Search `azure.extensions`, tick **BTREE_GIST** and
   **PG_TRGM**, then **Save**.
3. **Settings → Databases → Add.** Create `spms`, then create `openfga`.

Write down the host, **`<pg-host>`** = `spms-pg-aarfid.postgres.database.azure.com`.

---

## 2. Entra ID app registrations

In **Microsoft Entra ID → App registrations → New registration**:

**SpMS API**

1. Set supported account types to *Single tenant* and add no redirect URI. Register it.
2. Note its **Application (client) ID**. This is `<api-client-id>`. Also note the
   **Directory (tenant) ID**, which is `<tenant-id>`.
3. **Expose an API.**
   - Set the Application ID URI to `api://<api-client-id>`.
   - Add a scope `access_as_user`: consent *Admins and users*, display name "Use SpMS".
4. **Manifest.** Set `"requestedAccessTokenVersion": 2`. In the older manifest view this is
   `"accessTokenAcceptedVersion": 2`. Save.

   This matters because a v1 token has a different issuer, and SpMS would reject every sign-in.

**SpMS web**

1. Register it as *Single tenant*.
2. Choose platform **Single-page application**. Redirect URI:
   `https://spms-gjcrasbdatg3fbb9.centralindia-01.azurewebsites.net/sign-in`
3. Note its **Application (client) ID**. This is `<spa-client-id>`.
4. **API permissions → Add → My APIs → SpMS API → access_as_user.** Then click
   **Grant admin consent**.

**Admins' object ids.** In **Entra ID → Users**, open each person who will administer SpMS
and copy their **Object ID**. You need two administrators, because SpMS requires a role
proposed by one admin to be approved by a different one.

---

## 3. OpenFGA (Container Apps)

Both OpenFGA resources use the same datastore URI:

```
postgres://spmsadmin:<PG_ADMIN_PASSWORD>@<pg-host>:5432/openfga?sslmode=require
```

**3a. The migration job.** Go to **Create a resource → Container App Job**.

- **Basics:**
  - Resource group **aarfid**, name `spms-openfga-migrate`, region Central India.
  - Environment: create a new one, `spms-env`, *Consumption only*.
  - Trigger **Manual**, replica timeout 600, retry limit 0.
- **Container:**
  - Image source *Docker Hub or other registries*, public.
  - Registry `docker.io`, image and tag `openfga/openfga:v1.10.2`.
  - Arguments override: `migrate`. CPU 0.25, memory 0.5 Gi.
  - Environment variables:
    - `OPENFGA_DATASTORE_ENGINE` = `postgres`
    - `OPENFGA_DATASTORE_URI` = the datastore URI above
- Create it. Then click **Run now** on its Overview. The run should show **Succeeded** under
  **Execution history**.

**3b. The server.** Go to **Create a resource → Container App**.

- **Basics:** name `spms-openfga`, in the same environment `spms-env`.
- **Container:**
  - The same image, `openfga/openfga:v1.10.2`. Arguments override `run`. CPU 0.5, memory 1 Gi.
  - Environment variables:

    | Name | Value |
    |---|---|
    | `OPENFGA_DATASTORE_ENGINE` | `postgres` |
    | `OPENFGA_DATASTORE_URI` | the datastore URI above |
    | `OPENFGA_AUTHN_METHOD` | `preshared` |
    | `OPENFGA_AUTHN_PRESHARED_KEYS` | `FGA_KEY` |
    | `OPENFGA_PLAYGROUND_ENABLED` | `false` |
    | `OPENFGA_LOG_FORMAT` | `json` |
- **Ingress:** enabled, *Accepting traffic from anywhere*, HTTP, target port **8080**.
- Create it. Then go to **Application → Scale** and set min **1**, max **1**. Authorization fails
  closed, so a scale-to-zero cold start would be an outage.

From its Overview, copy the **Application Url**. This is `<openfga-url>`.

---

## 4. The API web app

**4a. Create it.** Go to **Create a resource → Web App**.

- **Basics:**
  - Resource group **aarfid**, name `spms-api`, publish **Code**.
  - Runtime stack **.NET 8 (LTS)**, OS **Windows**, region Central India, plan **aarfid-app-plan**.
- **Monitoring:** enable Application Insights and pick the existing **spms**, or create a new one.
- Create it. From Overview, copy the **Default domain**. This is `<api-default-domain>`.

**4b. Settings → Configuration → General settings:**

- Platform **64 Bit**
- **Always On: On** (the outbox worker and the nightly WebJob need it)
- **HTTPS Only: On**, minimum TLS 1.2
- If Visual Studio's publish later fails with 401, turn **SCM Basic Auth Publishing
  Credentials** on.

Save.

**4c. Settings → Health check.** Enable it with path `/health/ready`.

**4d. Leave API → CORS empty.** SpMS answers CORS itself. With both, the browser sees the header
twice and refuses every call.

**4e. Settings → Environment variables → App settings.** Add each of these. The *first run
only* rows are removed after step 5.

Connection strings:

| Name | Value |
|---|---|
| `ConnectionStrings__Spms` | `Host=<pg-host>;Port=5432;Database=spms;SSL Mode=Require;Username=spms_app_login;Password=<PG_APP_PASSWORD>;Maximum Pool Size=50` |
| `ConnectionStrings__SpmsOwner` | `Host=<pg-host>;Port=5432;Database=spms;SSL Mode=Require;Username=spms_owner_login;Password=<PG_OWNER_PASSWORD>` |
| `ConnectionStrings__SpmsAdmin` *(first run only)* | `Host=<pg-host>;Port=5432;Database=spms;SSL Mode=Require;Username=spmsadmin;Password=<PG_ADMIN_PASSWORD>` |

Database roles and logins:

| Name | Value |
|---|---|
| `Database__RuntimeRole` | `spms_app` |
| `Database__MigrationRole` | `spms_owner` |
| `Database__Logins__Owner__Name` | `spms_owner_login` |
| `Database__Logins__Owner__Password` *(first run only)* | `PG_OWNER_PASSWORD` |
| `Database__Logins__App__Name` | `spms_app_login` |
| `Database__Logins__App__Password` *(first run only)* | `PG_APP_PASSWORD` |

Sign-in and authorization:

| Name | Value |
|---|---|
| `Auth__Authority` | `https://login.microsoftonline.com/<tenant-id>/v2.0` |
| `Auth__Audience` | `<api-client-id>`, the GUID itself. A v2 token's audience is the client id, not `api://…` |
| `Auth__GuestSigningKey` | `GUEST_SIGNING_KEY` |
| `Authorization__OpenFga__ApiUrl` | `<openfga-url>` |
| `Authorization__OpenFga__ApiToken` | `FGA_KEY` |

Data protection:

| Name | Value |
|---|---|
| `Protection__ActiveKey` | `k1` |
| `Protection__Keys__k1` | `PROTECTION_K1` |
| `Protection__LookupKey` | `LOOKUP_KEY` |

Web, jobs and observability:

| Name | Value |
|---|---|
| `Cors__AllowedOrigins__0` | `https://spms-gjcrasbdatg3fbb9.centralindia-01.azurewebsites.net` |
| `Guest__Links__BaseUrl` | `https://spms-gjcrasbdatg3fbb9.centralindia-01.azurewebsites.net` |
| `Jobs__Enabled` | `true` |
| `Observability__MetricsKey` | `METRICS_KEY` |
| `Observability__LogFormat` | `json` |
| `ASPNETCORE_ENVIRONMENT` | `Production` |

First-run provisioning. Creating your tenant, property and administrators is idempotent, so
leaving these settings in place does no harm:

| Name | Value (example) |
|---|---|
| `Provision__Tenant__Code` | `aarfid` (lower-case letters, digits and dashes) |
| `Provision__Tenant__Name` | `AARFID` |
| `Provision__Tenant__Currency` | `INR` |
| `Provision__Properties__0__Code` | `main` |
| `Provision__Properties__0__Name` | your spa's name |
| `Provision__Properties__0__Timezone` | `Asia/Kolkata` |
| `Provision__Admins__0__ObjectId` | first admin's Entra object id |
| `Provision__Admins__0__Name` | e.g. `Owais` |
| `Provision__Admins__0__Email` | their work email |
| `Provision__Admins__1__ObjectId` | second admin's object id |
| `Provision__Admins__1__Name` / `__Email` | … |

Each admin gets the tenant-wide roles *platform_admin*, *spa_manager* and *configuration_approver*. The two admins then approve each other's role and settings changes. To set other roles,
use `Provision__Admins__0__Roles__0`, `__Roles__1`, and so on. Add more properties with
`Provision__Properties__1__…`.

Click **Apply**. The app restarts.

**4f. Publish from Visual Studio 2022.**

1. Open `backend\Spms.sln` and right-click **Spms.Host → Publish**.
2. Choose **Azure → Azure App Service (Windows)**, then **spms-api**, then **Finish**.
3. Under **Show all settings**, set Configuration *Release*, target framework *net8.0*,
   deployment mode *Framework-dependent* and target runtime *Portable*. Under *File publish
   options*, tick **Remove additional files at destination**.
4. Click **Publish**.

The three WebJobs are published with the app, under `App_Data\jobs\triggered`. Until step 5
runs, the API shows an error page, because the database is empty. That is expected.

---

## 5. Create the schema and your tenant

1. Open **spms-api → Settings → WebJobs**. You should see **spms-migrate**, **spms-maintain**
   and **spms-fga-sync**.
2. Select **spms-migrate** and click **Run**, then open **Logs**. A good run ends with
   `Applied N migration(s)` and `Provisioning for tenant aarfid (…): tenant aarfid; property main;
   admin …`.
3. Select **spms-fga-sync** and click **Run**. Its log shows `OpenFGA store …, model …` and
   `OpenFGA reconciliation wrote N tuples`.
4. Remove the *first run only* app settings: `ConnectionStrings__SpmsAdmin` and the two
   `Database__Logins__*__Password` settings. Apply. The running API never needs the admin login.
5. **Overview → Restart.** Then open `https://<api-default-domain>/health/ready`. It should
   return `{"status":"ok",…}`.

---

## 6. The front end

1. Fill in `frontend\deploy\config.production.js`: `<api-default-domain>`, `<spa-client-id>`,
   `<tenant-id>` and `<api-client-id>`. Set the guest codes to your provisioning codes. Commit
   it; none of it is secret.
2. In PowerShell, run `frontend\deploy\build-for-azure.ps1`. You need Node.js 22.22.3 or later.
3. In VS Code, install the **Azure App Service** extension and sign in. Right-click
   `frontend\dist\web\browser`, choose **Deploy to Web App…**, pick **spms**, and confirm the
   overwrite.

   Without VS Code, zip the *contents* of `dist\web\browser` instead. Open
   **spms → Advanced Tools → Go → Tools → Zip Push Deploy** and drag the zip in.

The build includes `web.config`, which provides the deep links (`/app/schedule` answers 200)
and never caches `index.html` or `config.js`. The web app's .NET 9 stack setting does not
matter, because IIS serves the files directly.

---

## 7. Lock OpenFGA to the API

1. Open **spms-api → Settings → Networking → Outbound traffic configuration**. Copy every
   address under **Outbound addresses**.
2. Open **spms-openfga → Networking → Ingress → IP Security Restrictions Mode → Allow**. Add
   each address as `x.x.x.x/32`. Save.

The preshared key is still the real protection; this step keeps OpenFGA off the open internet.

---

## 8. First sign-in and smoke test

1. Open `https://spms-gjcrasbdatg3fbb9.centralindia-01.azurewebsites.net` and sign in as one of
   the provisioned admins. You land in the workspace with your spa's name in the header.
2. **Setup:** add rooms and services. Settings such as deposit and cancellation policy are
   proposed by one admin and approved by the other, as are roles.
3. **Staff:**
   - Add a person.
   - On their Profile tab, under **Sign-in**, paste their Entra object id and click
     **Give sign-in**.
   - On Roles, propose their role. The *other* admin approves it.
   - They can now sign in.
4. **Schedule:** book, move and check in one appointment. Open the board in a second browser;
   it should show **Live** and update without a refresh.
5. **WebJobs → spms-maintain → Run** once by hand. After that it runs nightly at 03:17 UTC.

---

## Releasing an update

- **API:**
  1. Visual Studio → Publish.
  2. **WebJobs → spms-migrate → Run.** This is safe every time; it applies only new migrations.
  3. If `authorization/model.fga` changed, also run **spms-fga-sync**.
  4. **Restart** spms-api. The API pins the authorization model when it starts.
- **Front end:** run `build-for-azure.ps1`, then Deploy to Web App.

A release that adds database roles says so in its notes. For that one run, put
`ConnectionStrings__SpmsAdmin` back.

## When something is wrong

| Symptom | Where to look | Usual cause |
|---|---|---|
| API won't start | spms-api → **Log stream** | A missing setting from 4e (the message names it), or step 5 not run yet |
| "This identity is not provisioned in SpMS" | | The person has no sign-in (step 8.3), or the API registration still issues v1 tokens (step 2, manifest) |
| Every call is 401 | | `Auth__Audience` is `api://…` instead of the client-id GUID |
| Every call is 503 | spms-openfga → **Log stream** | OpenFGA unreachable, the key doesn't match, or the IP list is missing an address |
| Calls fail as "CORS" in the browser | | Origins are set in the portal CORS blade as well (4d), or the front-end URL in `Cors__AllowedOrigins__0` is wrong |
| Board never shows Live | | Plan scaled to more than one instance, or Always On is off |
| migrate fails on roles or BYPASSRLS | | The server isn't PostgreSQL 16 |
| migrate fails on an extension | | Step 1.2 (`azure.extensions`) was not saved |
