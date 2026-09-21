# Deploying the front end to Vercel

Vercel replaces the whole Blob + Front Door arrangement in `infra/main.bicep`.
The SPA fallback, TLS, a global CDN and the cache headers are all things Vercel
does by default or from `web/vercel.json`, so there is no rewrite rule to get
right and no storage account to provision.

The deployed site runs in **demo mode** — the in-memory store — because the API
is local. A page served over HTTPS cannot call `http://127.0.0.1`; the browser
blocks it as mixed content. Demo mode is what makes every screen usable from
the URL with nothing running anywhere.

Two files in the repo do the work:

| File | What it does |
|---|---|
| `web/vercel.json` | Build command, output directory, SPA fallback, cache and security headers |
| `web/tools/write-runtime-config.mjs` | Writes `assets/config.js` from environment variables after the build |

## Steps

### 1. Import the repository

At <https://vercel.com/new>, pick the SpMS repository.

### 2. Set the Root Directory to `web`

This is the step that is easy to miss and confusing to debug. The repository
root holds `api/`, `web/`, `infra/` and `docs/`; Vercel looks for
`package.json` in the root directory you give it. Left at the repository root
it finds nothing to build and reports a framework it cannot detect.

Set it to `web`, and `web/vercel.json` becomes the configuration Vercel reads.

### 3. Set the Node.js version to 22.x or 24.x

Project Settings → General → Node.js Version.

`web/.npmrc` sets `engine-strict=true` and Angular 22 requires Node ≥ 22.22.3.
On an older Node the install fails with `EBADENGINE` naming a version nobody
chose, which reads like a dependency problem rather than a project setting.

### 4. Deploy

Leave the build and output settings alone — `vercel.json` supplies them:

    buildCommand      ng build --configuration production && node tools/write-runtime-config.mjs
    outputDirectory   dist/web/browser

Press **Deploy**. No environment variables are needed: the config writer
defaults to demo mode.

### 5. Check a deep link, not just the home page

    https://<your-deployment>.vercel.app/            → 200
    https://<your-deployment>.vercel.app/app/schedule → 200, and lands on
                                                        /sign-in?returnUrl=%2Fapp%2Fschedule

The home page working proves almost nothing — it is the one path a static host
serves correctly without any configuration. The deep link is the test: it has
to reach `index.html` under a **200**, and the router then has to read the
original URL and keep it as the return URL. If the deep link 404s, the rewrite
in `vercel.json` is not being applied, which in practice means step 2 was
missed and Vercel is reading a different configuration.

Then sign in and open the schedule — the board should draw five lanes with a
conflict flagged on Marco Ruiz.

## From the command line instead

```bash
npm i -g vercel
cd web
vercel login
vercel link          # answer: yes, new project, root directory = .
vercel --prod
```

Run these from `web/`, not the repository root, or `vercel` picks up the wrong
directory and the root-directory problem in step 2 comes back in a different
shape.

## Pointing it at a real API

Once the API is deployed somewhere with HTTPS, set these in Project Settings →
Environment Variables and redeploy:

| Variable | Value |
|---|---|
| `SPMS_USE_REAL_API` | `true` |
| `SPMS_API_BASE_URL` | `https://spms-api.example.com` |

The config writer refuses two mistakes at build time rather than shipping them:

- `SPMS_USE_REAL_API=true` with no base URL — the compiled fallback is `/api`,
  and a static host has nothing there, so every request would 404.
- An `http://` base URL — blocked as mixed content in every browser, and the
  board would fail silently.

The API also needs CORS allowing the Vercel origin and the `Idempotency-Key`,
`If-Match` and `X-Correlation-Id` request headers, with `ETag` and `Location`
exposed on responses. The client reads all five.

## What Vercel does not give you

- **No API hosting for the .NET app.** Vercel's functions do not run ASP.NET
  Core, and there is no managed PostgreSQL here. The API and its database still
  need a host — Azure App Service, Container Apps, Fly, Render.
- **Two deployment paths now exist** for the same front end: this one and
  `infra/main.bicep`. Keep whichever you intend to use and delete the other
  when the choice is settled, or the next person will not know which is live.
