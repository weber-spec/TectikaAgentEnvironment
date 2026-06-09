// Log Analytics workspace + Application Insights (workspace-based).
param namePrefix string
param location string

resource law 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'law-${namePrefix}'
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'ai-${namePrefix}'
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
