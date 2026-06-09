// Azure Container Registry. Admin disabled — pulls are via managed identity + AcrPull.
param namePrefix string
param location string

@description('Globally-unique ACR name (alphanumeric only, derived deterministically).')
param acrName string = toLower(take('acr${replace(namePrefix, '-', '')}${uniqueString(resourceGroup().id)}', 50))

resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: acrName
  location: location
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
  }
}

output id string = acr.id
output name string = acr.name
output loginServer string = acr.properties.loginServer
