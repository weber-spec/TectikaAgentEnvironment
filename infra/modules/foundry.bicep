// ============================================================================
//  Azure AI Foundry — account (kind=AIServices) + project + model deployment.
//  Basic Agent Service setup (Microsoft-managed thread/file state).
//  Keyless: local auth disabled; callers use managed identity + RBAC.
// ============================================================================
param namePrefix string
@description('Optional global-uniqueness suffix; empty = bare names.')
param nameSuffix string = ''
param location string
param modelName string
param modelVersion string
param modelCapacity int

var sfx = empty(nameSuffix) ? '' : '-${nameSuffix}'
// Custom subdomain must be globally unique (required for AAD/token auth). uniqueString
// keeps the data-plane endpoint globally unique regardless of nameSuffix.
var customSubDomain = toLower(take('aif-${replace(namePrefix, '-', '')}-${uniqueString(resourceGroup().id)}', 63))

resource account 'Microsoft.CognitiveServices/accounts@2025-04-01-preview' = {
  name: 'aif-${namePrefix}${sfx}'
  location: location
  kind: 'AIServices'
  sku: { name: 'S0' }
  identity: { type: 'SystemAssigned' }
  properties: {
    customSubDomainName: customSubDomain
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true // keyless — no API keys
    allowProjectManagement: true // enables Foundry projects / Agent Service
  }
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-04-01-preview' = {
  parent: account
  name: 'proj-${namePrefix}${sfx}'
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    displayName: '${namePrefix} agents'
    description: 'TectikaAgents Foundry project (Agent Service, basic setup)'
  }
}

// Model deployment lives on the account; nested under project for ordering.
resource deployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: account
  name: modelName
  // Standard (regional) deployment: this subscription has Standard gpt-4o quota in
  // Sweden Central but zero GlobalStandard quota. Account region (foundryLocation)
  // must be one with Standard quota for `modelName`.
  sku: { name: 'Standard', capacity: modelCapacity }
  properties: {
    model: { format: 'OpenAI', name: modelName, version: modelVersion }
    versionUpgradeOption: 'OnceCurrentVersionExpired'
  }
  dependsOn: [ project ]
}

output accountName string = account.name
output projectName string = project.name
// Azure OpenAI data-plane endpoint the app appends `/openai/deployments/...` to.
output endpoint string = 'https://${customSubDomain}.openai.azure.com/'
// Agent Service (data-plane) project endpoint for Azure.AI.Agents.Persistent.
output projectEndpoint string = 'https://${customSubDomain}.services.ai.azure.com/api/projects/${project.name}'
