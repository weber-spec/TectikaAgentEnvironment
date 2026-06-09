// ============================================================================
//  All role assignments (keyless). Names are deterministic GUIDs so re-running
//  never creates duplicates. principalType=ServicePrincipal avoids AAD-propagation
//  errors for freshly-created managed identities.
// ============================================================================
param apiMiPrincipalId string
param workflowsMiPrincipalId string
param webMiPrincipalId string
param acrName string
param cosmosName string
param serviceBusName string
param keyVaultName string
param storageName string
param foundryAccountName string
param foundryProjectName string

// ── Built-in role definition ids ─────────────────────────────────────────────
var roles = {
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  serviceBusDataOwner: '090c5cfd-751d-490a-894a-3ce6f1109419'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  cognitiveServicesOpenAIUser: '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
  azureAIDeveloper: '64702f94-c441-49e6-a78b-ef80e0188fee'
  storageBlobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  storageQueueDataContributor: '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  storageTableDataContributor: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
}
var cosmosDataContributorDefId = '00000000-0000-0000-0000-000000000002'

// Identities that talk to data plane + model (API + Workflows).
var aiPrincipals = [ apiMiPrincipalId, workflowsMiPrincipalId ]

// ── Existing resource references ──────────────────────────────────────────────
resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' existing = { name: acrName }
resource sb 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = { name: serviceBusName }
resource kv 'Microsoft.KeyVault/vaults@2023-07-01' existing = { name: keyVaultName }
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = { name: storageName }
resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' existing = { name: cosmosName }
resource foundry 'Microsoft.CognitiveServices/accounts@2025-04-01-preview' existing = { name: foundryAccountName }
resource foundryProject 'Microsoft.CognitiveServices/accounts/projects@2025-04-01-preview' existing = {
  parent: foundry
  name: foundryProjectName
}

// ── AcrPull: api, workflows, web ──────────────────────────────────────────────
resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for p in [ apiMiPrincipalId, workflowsMiPrincipalId, webMiPrincipalId ]: {
    name: guid(acr.id, p, roles.acrPull)
    scope: acr
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
      principalId: p
      principalType: 'ServicePrincipal'
    }
  }
]

// ── Service Bus Data Owner: api, workflows ───────────────────────────────────
resource sbOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for p in aiPrincipals: {
    name: guid(sb.id, p, roles.serviceBusDataOwner)
    scope: sb
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataOwner)
      principalId: p
      principalType: 'ServicePrincipal'
    }
  }
]

// ── Key Vault Secrets User: api, workflows ───────────────────────────────────
resource kvUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for p in aiPrincipals: {
    name: guid(kv.id, p, roles.keyVaultSecretsUser)
    scope: kv
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
      principalId: p
      principalType: 'ServicePrincipal'
    }
  }
]

// ── Cognitive Services OpenAI User (account): api, workflows ─────────────────
resource openAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for p in aiPrincipals: {
    name: guid(foundry.id, p, roles.cognitiveServicesOpenAIUser)
    scope: foundry
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.cognitiveServicesOpenAIUser)
      principalId: p
      principalType: 'ServicePrincipal'
    }
  }
]

// ── Azure AI Developer (project, Agent Service data plane): api, workflows ───
resource aiDeveloper 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for p in aiPrincipals: {
    name: guid(foundryProject.id, p, roles.azureAIDeveloper)
    scope: foundryProject
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.azureAIDeveloper)
      principalId: p
      principalType: 'ServicePrincipal'
    }
  }
]

// ── Storage (Functions host, identity-based): workflows only ─────────────────
resource storageBlob 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, workflowsMiPrincipalId, roles.storageBlobDataOwner)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataOwner)
    principalId: workflowsMiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource storageQueue 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, workflowsMiPrincipalId, roles.storageQueueDataContributor)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageQueueDataContributor)
    principalId: workflowsMiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource storageTable 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, workflowsMiPrincipalId, roles.storageTableDataContributor)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageTableDataContributor)
    principalId: workflowsMiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// ── Cosmos DB data-plane (SQL role assignment, NOT ARM RBAC): api, workflows ─
resource cosmosData 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = [
  for p in aiPrincipals: {
    parent: cosmos
    name: guid(cosmos.id, p, cosmosDataContributorDefId)
    properties: {
      roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosDataContributorDefId}'
      principalId: p
      scope: cosmos.id
    }
  }
]
