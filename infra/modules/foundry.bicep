// ============================================================================
//  Azure AI Foundry — account (kind=AIServices) + project + model deployment.
//  Basic Agent Service setup (Microsoft-managed thread/file state).
//  Keyless: local auth disabled; callers use managed identity + RBAC.
// ============================================================================
param namePrefix string
param location string
param modelName string
param modelVersion string
param modelCapacity int

var suffix = uniqueString(resourceGroup().id)
// Custom subdomain must be globally unique (required for AAD/token auth).
var customSubDomain = toLower(take('aif-${replace(namePrefix, '-', '')}-${suffix}', 63))

resource account 'Microsoft.CognitiveServices/accounts@2025-04-01-preview' = {
  name: 'aif-${namePrefix}'
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
  name: 'proj-${namePrefix}'
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
  sku: { name: 'GlobalStandard', capacity: modelCapacity }
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
