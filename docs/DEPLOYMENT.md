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
