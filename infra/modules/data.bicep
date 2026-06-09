// Stateful resources: Cosmos DB (serverless), Service Bus (Standard), Storage, Key Vault.
param namePrefix string
param location string

var suffix = uniqueString(resourceGroup().id)
var databaseName = 'tectikaagents'
var functionDeployContainerName = 'function-releases'

// ── Cosmos DB (serverless) ────────────────────────────────────────────────────
resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = {
  name: toLower(take('cosmos-${namePrefix}-${suffix}', 44))
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    consistencyPolicy: { defaultConsistencyLevel: 'Session' }
    capabilities: [ { name: 'EnableServerless' } ]
    locations: [ { locationName: location, failoverPriority: 0, isZoneRedundant: false } ]
    disableLocalAuth: true // keyless: data plane via AAD/RBAC only
  }
}

resource cosmosDb 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' = {
  parent: cosmos
  name: databaseName
  properties: { resource: { id: databaseName } }
}

var containers = [
  { name: 'tasks', pk: '/id' }
  { name: 'agents', pk: '/id' }
  { name: 'runs', pk: '/taskId' }
  { name: 'approvals', pk: '/runId' }
  { name: 'audit', pk: '/runId' }
]

resource cosmosContainers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = [
  for c in containers: {
    parent: cosmosDb
    name: c.name
    properties: {
      resource: {
        id: c.name
        partitionKey: { paths: [ c.pk ], kind: 'Hash' }
      }
    }
  }
]

// ── Service Bus (Standard — Topics require Standard) ─────────────────────────
resource sb 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: toLower(take('sb-${namePrefix}-${suffix}', 50))
  location: location
  sku: { name: 'Standard', tier: 'Standard' }
}

resource sbTopic 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: sb
  name: 'agent-events'
  properties: { defaultMessageTimeToLive: 'P14D' }
}

resource sbSub 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  parent: sbTopic
  name: 'api-sub'
}

resource sbQueueTrigger 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: sb
  name: 'task-trigger'
}

resource sbQueueApprovals 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: sb
  name: 'approvals'
}

// ── Storage (Durable Functions / Function App host) ──────────────────────────
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: toLower(take('st${replace(namePrefix, '-', '')}${suffix}', 24))
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    allowSharedKeyAccess: true // Functions host still uses this path on some operations
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource functionDeployContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: functionDeployContainerName
}

// ── Key Vault (RBAC authorization) ───────────────────────────────────────────
resource kv 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: toLower(take('kv-${namePrefix}-${suffix}', 24))
  location: location
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
  }
}

output cosmosName string = cosmos.name
output cosmosEndpoint string = cosmos.properties.documentEndpoint
output cosmosDatabaseName string = databaseName
output serviceBusName string = sb.name
output serviceBusFqdn string = '${sb.name}.servicebus.windows.net'
output storageName string = storage.name
output functionDeployContainer string = functionDeployContainerName
output keyVaultName string = kv.name
output keyVaultUri string = kv.properties.vaultUri
