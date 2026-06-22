// ============================================================================
//  TectikaAgents — full infrastructure (subscription scope, idempotent)
// ----------------------------------------------------------------------------
//  Provisions the ENTIRE project on a new resource group. Re-running converges
//  (ARM PUT semantics) — it never errors on "already exists" and never reverts
//  CI-deployed container images (see `apiImage`/`webImage` params).
//
//  Deploy:  az deployment sub create -l <loc> -f main.bicep -p main.bicepparam
//  Non-ARM Entra actions live in entra.ps1 (run by deploy.ps1).
// ============================================================================
targetScope = 'subscription'

// ── Naming / location ───────────────────────────────────────────────────────
@description('Short prefix for all resource names. Pick a unique value per tenant.')
param namePrefix string = 'agentteam'

@description('Optional suffix appended to globally-unique resource names (ACR, storage, Cosmos, Key Vault, Service Bus, etc.) for multi-tenant uniqueness. Empty string = bare names (the convention this subscription uses). Set to a short token in another tenant to avoid global-name collisions.')
param nameSuffix string = ''

@description('Azure region for every resource.')
param location string = 'westeurope'

@description('Region for the Azure AI Foundry account + model deployment. Kept separate from `location` because gpt-4o quota is region-specific (this subscription has Standard gpt-4o quota in Sweden Central, none in West Europe).')
param foundryLocation string = 'swedencentral'

@description('Data region for the Cosmos DB account (the `locations` write region). Cosmos regions are immutable, so when adopting an existing account this must match its current region. Defaults to `location`; deploy.ps1 auto-detects the live value on re-runs.')
param cosmosLocation string = location

@description('Resource group to create / deploy into.')
param resourceGroupName string = 'rg-${namePrefix}-dev-001'

@description('Entra tenant id (for AzureAd config on the API).')
param tenantId string = subscription().tenantId

// ── Foundry model deployment ─────────────────────────────────────────────────
@description('Model name to deploy in the Foundry project.')
param modelName string = 'gpt-4o'

@description('Model version for the deployment.')
param modelVersion string = '2024-11-20'

@description('GlobalStandard capacity (TPM in thousands).')
param modelCapacity int = 30

// ── Container images (owned by CI; deploy.ps1 passes the live value to avoid
//    reverting a real image back to the placeholder on re-run) ────────────────
@description('API container image. Default is the bootstrap placeholder.')
param apiImage string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'

@description('Web container image. Default is the bootstrap placeholder.')
param webImage string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'

// ── Entra-derived values (empty on pass 1; filled by deploy.ps1 on pass 2) ────
@description('API app registration client id (from entra.ps1). Empty on first pass.')
param apiClientId string = ''

@description('Web/platform SPA app registration client id (from entra.ps1).')
param platformClientId string = ''

// ── Resource group ────────────────────────────────────────────────────────────
resource rg 'Microsoft.Resources/resourceGroups@2024-11-01' = {
  name: resourceGroupName
  location: location
}

// ── Modules ───────────────────────────────────────────────────────────────────
module identities 'modules/identities.bicep' = {
  scope: rg
  name: 'identities'
  params: { namePrefix: namePrefix, nameSuffix: nameSuffix, location: location }
}

module registry 'modules/registry.bicep' = {
  scope: rg
  name: 'registry'
  params: { namePrefix: namePrefix, nameSuffix: nameSuffix, location: location }
}

module observability 'modules/observability.bicep' = {
  scope: rg
  name: 'observability'
  params: { namePrefix: namePrefix, nameSuffix: nameSuffix, location: location }
}

module data 'modules/data.bicep' = {
  scope: rg
  name: 'data'
  params: { namePrefix: namePrefix, nameSuffix: nameSuffix, location: location, cosmosLocation: cosmosLocation }
}

module foundry 'modules/foundry.bicep' = {
  scope: rg
  name: 'foundry'
  params: {
    namePrefix: namePrefix
    nameSuffix: nameSuffix
    location: foundryLocation
    modelName: modelName
    modelVersion: modelVersion
    modelCapacity: modelCapacity
  }
}

module functionApp 'modules/functionapp.bicep' = {
  scope: rg
  name: 'functionApp'
  // Flex Consumption validates deployment-storage access at create time → ensure
  // the workflows identity already holds the storage roles (rbac) first.
  dependsOn: [ rbac ]
  params: {
    namePrefix: namePrefix
    nameSuffix: nameSuffix
    location: location
    miId: identities.outputs.workflowsMiId
    miClientId: identities.outputs.workflowsMiClientId
    storageName: data.outputs.storageName
    deploymentContainerName: data.outputs.functionDeployContainer
    appInsightsConnectionString: observability.outputs.appInsightsConnectionString
    cosmosEndpoint: data.outputs.cosmosEndpoint
    cosmosDatabaseName: data.outputs.cosmosDatabaseName
    serviceBusFqdn: data.outputs.serviceBusFqdn
    foundryEndpoint: foundry.outputs.endpoint
    foundryProjectName: foundry.outputs.projectName
    foundryProjectEndpoint: foundry.outputs.projectEndpoint
    modelName: modelName
  }
}

module containerApps 'modules/containerapps.bicep' = {
  scope: rg
  name: 'containerApps'
  params: {
    namePrefix: namePrefix
    nameSuffix: nameSuffix
    location: location
    lawCustomerId: observability.outputs.lawCustomerId
    lawSharedKey: observability.outputs.lawSharedKey
    acrLoginServer: registry.outputs.loginServer
    apiMiId: identities.outputs.apiMiId
    apiMiClientId: identities.outputs.apiMiClientId
    webMiId: identities.outputs.webMiId
    apiImage: apiImage
    webImage: webImage
    cosmosEndpoint: data.outputs.cosmosEndpoint
    cosmosDatabaseName: data.outputs.cosmosDatabaseName
    serviceBusFqdn: data.outputs.serviceBusFqdn
    keyVaultUri: data.outputs.keyVaultUri
    appInsightsConnectionString: observability.outputs.appInsightsConnectionString
    foundryEndpoint: foundry.outputs.endpoint
    foundryProjectName: foundry.outputs.projectName
    foundryProjectEndpoint: foundry.outputs.projectEndpoint
    modelName: modelName
    functionAppUrl: functionApp.outputs.defaultUrl
    workflowsFunctionKeySecretUri: '${data.outputs.keyVaultUri}secrets/workflows-function-key'
    workflowsManagementKeySecretUri: '${data.outputs.keyVaultUri}secrets/workflows-management-key'
    tenantId: tenantId
    apiClientId: apiClientId
    platformClientId: platformClientId
    previewResourceGroup: resourceGroupName
    // Preview ACI pulls the preview-runner image with the same UAMI the agent workspaces use.
    previewMiResourceId: identities.outputs.workflowsMiId
  }
}

module rbac 'modules/rbac.bicep' = {
  scope: rg
  name: 'rbac'
  params: {
    apiMiPrincipalId: identities.outputs.apiMiPrincipalId
    workflowsMiPrincipalId: identities.outputs.workflowsMiPrincipalId
    webMiPrincipalId: identities.outputs.webMiPrincipalId
    acrName: registry.outputs.name
    cosmosName: data.outputs.cosmosName
    serviceBusName: data.outputs.serviceBusName
    keyVaultName: data.outputs.keyVaultName
    storageName: data.outputs.storageName
    foundryAccountName: foundry.outputs.accountName
  }
}

// ── Outputs (consumed by deploy.ps1 → GitHub vars/secrets + entra.ps1) ─────────
output resourceGroupName string = rg.name
output location string = location
output acrLoginServer string = registry.outputs.loginServer
output acrName string = registry.outputs.name
output apiContainerAppName string = containerApps.outputs.apiName
output webContainerAppName string = containerApps.outputs.webName
output functionAppName string = functionApp.outputs.name
output apiUrl string = containerApps.outputs.apiUrl
output webUrl string = containerApps.outputs.webUrl
output foundryEndpoint string = foundry.outputs.endpoint
output foundryProjectName string = foundry.outputs.projectName
output foundryAccountName string = foundry.outputs.accountName
