// Log Analytics workspace + Application Insights (workspace-based).
param namePrefix string
@description('Optional global-uniqueness suffix; empty = bare names.')
param nameSuffix string = ''
param location string

var sfx = empty(nameSuffix) ? '' : '-${nameSuffix}'

resource law 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'law-${namePrefix}${sfx}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'ai-${namePrefix}${sfx}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: law.id
  }
}

output lawId string = law.id
output lawCustomerId string = law.properties.customerId
@secure()
output lawSharedKey string = law.listKeys().primarySharedKey
output appInsightsConnectionString string = appInsights.properties.ConnectionString
