// User-assigned managed identities. Idempotent (PUT).
param namePrefix string
@description('Optional global-uniqueness suffix; empty = bare names.')
param nameSuffix string = ''
param location string

var sfx = empty(nameSuffix) ? '' : '-${nameSuffix}'

resource apiMi 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'mi-${namePrefix}-api${sfx}'
  location: location
}

resource workflowsMi 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'mi-${namePrefix}-workflows${sfx}'
  location: location
}

// New: web needs its own identity so the Container App can pull from a private ACR.
resource webMi 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'mi-${namePrefix}-web${sfx}'
  location: location
}

output apiMiId string = apiMi.id
output apiMiClientId string = apiMi.properties.clientId
output apiMiPrincipalId string = apiMi.properties.principalId

output workflowsMiId string = workflowsMi.id
output workflowsMiClientId string = workflowsMi.properties.clientId
output workflowsMiPrincipalId string = workflowsMi.properties.principalId

output webMiId string = webMi.id
output webMiClientId string = webMi.properties.clientId
output webMiPrincipalId string = webMi.properties.principalId
