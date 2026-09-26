# Builds the SpMS front end for the `spms` Azure web app.
#
#   cd frontend\deploy
#   .\build-for-azure.ps1
#
# Then deploy the folder frontend\dist\web\browser to the web app (VS Code:
# right-click the folder -> "Deploy to Web App..." -> spms). Needs Node.js 22.22.3+.
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    npm ci
    npx ng build --configuration production
    Copy-Item -Force deploy\config.production.js dist\web\browser\assets\config.js
    if (-not (Test-Path dist\web\browser\web.config)) { throw 'web.config is missing from the build output (frontend\public\web.config).' }
    if (Select-String -Path dist\web\browser\assets\config.js -Pattern '<(api-default-domain|spa-client-id|tenant-id|api-client-id)>' -Quiet) {
        Write-Warning 'config.production.js still has <placeholders>. Fill them in before deploying.'
    }
    Write-Host "Built. Deploy this folder: $(Resolve-Path dist\web\browser)"
}
finally { Pop-Location }
