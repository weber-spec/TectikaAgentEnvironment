// ============================================================================
//  Container Apps Environment + API app + Web app.
//  Images are owned by CI; `apiImage`/`webImage` default to a placeholder and
//  deploy.ps1 passes the live image on re-runs so a redeploy never reverts code.
// ============================================================================
param namePrefix string
@description('Optional global-uniqueness suffix; empty = bare names.')
param nameSuffix string = ''
param location string
param lawCustomerId string
@secure()
param lawSharedKey string
param acrLoginServer string
param apiMiId string
param apiMiClientId string
param webMiId string
param apiImage string
param webImage string
param cosmosEndpoint string
param cosmosDatabaseName string
param serviceBusFqdn string
param keyVaultUri string
param appInsightsConnectionString string
param foundryEndpoint string
param foundryProjectName string
param foundryProjectEndpoint string
param modelName string
param functionAppUrl string
param workflowsFunctionKeySecretUri string
param tenantId string
param apiClientId string
param platformClientId string

var apiAudience = empty(apiClientId) ? '' : 'api://${apiClientId}'
var sfx = empty(nameSuffix) ? '' : '-${nameSuffix}'

resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${namePrefix}${sfx}'
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: lawCustomerId
        sharedKey: lawSharedKey
      }
    }
  }
}

resource apiApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-${namePrefix}-api${sfx}'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${apiMiId}': {} }
  }
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
      }
      registries: [
        { server: acrLoginServer, identity: apiMiId }
      ]
      secrets: [
        {
          name: 'workflows-function-key'
          keyVaultUrl: workflowsFunctionKeySecretUri
          identity: apiMiId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: apiImage
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'MockDatabase__Enabled', value: 'false' }
            { name: 'CosmosDb__AccountEndpoint', value: cosmosEndpoint }
            { name: 'CosmosDb__DatabaseName', value: cosmosDatabaseName }
            { name: 'ServiceBus__Namespace', value: serviceBusFqdn }
            { name: 'ServiceBus__AgentEventsTopic', value: 'agent-events' }
            { name: 'ServiceBus__TaskTriggerQueue', value: 'task-trigger' }
            { name: 'ServiceBus__ApprovalsQueue', value: 'approvals' }
            { name: 'KeyVault__VaultUri', value: keyVaultUri }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
            { name: 'AZURE_CLIENT_ID', value: apiMiClientId }
            { name: 'Foundry__Endpoint', value: foundryEndpoint }
            { name: 'Foundry__ProjectName', value: foundryProjectName }
            { name: 'Foundry__ProjectEndpoint', value: foundryProjectEndpoint }
            { name: 'Foundry__DefaultModel', value: modelName }
            { name: 'Foundry__IsOpenAiDirect', value: 'false' }
            { name: 'Foundry__ApiKey', value: '' }
            { name: 'DurableFunctions__StartUrl', value: '${functionAppUrl}/api/pipelines/start' }
            { name: 'DurableFunctions__FunctionKey', secretRef: 'workflows-function-key' }
            { name: 'AzureAd__Instance', value: environment().authentication.loginEndpoint }
            { name: 'AzureAd__TenantId', value: tenantId }
            { name: 'AzureAd__ClientId', value: apiClientId }
            { name: 'AzureAd__Audience', value: apiAudience }
            { name: 'AzureAd__PlatformClientId', value: platformClientId }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 5 }
    }
  }
}

resource webApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-${namePrefix}-web${sfx}'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${webMiId}': {} }
  }
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 3000
        transport: 'auto'
      }
      registries: [
        { server: acrLoginServer, identity: webMiId }
      ]
    }
    template: {
      containers: [
        {
          name: 'web'
          image: webImage
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'NODE_ENV', value: 'production' }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
}

output apiName string = apiApp.name
output webName string = webApp.name
output apiUrl string = 'https://${apiApp.properties.configuration.ingress.fqdn}'
output webUrl string = 'https://${webApp.properties.configuration.ingress.fqdn}'
