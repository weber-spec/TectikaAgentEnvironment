// Azure Container Registry. Admin disabled — pulls are via managed identity + AcrPull.
param namePrefix string
@description('Optional global-uniqueness suffix appended to the alphanumeric ACR name; empty = bare name.')
param nameSuffix string = ''
param location string

// Base 'tacr<prefix>' (alphanumeric only). Empty suffix yields the canonical bare name;
// set nameSuffix in another tenant for a globally-unique registry.
@description('Globally-unique ACR name (alphanumeric only).')
param acrName string = toLower(take('tacr${replace(namePrefix, '-', '')}${replace(nameSuffix, '-', '')}', 50))

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
