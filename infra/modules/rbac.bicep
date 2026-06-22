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

// ── Built-in role definition ids ─────────────────────────────────────────────
var roles = {
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  serviceBusDataOwner: '090c5cfd-751d-490a-894a-3ce6f1109419'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  // Contributor on the RG — lets the workflows identity create/delete ACI container groups.
  contributor: 'b24988ac-6180-42a0-ab88-20f7382dd24c'
  // "Azure AI User" (surfaced as "Foundry User" in some tenants). dataActions = Microsoft.CognitiveServices/*
  // → the Foundry data-plane role: Agent Service (accounts/AIServices/agents/* — create/update/delete agents,
  // threads, runs) AND OpenAI inference. Replaces the prior OpenAI-User + AI-Developer pair, which lacked
  // the agents/write data action (caused 401 PermissionDenied on POST /assistants).
  foundryUser: '53ca6127-db72-4b80-b1b0-d745d6d5456d'
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

// ── Foundry data plane — "Azure AI User"/"Foundry User", account scope: api, workflows ──
// Grants Microsoft.CognitiveServices/* incl. accounts/AIServices/agents/write (agent create/
// update/delete) + threads/runs + OpenAI inference. Account scope covers the child project.
resource foundryUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for p in aiPrincipals: {
    name: guid(foundry.id, p, roles.foundryUser)
    scope: foundry
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.foundryUser)
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

// ── Contributor on RG: workflows (for ACI create/delete) ─────────────────────
// Scoped to RG rather than subscription — least-privilege for container management.
resource aciContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, workflowsMiPrincipalId, roles.contributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.contributor)
    principalId: workflowsMiPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// ── Contributor on RG: api (for live-preview ACI create/delete) ──────────────
// The API process provisions/destroys preview Container Instances; mirrors the
// workflows grant above (RG-scoped Contributor) so re-deploy is idempotent.
resource apiAciContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, apiMiPrincipalId, roles.contributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.contributor)
    principalId: apiMiPrincipalId
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
