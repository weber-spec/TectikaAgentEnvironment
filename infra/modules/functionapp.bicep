// ============================================================================
//  Workflows = dedicated Azure Function App (Linux, dotnet-isolated .NET 9)
//  on a Flex Consumption plan. Keyless: identity-based AzureWebJobsStorage and
//  deployment storage via the workflows user-assigned identity.
//  Code is shipped by CI (deploy-workflows.yml) — this only provisions the host.
// ============================================================================
param namePrefix string
@description('Optional global-uniqueness suffix; empty = bare names.')
param nameSuffix string = ''
param location string
param miId string
param miClientId string
param storageName string
param deploymentContainerName string
param appInsightsConnectionString string
param cosmosEndpoint string
param cosmosDatabaseName string
param serviceBusFqdn string
param foundryEndpoint string
param foundryProjectName string
param modelName string

var sfx = empty(nameSuffix) ? '' : '-${nameSuffix}'

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageName
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'plan-${namePrefix}-workflows${sfx}'
  location: location
  kind: 'functionapp'
  sku: { name: 'FC1', tier: 'FlexConsumption' }
  properties: { reserved: true }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: toLower(take('func-${namePrefix}-workflows${sfx}', 60))
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${miId}': {} }
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${storage.properties.primaryEndpoints.blob}${deploymentContainerName}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: miId
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: 100
        instanceMemoryMB: 2048
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '9.0'
      }
    }
    siteConfig: {
      appSettings: [
        // Identity-based host storage (keyless)
        { name: 'AzureWebJobsStorage__accountName', value: storageName }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'AzureWebJobsStorage__clientId', value: miClientId }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
        // App config
        { name: 'CosmosDb__AccountEndpoint', value: cosmosEndpoint }
        { name: 'CosmosDb__DatabaseName', value: cosmosDatabaseName }
        { name: 'ServiceBus__Namespace', value: serviceBusFqdn }
        { name: 'ServiceBus__AgentEventsTopic', value: 'agent-events' }
        { name: 'ServiceBus__TaskTriggerQueue', value: 'task-trigger' }
        { name: 'ServiceBus__ApprovalsQueue', value: 'approvals' }
        { name: 'Foundry__Endpoint', value: foundryEndpoint }
        { name: 'Foundry__ProjectName', value: foundryProjectName }
        { name: 'Foundry__DefaultModel', value: modelName }
        { name: 'Foundry__IsOpenAiDirect', value: 'false' }
        { name: 'Foundry__ApiKey', value: '' }
        // DefaultAzureCredential picks this user-assigned identity
        { name: 'AZURE_CLIENT_ID', value: miClientId }
      ]
    }
  }
}

output name string = functionApp.name
output defaultUrl string = 'https://${functionApp.properties.defaultHostName}'
