# Deploying the SpMS front end

The Angular application is deployed to **Azure Blob static website hosting
behind Azure Front Door Standard**. The API is not deployed: it runs locally, by
decision, so the deployed build runs in **demo mode** against the in-memory
store. Every screen is usable from the public URL with nothing running on
anyone's machine, and repointing the site at a real API later does not need a
rebuild.

Two files do the work:

| File | What it is |
|---|---|
| `infra/main.bicep` | The storage account, the Front Door profile, and the rules |
| `.github/workflows/deploy-web.yml` | Build, deploy, upload, purge, verify |

## Why Front Door is in the stack

Blob static website hosting has exactly two routing controls: an index document
and a 404 document. `/app/schedule` is not a blob, so the account answers
it with the 404 document. Pointing that at `index.html` makes the deep link
*work* — the Angular router reads the URL and renders the right screen — but it
arrives under a **404 status code**. Every crawler, uptime check and cache in
the path is told the page does not exist.

Front Door is what makes it a 200. A rules-engine rule matches any request whose
URL has no file extension and rewrites it to `/index.html` *before* the request
reaches the origin, so the origin is asked for a blob that exists. Front Door
also supplies a managed TLS certificate on a stable hostname, HTTP-to-HTTPS
redirection, and the split cache policy the site needs — a year on the
fingerprinted bundle, no-store on the two files that decide which bundle and
which API the browser uses.

There is a cost to this: Front Door Standard has a monthly base charge. If that
is not worth paying for a demo site, Azure Static Web Apps has the SPA fallback
built in for free and is the better fit — but it is a different resource, not a
setting on this one.

## One-time setup

Done once per subscription. Everything after this is `git push`.

### 1. Resource group

```bash
az group create --name rg-spms-dev --location westeurope
```

### 2. App registration with a federated credential

No publish profile and no storage key is stored in the repository. The workflow
asks GitHub's OIDC provider for a short-lived token and exchanges it for an
Azure one, so there is no long-lived credential here to leak or rotate.

```bash
# Replace OWNER/REPO with your repository.
REPO="OWNER/REPO"
RG="rg-spms-dev"
SUB=$(az account show --query id -o tsv)

APP_ID=$(az ad app create --display-name "spms-deploy" --query appId -o tsv)
az ad sp create --id "$APP_ID"
OBJ_ID=$(az ad sp show --id "$APP_ID" --query id -o tsv)

# Control plane: create and update the resources in this one group.
az role assignment create \
  --assignee-object-id "$OBJ_ID" --assignee-principal-type ServicePrincipal \
  --role Contributor \
  --scope "/subscriptions/$SUB/resourceGroups/$RG"

# Trust: one credential per environment, because the subject includes it.
az ad app federated-credential create --id "$APP_ID" --parameters "{
  \"name\": \"github-dev\",
  \"issuer\": \"https://token.actions.githubusercontent.com\",
  \"subject\": \"repo:$REPO:environment:dev\",
  \"audiences\": [\"api://AzureADTokenExchange\"]
}"

echo "AZURE_CLIENT_ID        $APP_ID"
echo "AZURE_TENANT_ID        $(az account show --query tenantId -o tsv)"
echo "AZURE_SUBSCRIPTION_ID  $SUB"
echo "AZURE_DEPLOYER_OBJECT_ID  $OBJ_ID"
```

The **object id** is the one that matters for the data-plane role. Contributor
does not grant blob access, so the template assigns *Storage Blob Data
Contributor* on the storage account to whatever `deployerPrincipalId` it is
given. That is why the object id — not the client id — goes into the repository
variables below.

The federated `subject` names the GitHub **environment**, which is why the
workflow declares `environment:`. A run from any other environment, branch or
repository presents a subject the credential does not match and is refused,
which is the point.

### 3. Repository configuration

Secrets (Settings → Secrets and variables → Actions → Secrets):

| Secret | Value |
|---|---|
| `AZURE_CLIENT_ID` | the app registration's application (client) id |
| `AZURE_TENANT_ID` | the directory (tenant) id |
| `AZURE_SUBSCRIPTION_ID` | the subscription id |

Variables (→ Variables):

| Variable | Value |
|---|---|
| `AZURE_RESOURCE_GROUP` | `rg-spms-dev` |
| `AZURE_DEPLOYER_OBJECT_ID` | the service principal's **object** id |

Then create a GitHub environment named `dev` (Settings → Environments), or the
federated subject will not match.

## Deploying

Push to `main` with a change under `web/` or `infra/`, or run **Deploy web** by
hand and pick an environment. The workflow builds, deploys the template, enables
static website hosting, uploads in two passes with different cache lifetimes,
purges the edge, and then **verifies that a deep link answers 200** — the one
thing Blob hosting cannot do alone, and the only reason Front Door is here. The
run summary carries the site URL.

## Deploying from your own machine

The same sequence, if you would rather not wait for a runner. Requires the Azure
CLI and a `web/dist/web/browser` you have already built.

```bash
RG=rg-spms-dev

az deployment group create \
  --resource-group "$RG" \
  --template-file infra/main.bicep \
  --parameters namePrefix=spms environmentName=dev

ACCOUNT=$(az deployment group show -g "$RG" -n main --query properties.outputs.storageAccountName.value -o tsv)
PROFILE=$(az deployment group show -g "$RG" -n main --query properties.outputs.frontDoorProfileName.value -o tsv)
ENDPOINT=$(az deployment group show -g "$RG" -n main --query properties.outputs.frontDoorEndpointName.value -o tsv)
SITE=$(az deployment group show -g "$RG" -n main --query properties.outputs.siteUrl.value -o tsv)

az storage blob service-properties update --account-name "$ACCOUNT" --auth-mode login \
  --static-website --index-document index.html --404-document index.html

cd web && npx ng build --configuration production && cd ..

cat > web/dist/web/browser/assets/config.js <<'CONFIG'
window.__SPMS_CONFIG__ = { useRealApi: false };
CONFIG

az storage blob upload-batch --account-name "$ACCOUNT" --auth-mode login \
  --destination '$web' --source web/dist/web/browser --overwrite \
  --content-cache 'public, max-age=31536000, immutable' --exclude-pattern 'index.html'

for f in index.html assets/config.js; do
  az storage blob upload --account-name "$ACCOUNT" --auth-mode login \
    --container-name '$web' --name "$f" --file "web/dist/web/browser/$f" \
    --overwrite --content-cache 'no-cache, no-store, must-revalidate'
done

az afd endpoint purge -g "$RG" --profile-name "$PROFILE" --endpoint-name "$ENDPOINT" --content-paths '/*'

curl -s -o /dev/null -w 'root %{http_code}\n'      "$SITE/"
curl -s -o /dev/null -w 'deep link %{http_code}\n' "$SITE/app/schedule"
```

Both must read 200. If the root is 200 and the deep link is 404, the rewrite
rule is not in effect — check the rule set is linked to the route.

If `--auth-mode login` is refused, your own account needs *Storage Blob Data
Contributor* on the account; the role the template assigns is for the workflow's
identity, not yours.

## Repointing the site at a real API

Nothing needs rebuilding. `assets/config.js` is written after the build and is
never fingerprinted, which is the whole reason it exists:

```bash
cat > config.js <<'CONFIG'
window.__SPMS_CONFIG__ = {
  apiBaseUrl: 'https://spms-api.example.com',
  useRealApi: true,
};
CONFIG

az storage blob upload --account-name "$ACCOUNT" --auth-mode login \
  --container-name '$web' --name assets/config.js --file config.js --overwrite \
  --content-cache 'no-cache, no-store, must-revalidate'

az afd endpoint purge -g "$RG" --profile-name "$PROFILE" --endpoint-name "$ENDPOINT" \
  --content-paths '/assets/config.js' '/index.html'
```

Two things have to be true of that API before the board works against it:

- **HTTPS.** A page served over HTTPS cannot call an `http://` origin; the
  browser blocks it as mixed content. `http://127.0.0.1:5199` will not work from
  the deployed site, which is why the deployed build ships in demo mode.
- **CORS**, allowing the Front Door origin and the `Idempotency-Key`,
  `If-Match` and `X-Correlation-Id` request headers, and exposing `ETag` and
  `Location` in the response — the client reads all five.

The tidier arrangement, once the API is deployed, is a second origin group and a
`/api/*` route on the same Front Door endpoint. Then the site and the API are
same-origin, CORS stops being a concern, and `apiBaseUrl` can stay at its
compiled default of `/api`. The SPA route matches `/*` rather than claiming
specific paths precisely so that a more specific `/api/*` route can be added in
front of it.

---

# Managing Front Door

Everything below assumes these three, which the deployment outputs give you:

```bash
RG=rg-spms-dev
PROFILE=$(az deployment group show -g "$RG" -n main --query properties.outputs.frontDoorProfileName.value -o tsv)
ENDPOINT=$(az deployment group show -g "$RG" -n main --query properties.outputs.frontDoorEndpointName.value -o tsv)
```

## Purging the cache

The one operation you will run most. Front Door holds `index.html` at every
edge it has served it from, so a deploy that does not purge appears not to have
happened — for some users, for an unpredictable length of time.

```bash
# Everything. Slow (minutes) but unambiguous.
az afd endpoint purge -g "$RG" --profile-name "$PROFILE" --endpoint-name "$ENDPOINT" \
  --content-paths '/*'

# Just the two files that are not fingerprinted. This is all a normal deploy
# needs: every other file changed its name, so no edge is holding a stale copy
# under a URL anyone will request.
az afd endpoint purge -g "$RG" --profile-name "$PROFILE" --endpoint-name "$ENDPOINT" \
  --content-paths '/' '/index.html' '/assets/config.js'
```

Purging is eventually consistent. If you are checking by hand, use a fresh
private window or `curl -H 'Cache-Control: no-cache'` — your own browser cache
will otherwise convince you the purge failed.

## Adding a custom domain

Five steps, and the order matters: the certificate cannot be issued before the
TXT record exists, and the domain serves nothing until it is attached to the
route.

```bash
DOMAIN=spa.example.com
NAME=spa-example-com          # Azure resource name; dots are not allowed

# 1. Declare it, with a free managed certificate.
az afd custom-domain create -g "$RG" --profile-name "$PROFILE" \
  --custom-domain-name "$NAME" --host-name "$DOMAIN" \
  --certificate-type ManagedCertificate --minimum-tls-version TLS12

# 2. Read the validation token.
az afd custom-domain show -g "$RG" --profile-name "$PROFILE" \
  --custom-domain-name "$NAME" --query validationProperties -o json
```

3. At your DNS provider add **two** records:

| Type | Name | Value |
|---|---|---|
| `TXT` | `_dnsauth.spa` | the `validationToken` from step 2 |
| `CNAME` | `spa` | `<endpoint>.azurefd.net` |

For an apex domain (`example.com`) a CNAME is not legal; use Azure DNS and an
alias record, or your provider's ALIAS/ANAME equivalent.

```bash
# 4. Wait for validation. Approved means the certificate has been issued.
az afd custom-domain show -g "$RG" --profile-name "$PROFILE" \
  --custom-domain-name "$NAME" --query '{domain:domainValidationState, cert:tlsSettings.certificateType}'

# 5. Attach it to the route. Until this runs the domain resolves and then 404s,
#    which looks like a DNS problem and is not one.
az afd route update -g "$RG" --profile-name "$PROFILE" --endpoint-name "$ENDPOINT" \
  --route-name spa --custom-domains "$NAME"
```

## Inspecting and changing the rules

The SPA rewrite is the one rule the site cannot work without. If deep links
start 404ing, this is the first thing to look at:

```bash
az afd rule list -g "$RG" --profile-name "$PROFILE" --rule-set-name spaRules \
  -o table

az afd rule show -g "$RG" --profile-name "$PROFILE" --rule-set-name spaRules \
  --rule-name spaDeepLinkRewrite -o json
```

Change rules in `infra/main.bicep` and redeploy, not here. A rule edited in the
portal is silently reverted by the next deployment, and the two minutes you
spend finding that out are worse than the two minutes the redeploy takes.

## Watching it

```bash
# Is the origin healthy? An unhealthy origin serves 503 from every edge.
az afd origin show -g "$RG" --profile-name "$PROFILE" \
  --origin-group-name web-origin-group --origin-name blob-static-website \
  --query '{host:hostName, enabled:enabledState}'

# Cache hit ratio and request counts, last 24h.
PROFILE_ID=$(az afd profile show -g "$RG" --profile-name "$PROFILE" --query id -o tsv)
az monitor metrics list --resource "$PROFILE_ID" \
  --metric RequestCount OriginRequestCount --interval PT1H -o table
```

Two metrics are worth an alert. `Percentage4XX` rising is usually a deploy that
uploaded a changed `index.html` without purging, so browsers are asking for
bundle files that no longer exist. `OriginHealthPercentage` dropping means the
storage account is refusing Front Door — most often static website hosting got
turned off, or `allowBlobPublicAccess` was disabled by a policy.

Diagnostic logs are not on by default and are the only way to see which rule
matched a given request:

```bash
WORKSPACE=$(az monitor log-analytics workspace create -g "$RG" -n spms-logs --query id -o tsv)
az monitor diagnostic-settings create --name afd-logs --resource "$PROFILE_ID" \
  --workspace "$WORKSPACE" \
  --logs '[{"category":"FrontDoorAccessLog","enabled":true},{"category":"FrontDoorHealthProbeLog","enabled":true}]'
```

## What it costs

Front Door Standard bills a **fixed base fee of roughly $35/month** plus about
**$0.0825/GB** egress and a small per-request charge — so this site costs
essentially the base fee, and the traffic is rounding error. Check
[the pricing page](https://azure.microsoft.com/pricing/details/frontdoor/) for
your region before committing; these figures move.

That base fee buys one thing this stack genuinely needs — the deep-link
rewrite — plus a managed certificate and a CDN. If it is not worth paying for a
demo, **Azure Static Web Apps** has the SPA fallback and a managed certificate
on its free tier. That is a different resource, not a setting on this one:
it would replace `infra/main.bicep` rather than amend it.

## Turning it off

The storage account is a few cents a month; the Front Door profile is the bill.

```bash
# Stop serving without losing the configuration.
az afd endpoint update -g "$RG" --profile-name "$PROFILE" \
  --endpoint-name "$ENDPOINT" --enabled-state Disabled

# Or remove everything. The resource group holds nothing else.
az group delete -n "$RG" --yes
```

A disabled endpoint still bills the base fee. Only deleting the profile stops
it.
