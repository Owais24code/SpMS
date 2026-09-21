// -----------------------------------------------------------------------------
// SpMS front end: Blob static website behind Azure Front Door Standard.
//
// Why Front Door is not optional here. Blob static website hosting can serve an
// index document and a 404 document, and that is the whole of its routing. An
// Angular deep link such as /app/schedule is not a blob, so the account
// answers it with the 404 document — index.html, but under a 404 status. The
// router then renders the right screen while every crawler, uptime check and
// browser cache is told the page does not exist. Front Door is what turns that
// into a 200: a rule rewrites any extensionless path to /index.html before the
// request reaches the origin, so the origin serves a blob that exists.
//
// Front Door also supplies the things the storage endpoint cannot: a managed
// TLS certificate on a stable hostname, HTTP-to-HTTPS redirection, and cache
// control that can differ between the fingerprinted bundle and the two files
// that must never be cached.
//
// There are no API resources in this file. The API runs locally for now, by
// decision, so there is nothing here to point an origin at. When that changes,
// the addition is an origin group plus a /api/* route on the SAME endpoint,
// which is why the SPA route matches /* rather than claiming every path.
// -----------------------------------------------------------------------------

targetScope = 'resourceGroup'

@description('Short name that every resource is derived from. Lowercase letters and digits only: the storage account name is built from it and Azure allows nothing else there.')
@minLength(3)
@maxLength(11)
param namePrefix string = 'spms'

@description('Region for the storage account. Front Door is global and ignores this.')
param location string = resourceGroup().location

@description('Environment discriminator, so dev and prod can share a subscription without colliding.')
@allowed(['dev', 'test', 'prod'])
param environmentName string = 'dev'

@description('Object id of the principal that runs the deploy workflow. Supply it and the template grants that principal the data-plane role the upload needs, so the deployment is self-sufficient; leave it empty and the role has to be assigned by hand once. It is an object id, not an application (client) id — for a federated app registration that is the enterprise application\'s object id.')
param deployerPrincipalId string = ''

@description('Front Door tier. Standard is sufficient: the rules engine, managed certificates and caching are all in Standard. Premium adds private origins and the managed WAF, neither of which a public static site needs.')
@allowed(['Standard_AzureFrontDoor', 'Premium_AzureFrontDoor'])
param frontDoorSku string = 'Standard_AzureFrontDoor'

// A storage account name has no separators and a 24-character ceiling, so it is
// built from a hash rather than from the parts — two resource groups with the
// same prefix would otherwise produce the same globally unique name and the
// second deployment would fail on a name someone else owns.
var storageAccountName = toLower('${namePrefix}${environmentName}${substring(uniqueString(resourceGroup().id), 0, 6)}')
var profileName = '${namePrefix}-${environmentName}-afd'
var endpointName = '${namePrefix}-${environmentName}'

// -----------------------------------------------------------------------------
// Origin: the storage account's static website
// -----------------------------------------------------------------------------

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  sku: {
    // The site is a few hundred kilobytes of immutable, rebuildable assets.
    // Zone-redundant storage would pay for durability that a re-run of the
    // deploy workflow already provides.
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    // Static website hosting reads $web anonymously; without this the account
    // would accept the upload and answer every request with 409.
    allowBlobPublicAccess: true
    allowSharedKeyAccess: true
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    accessTier: 'Hot'
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      bypass: 'AzureServices'
      defaultAction: 'Allow'
    }
  }
}

// Static website hosting itself is a DATA-plane setting on the blob service and
// is not expressible in ARM, so it cannot be turned on from this file. The
// deploy workflow enables it immediately after this deployment:
//
//   az storage blob service-properties update --account-name <name> \
//     --static-website --index-document index.html --404-document index.html
//
// The 404 document stays index.html as a second line of defence: if the Front
// Door rewrite below is ever removed, deep links keep working — badly, under a
// 404 — rather than showing the operator a storage error page.

// primaryEndpoints.web is published whether or not static website hosting is
// enabled, so the origin host below is known at deployment time.
var webOriginHost = replace(replace(storage.properties.primaryEndpoints.web, 'https://', ''), '/', '')

// The upload runs with --auth-mode login rather than an account key, so the
// workflow's identity needs a data-plane role: control-plane Contributor does
// not grant blob access. Storage Blob Data Contributor is the least role that
// can write into $web.
//
// The name is a deterministic GUID over the scope, principal and role, which is
// what makes re-running the deployment idempotent — a random name would create
// a second identical assignment on every run.
var blobDataContributor = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')

resource uploadRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId)) {
  name: guid(storage.id, deployerPrincipalId, blobDataContributor)
  scope: storage
  properties: {
    roleDefinitionId: blobDataContributor
    principalId: deployerPrincipalId
    // Omitting this makes the assignment fail transiently for a brand-new
    // service principal that has not yet replicated across Entra ID.
    principalType: 'ServicePrincipal'
  }
}

// -----------------------------------------------------------------------------
// Front Door
// -----------------------------------------------------------------------------

resource profile 'Microsoft.Cdn/profiles@2024-02-01' = {
  name: profileName
  location: 'global'
  sku: {
    name: frontDoorSku
  }
}

resource endpoint 'Microsoft.Cdn/profiles/afdEndpoints@2024-02-01' = {
  parent: profile
  name: endpointName
  location: 'global'
  properties: {
    enabledState: 'Enabled'
  }
}

resource originGroup 'Microsoft.Cdn/profiles/originGroups@2024-02-01' = {
  parent: profile
  name: 'web-origin-group'
  properties: {
    loadBalancingSettings: {
      sampleSize: 4
      successfulSamplesRequired: 3
      additionalLatencyInMilliseconds: 50
    }
    healthProbeSettings: {
      // HEAD against the root, not GET: the probe only needs to know the origin
      // answers, and a GET would pull the document on every probe from every
      // edge.
      probePath: '/'
      probeRequestType: 'HEAD'
      probeProtocol: 'Https'
      probeIntervalInSeconds: 120
    }
    sessionAffinityState: 'Disabled'
  }
}

resource origin 'Microsoft.Cdn/profiles/originGroups/origins@2024-02-01' = {
  parent: originGroup
  name: 'blob-static-website'
  properties: {
    hostName: webOriginHost
    httpPort: 80
    httpsPort: 443
    // The origin host header must be the storage hostname, not the Front Door
    // one. Forwarding the incoming host would make the account look for a
    // container named after the front door and answer 404 for everything.
    originHostHeader: webOriginHost
    priority: 1
    weight: 1000
    enabledState: 'Enabled'
    enforceCertificateNameCheck: true
  }
}

// -----------------------------------------------------------------------------
// Rules: the SPA rewrite and the two cache policies
// -----------------------------------------------------------------------------

resource ruleSet 'Microsoft.Cdn/profiles/ruleSets@2024-02-01' = {
  parent: profile
  name: 'spaRules'
}

// The rewrite. UrlFileExtension <= 0 is the condition that distinguishes a
// route from a file: /app/schedule has no extension and is rewritten to
// /index.html, while /main-A1B2C3.js and /assets/config.js keep their paths and
// are served as themselves.
//
// The order matters. This rule runs first and does NOT stop, because the cache
// rules below must still see the request — a rewritten request is still a
// request for index.html and must still be told not to cache.
resource rewriteRule 'Microsoft.Cdn/profiles/ruleSets/rules@2024-02-01' = {
  parent: ruleSet
  name: 'spaDeepLinkRewrite'
  properties: {
    order: 1
    matchProcessingBehavior: 'Continue'
    conditions: [
      {
        name: 'UrlFileExtension'
        parameters: {
          typeName: 'DeliveryRuleUrlFileExtensionMatchConditionParameters'
          operator: 'LessThanOrEqual'
          negateCondition: false
          matchValues: ['0']
          transforms: []
        }
      }
    ]
    actions: [
      {
        name: 'UrlRewrite'
        parameters: {
          typeName: 'DeliveryRuleUrlRewriteActionParameters'
          sourcePattern: '/'
          destination: '/index.html'
          // False, deliberately: preserving the unmatched path would append the
          // route to the destination and ask the origin for
          // /index.htmlapp/schedule.
          preserveUnmatchedPath: false
        }
      }
    ]
  }
}

// index.html and assets/config.js are the two files that are NOT fingerprinted,
// and they are the two that decide which bundle and which API the browser uses.
// A cached index.html keeps serving script tags for a bundle that has been
// replaced; a cached config.js keeps pointing at yesterday's API. Both must
// revalidate on every request, and Front Door's cache-override is the only way
// to be sure of that at the edge as well as in the browser.
resource noCacheRule 'Microsoft.Cdn/profiles/ruleSets/rules@2024-02-01' = {
  parent: ruleSet
  name: 'noCacheEntrypoints'
  dependsOn: [rewriteRule]
  properties: {
    order: 2
    matchProcessingBehavior: 'Stop'
    conditions: [
      {
        name: 'UrlPath'
        parameters: {
          typeName: 'DeliveryRuleUrlPathMatchConditionParameters'
          operator: 'Equal'
          negateCondition: false
          matchValues: ['/index.html', '/assets/config.js']
          transforms: ['Lowercase']
        }
      }
    ]
    actions: [
      {
        name: 'RouteConfigurationOverride'
        parameters: {
          typeName: 'DeliveryRuleRouteConfigurationOverrideActionParameters'
          cacheConfiguration: {
            cacheBehavior: 'BypassCache'
            isCompressionEnabled: 'Enabled'
            queryStringCachingBehavior: 'IgnoreQueryString'
          }
        }
      }
      {
        name: 'ModifyResponseHeader'
        parameters: {
          typeName: 'DeliveryRuleHeaderActionParameters'
          headerAction: 'Overwrite'
          headerName: 'Cache-Control'
          value: 'no-cache, no-store, must-revalidate'
        }
      }
    ]
  }
}

// Everything else out of the Angular build carries a content hash in its name,
// so a changed file is a changed URL and a year is a safe lifetime. This rule
// exists because the blob-level Cache-Control set by the workflow governs the
// browser, and this governs the edge.
resource immutableAssetRule 'Microsoft.Cdn/profiles/ruleSets/rules@2024-02-01' = {
  parent: ruleSet
  name: 'cacheFingerprintedAssets'
  dependsOn: [noCacheRule]
  properties: {
    order: 3
    matchProcessingBehavior: 'Stop'
    conditions: [
      {
        name: 'UrlFileExtension'
        parameters: {
          typeName: 'DeliveryRuleUrlFileExtensionMatchConditionParameters'
          operator: 'Equal'
          negateCondition: false
          matchValues: ['js', 'css', 'woff2', 'woff', 'svg', 'png', 'jpg', 'webp', 'ico']
          transforms: ['Lowercase']
        }
      }
    ]
    actions: [
      {
        name: 'RouteConfigurationOverride'
        parameters: {
          typeName: 'DeliveryRuleRouteConfigurationOverrideActionParameters'
          cacheConfiguration: {
            cacheBehavior: 'OverrideAlways'
            cacheDuration: '365.00:00:00'
            isCompressionEnabled: 'Enabled'
            queryStringCachingBehavior: 'IgnoreQueryString'
          }
        }
      }
    ]
  }
}

// -----------------------------------------------------------------------------
// Route
// -----------------------------------------------------------------------------

resource route 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = {
  parent: endpoint
  name: 'spa'
  dependsOn: [origin]
  properties: {
    originGroup: {
      id: originGroup.id
    }
    ruleSets: [
      {
        id: ruleSet.id
      }
    ]
    // /* rather than a narrower set: every path the router owns has to reach
    // this route to be rewritten. When the API is added it takes /api/* on a
    // more specific pattern, which Front Door matches ahead of this one.
    patternsToMatch: ['/*']
    supportedProtocols: ['Http', 'Https']
    forwardingProtocol: 'HttpsOnly'
    httpsRedirect: 'Enabled'
    linkToDefaultDomain: 'Enabled'
    enabledState: 'Enabled'
    cacheConfiguration: {
      queryStringCachingBehavior: 'IgnoreQueryString'
      compressionSettings: {
        isCompressionEnabled: true
        contentTypesToCompress: [
          'text/html'
          'text/css'
          'text/javascript'
          'application/javascript'
          'application/json'
          'image/svg+xml'
          'font/woff'
          'font/woff2'
        ]
      }
    }
  }
}

// -----------------------------------------------------------------------------
// Outputs — consumed by the deploy workflow, which is why they are named for
// the CLI arguments they become rather than for the resources they describe.
// -----------------------------------------------------------------------------

@description('--account-name for the upload and for enabling static website hosting.')
output storageAccountName string = storage.name

@description('The origin. Useful for confirming a deep link returns 404 here and 200 through Front Door.')
output storageWebEndpoint string = storage.properties.primaryEndpoints.web

@description('--profile-name for the cache purge.')
output frontDoorProfileName string = profile.name

@description('--endpoint-name for the cache purge.')
output frontDoorEndpointName string = endpoint.name

@description('The public address of the site.')
output siteUrl string = 'https://${endpoint.properties.hostName}'
