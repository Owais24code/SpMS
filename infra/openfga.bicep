// -----------------------------------------------------------------------------
// OpenFGA for SpMS on Azure Container Apps.
//
// Deployed separately from main.bicep (the static front end), into the same
// resource group:
//
//   az deployment group create -g rg-spms-dev -f infra/openfga.bicep \
//     -p keyVaultName=<kv> datastoreSecretName=openfga-datastore-uri
//
// Decisions (see README, Authorization):
//   * Self-hosted, datastore = a separate `openfga` database on the same
//     PostgreSQL Flexible Server as the application (not the app database).
//   * Internal ingress only. The SpMS API is the only caller; OpenFGA is never
//     reachable from the internet.
//   * The datastore URI and the preshared API key live in Key Vault and are read
//     through the app's managed identity; nothing secret is in this template.
//   * Migrations run as a Container Apps job before the app revision starts.
// -----------------------------------------------------------------------------
targetScope = 'resourceGroup'

@description('Prefix for resource names.')
param namePrefix string = 'spms'

@description('Deployment environment name, e.g. dev, test, prod.')
param environmentName string = 'dev'

param location string = resourceGroup().location

@description('Pinned OpenFGA image. Upgrade deliberately: the migration job runs the same tag.')
param openfgaImage string = 'openfga/openfga:v1.10.2'

@description('Existing Key Vault holding the OpenFGA secrets.')
param keyVaultName string

@description('Key Vault secret: postgres://user:pass@server.postgres.database.azure.com:5432/openfga?sslmode=require')
param datastoreSecretName string = 'openfga-datastore-uri'

@description('Key Vault secret: preshared key the SpMS API presents to OpenFGA.')
param presharedKeySecretName string = 'openfga-preshared-key'

@description('Container Apps environment to join (api.bicep output environmentName). The API reaches OpenFGA over internal ingress, so both must share one environment. Empty = create one (OpenFGA on its own).')
param managedEnvironmentName string = ''

@minValue(1)
param minReplicas int = 1

@minValue(1)
param maxReplicas int = 3

var baseName = '${namePrefix}-${environmentName}-fga'

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = if (empty(managedEnvironmentName)) {
  name: '${baseName}-logs'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource sharedEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' existing = if (!empty(managedEnvironmentName)) {
  name: managedEnvironmentName
}

resource ownEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = if (empty(managedEnvironmentName)) {
  name: '${baseName}-env'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs!.properties.customerId
        sharedKey: logs!.listKeys().primarySharedKey
      }
    }
  }
}

var environmentId = empty(managedEnvironmentName) ? ownEnvironment.id : sharedEnvironment.id

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${baseName}-id'
  location: location
}

// Key Vault Secrets User on the vault, for the identity both the job and the app use.
var secretsUser = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')

resource secretsAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, identity.id, secretsUser)
  scope: keyVault
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: secretsUser
  }
}

var secrets = [
  {
    name: 'datastore-uri'
    keyVaultUrl: '${keyVault.properties.vaultUri}secrets/${datastoreSecretName}'
    identity: identity.id
  }
  {
    name: 'preshared-key'
    keyVaultUrl: '${keyVault.properties.vaultUri}secrets/${presharedKeySecretName}'
    identity: identity.id
  }
]

var datastoreEnv = [
  { name: 'OPENFGA_DATASTORE_ENGINE', value: 'postgres' }
  { name: 'OPENFGA_DATASTORE_URI', secretRef: 'datastore-uri' }
]

resource migrate 'Microsoft.App/jobs@2024-03-01' = {
  name: '${baseName}-migrate'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  dependsOn: [secretsAccess]
  properties: {
    environmentId: environmentId
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 600
      replicaRetryLimit: 1
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      secrets: secrets
    }
    template: {
      containers: [
        {
          name: 'migrate'
          image: openfgaImage
          args: ['migrate']
          env: datastoreEnv
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
        }
      ]
    }
  }
}

resource openfga 'Microsoft.App/containerApps@2024-03-01' = {
  name: baseName
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  dependsOn: [secretsAccess]
  properties: {
    managedEnvironmentId: environmentId
    configuration: {
      secrets: secrets
      ingress: {
        external: false          // reachable from inside the environment/VNet only
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: 'openfga'
          image: openfgaImage
          args: ['run']
          env: concat(datastoreEnv, [
            { name: 'OPENFGA_AUTHN_METHOD', value: 'preshared' }
            { name: 'OPENFGA_AUTHN_PRESHARED_KEYS', secretRef: 'preshared-key' }
            { name: 'OPENFGA_PLAYGROUND_ENABLED', value: 'false' }
            { name: 'OPENFGA_LOG_FORMAT', value: 'json' }
            { name: 'OPENFGA_METRICS_ENABLED', value: 'true' }
            { name: 'OPENFGA_DATASTORE_MAX_OPEN_CONNS', value: '30' }
          ])
          resources: { cpu: json('0.5'), memory: '1Gi' }
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/healthz', port: 8080 }
              periodSeconds: 10
            }
            {
              type: 'Readiness'
              httpGet: { path: '/healthz', port: 8080 }
              periodSeconds: 5
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas   // >= 1: authorization fails closed, so a cold start is an outage
        maxReplicas: maxReplicas
      }
    }
  }
}

@description('Internal URL the SpMS API uses as OpenFga:ApiUrl.')
output openfgaUrl string = 'https://${openfga.properties.configuration.ingress.fqdn}'

@description('Start this job (az containerapp job start) before rolling a new OpenFGA image.')
output migrateJobName string = migrate.name
