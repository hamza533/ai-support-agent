// Brightline AI Support Agent – Azure production profile.
// Validated in CI (az bicep build + lint); deployed only by the disabled-by-default workflow or manually.
// Container Apps (web + worker + legacy stand-in + migration jobs), PostgreSQL Flexible Server with pgvector,
// Azure OpenAI, Key Vault, a user-assigned managed identity (no secrets in app settings), Application Insights
// and Blob Storage for uploads.
targetScope = 'resourceGroup'

@description('Short name used as a prefix for every resource (lowercase letters and digits).')
@minLength(3)
@maxLength(12)
param namePrefix string = 'brightline'

param location string = resourceGroup().location

@description('Container images (e.g. ghcr.io/<org>/brightline-web:1.0.0).')
param webImage string
param legacyImage string

@description('Postgres administrator login (password stored only in Key Vault).')
param postgresAdminLogin string = 'brightlineadmin'

@secure()
@description('Postgres administrator password.')
param postgresAdminPassword string

@secure()
@description('Shared key between the web app and the legacy Orders API.')
param legacyApiKey string

@description('Azure OpenAI model deployments.')
param chatModel string = 'gpt-4.1-mini'
param chatModelVersion string = '2025-04-14'
param embeddingModel string = 'text-embedding-3-small'
param embeddingModelVersion string = '1'

@description('Entra ID app registration for staff sign-in (optional).')
param entraTenantId string = ''
param entraClientId string = ''

@description('Minimum web replicas (1 keeps Blazor circuits warm; scale out with session affinity).')
@minValue(1)
param webMinReplicas int = 1

param tags object = {
  app: 'brightline-ai-support-agent'
  environment: 'production'
}

var suffix = uniqueString(resourceGroup().id, namePrefix)
var names = {
  identity: '${namePrefix}-id-${suffix}'
  logs: '${namePrefix}-logs-${suffix}'
  insights: '${namePrefix}-ai-${suffix}'
  keyVault: take('${namePrefix}kv${suffix}', 24)
  storage: take('${namePrefix}st${suffix}', 24)
  postgres: '${namePrefix}-pg-${suffix}'
  openai: '${namePrefix}-oai-${suffix}'
  env: '${namePrefix}-env-${suffix}'
}

// Built-in role definition ids
var roles = {
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  cognitiveServicesOpenAIUser: '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
}

// ---------- identity & observability ----------
resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: names.identity
  location: location
  tags: tags
}

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: names.logs
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: names.insights
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

// ---------- secrets ----------
resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: names.keyVault
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    enablePurgeProtection: true
    softDeleteRetentionInDays: 90
  }
}

resource kvRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, identity.id, roles.keyVaultSecretsUser)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

var pgHost = '${names.postgres}.postgres.database.azure.com'
var appConnection = 'Host=${pgHost};Database=brightline_app;Username=${postgresAdminLogin};Password=${postgresAdminPassword};SSL Mode=Require'
var legacyConnection = 'Host=${pgHost};Database=brightline_legacy;Username=${postgresAdminLogin};Password=${postgresAdminPassword};SSL Mode=Require'

resource secretAppDb 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'app-db-connection'
  properties: { value: appConnection }
}

resource secretLegacyDb 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'legacy-db-connection'
  properties: { value: legacyConnection }
}

resource secretLegacyKey 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'legacy-api-key'
  properties: { value: legacyApiKey }
}

resource secretInsights 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: keyVault
  name: 'appinsights-connection'
  properties: { value: insights.properties.ConnectionString }
}

// ---------- data ----------
resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: names.postgres
  location: location
  tags: tags
  sku: {
    name: 'Standard_B2s'
    tier: 'Burstable'
  }
  properties: {
    version: '16'
    administratorLogin: postgresAdminLogin
    administratorLoginPassword: postgresAdminPassword
    storage: { storageSizeGB: 32, autoGrow: 'Enabled' }
    backup: { backupRetentionDays: 14, geoRedundantBackup: 'Disabled' }
    highAvailability: { mode: 'Disabled' }
    authConfig: { activeDirectoryAuth: 'Enabled', passwordAuth: 'Enabled', tenantId: subscription().tenantId }
  }
}

resource pgExtensions 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2024-08-01' = {
  parent: postgres
  name: 'azure.extensions'
  properties: {
    value: 'VECTOR'
    source: 'user-override'
  }
}

resource pgAppDb 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: 'brightline_app'
  properties: { charset: 'UTF8', collation: 'en_US.utf8' }
}

resource pgLegacyDb 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: 'brightline_legacy'
  properties: { charset: 'UTF8', collation: 'en_US.utf8' }
}

resource pgAllowAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: postgres
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: names.storage
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_ZRS' }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource uploads 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'uploads'
  properties: { publicAccess: 'None' }
}

resource storageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, identity.id, roles.storageBlobDataContributor)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataContributor)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ---------- AI ----------
resource openai 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: names.openai
  location: location
  tags: tags
  kind: 'OpenAI'
  sku: { name: 'S0' }
  properties: {
    customSubDomainName: names.openai
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true // Entra ID (managed identity) only – no API keys
  }
}

resource chatDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openai
  name: chatModel
  sku: { name: 'GlobalStandard', capacity: 50 }
  properties: {
    model: { format: 'OpenAI', name: chatModel, version: chatModelVersion }
  }
}

resource embeddingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openai
  name: embeddingModel
  sku: { name: 'Standard', capacity: 50 }
  properties: {
    model: { format: 'OpenAI', name: embeddingModel, version: embeddingModelVersion }
  }
  dependsOn: [chatDeployment]
}

resource openaiRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openai.id, identity.id, roles.cognitiveServicesOpenAIUser)
  scope: openai
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.cognitiveServicesOpenAIUser)
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// ---------- compute ----------
resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: names.env
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

var identityConfig = {
  type: 'UserAssigned'
  userAssignedIdentities: { '${identity.id}': {} }
}

// Secrets resolved from Key Vault at runtime with the managed identity – nothing secret in app settings.
var kvSecrets = [
  { name: 'app-db', keyVaultUrl: secretAppDb.properties.secretUri, identity: identity.id }
  { name: 'legacy-db', keyVaultUrl: secretLegacyDb.properties.secretUri, identity: identity.id }
  { name: 'legacy-key', keyVaultUrl: secretLegacyKey.properties.secretUri, identity: identity.id }
  { name: 'appinsights', keyVaultUrl: secretInsights.properties.secretUri, identity: identity.id }
]

var commonEnv = [
  { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
  { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', secretRef: 'appinsights' }
]

var webEnv = concat(commonEnv, [
  { name: 'ConnectionStrings__App', secretRef: 'app-db' }
  { name: 'LegacyOrders__BaseUrl', value: 'https://${legacy.properties.configuration.ingress.fqdn}' }
  { name: 'LegacyOrders__ApiKey', secretRef: 'legacy-key' }
  { name: 'Ai__Provider', value: 'azure-openai' }
  { name: 'Ai__AzureOpenAI__Endpoint', value: openai.properties.endpoint }
  { name: 'Ai__ChatModel', value: chatDeployment.name }
  { name: 'Ai__EmbeddingModel', value: embeddingDeployment.name }
  { name: 'Ai__EmbeddingDimensions', value: '768' }
  { name: 'Auth__EntraId__Enabled', value: empty(entraClientId) ? 'false' : 'true' }
  { name: 'Auth__EntraId__TenantId', value: entraTenantId }
  { name: 'Auth__EntraId__ClientId', value: entraClientId }
  { name: 'Database__MigrateOnStartup', value: 'false' }
  { name: 'Demo__Enabled', value: 'false' }
])

resource legacy 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${namePrefix}-legacy'
  location: location
  tags: tags
  identity: identityConfig
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      ingress: { external: false, targetPort: 8080, transport: 'http' }
      secrets: kvSecrets
    }
    template: {
      containers: [
        {
          name: 'legacy-api'
          image: legacyImage
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: concat(commonEnv, [
            { name: 'ConnectionStrings__LegacyOrders', secretRef: 'legacy-db' }
            { name: 'LegacyOrders__ApiKey', secretRef: 'legacy-key' }
          ])
          probes: [
            { type: 'Liveness', httpGet: { path: '/health', port: 8080 } }
            { type: 'Readiness', httpGet: { path: '/ready', port: 8080 } }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
  dependsOn: [kvRole]
}

resource web 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${namePrefix}-web'
  location: location
  tags: tags
  identity: identityConfig
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto' // WebSockets for Blazor Server
        allowInsecure: false
        stickySessions: { affinity: 'sticky' } // Blazor circuits stay on one replica
      }
      secrets: kvSecrets
    }
    template: {
      containers: [
        {
          name: 'web'
          image: webImage
          resources: { cpu: json('1.0'), memory: '2Gi' }
          env: concat(webEnv, [{ name: 'Worker__Mode', value: 'separate' }])
          probes: [
            { type: 'Liveness', httpGet: { path: '/health', port: 8080 } }
            { type: 'Readiness', httpGet: { path: '/ready', port: 8080 } }
          ]
        }
      ]
      scale: {
        minReplicas: webMinReplicas
        maxReplicas: 10
        rules: [{ name: 'http', http: { metadata: { concurrentRequests: '100' } } }]
      }
    }
  }
  dependsOn: [kvRole, openaiRole, storageRole]
}

// Background jobs (ingestion, retention) in a dedicated worker: same image, `--worker`.
resource worker 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${namePrefix}-worker'
  location: location
  tags: tags
  identity: identityConfig
  properties: {
    managedEnvironmentId: env.id
    configuration: { secrets: kvSecrets }
    template: {
      containers: [
        {
          name: 'worker'
          image: webImage
          args: ['--worker']
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: concat(webEnv, [{ name: 'Worker__Mode', value: 'separate' }])
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 2 }
    }
  }
  dependsOn: [kvRole, openaiRole]
}

// Migrations run as a deploy step (never at app start-up in production).
resource migrateJob 'Microsoft.App/jobs@2024-03-01' = {
  name: '${namePrefix}-migrate'
  location: location
  tags: tags
  identity: identityConfig
  properties: {
    environmentId: env.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 900
      replicaRetryLimit: 1
      manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 }
      secrets: kvSecrets
    }
    template: {
      containers: [
        {
          name: 'migrate-app'
          image: webImage
          args: ['--migrate']
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: webEnv
        }
        {
          name: 'migrate-legacy'
          image: legacyImage
          args: ['--migrate', '--seed']
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: concat(commonEnv, [{ name: 'ConnectionStrings__LegacyOrders', secretRef: 'legacy-db' }])
        }
      ]
    }
  }
  dependsOn: [kvRole]
}

output webUrl string = 'https://${web.properties.configuration.ingress.fqdn}'
output keyVaultName string = keyVault.name
output openAiEndpoint string = openai.properties.endpoint
output migrationJob string = migrateJob.name
