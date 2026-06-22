// Stateful resources: Cosmos DB (serverless), Service Bus (Standard), Storage, Key Vault.
param namePrefix string
@description('Optional global-uniqueness suffix; empty = bare names (the convention this subscription uses).')
param nameSuffix string = ''
param location string
@description('Cosmos data/write region (immutable once created). Defaults to `location`; set to the live region when adopting an existing account.')
param cosmosLocation string = location

var sfx = empty(nameSuffix) ? '' : '-${nameSuffix}'          // for dash-separated names
var sfxAlnum = toLower(replace(nameSuffix, '-', ''))         // for alphanumeric-only names (storage)
var databaseName = 'tectikaagents'
var functionDeployContainerName = 'function-releases'

// ── Cosmos DB (serverless) ────────────────────────────────────────────────────
resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = {
  name: toLower(take('cosmos-${namePrefix}${sfx}', 44))
  location: location
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    consistencyPolicy: { defaultConsistencyLevel: 'Session' }
    capabilities: [ { name: 'EnableServerless' } ]
    locations: [ { locationName: cosmosLocation, failoverPriority: 0, isZoneRedundant: false } ]
    disableLocalAuth: true // keyless: data plane via AAD/RBAC only
  }
}

resource cosmosDb 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' = {
  parent: cosmos
  name: databaseName
  properties: { resource: { id: databaseName } }
}

// Partition keys are immutable and must match the live containers (the running app,
// CosmosDbService.ContainerDefinitions, is the source of truth). tasks is partitioned by /boardId.
var containers = [
  { name: 'boards', pk: '/tenantId' }
  { name: 'tasks', pk: '/boardId' }
  { name: 'agentRoles', pk: '/tenantId' }
  { name: 'workflowRuns', pk: '/taskId' }
  { name: 'artifacts', pk: '/taskId' }
  { name: 'humanInteractions', pk: '/runId' }
  { name: 'taskEdges', pk: '/boardId' }
  { name: 'runEvents', pk: '/taskId' }
  { name: 'pendingMessages', pk: '/runId' }
  { name: 'notifications', pk: '/tenantId' }
  { name: 'userSettings', pk: '/userId' }
  { name: 'usageEvents', pk: '/taskId' }
  { name: 'usageRollups', pk: '/tenantId' }
  { name: 'previewSessions', pk: '/boardId' }
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
  name: toLower(take('sb-${namePrefix}${sfx}', 50))
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

// ── Storage (Durable Functions / Function App host) ──────────────────────────
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: toLower(take('st${replace(namePrefix, '-', '')}flows${sfxAlnum}', 24))
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
  name: toLower(take('kv-${namePrefix}${sfx}', 24))
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
