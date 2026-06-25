using 'main.bicep'

// Tenant-specific values — change `namePrefix` per tenant for clean, unique names.
param namePrefix = 'agentteam'
param location = 'westeurope'

// nameSuffix is appended to globally-unique resource names (ACR, storage, Cosmos,
// Key Vault, Service Bus, ...). Empty = bare names (this subscription's convention).
// Set a short token in another tenant if a bare name collides globally.
param nameSuffix = ''

// Foundry account + model region. Model quota is region-specific; this
// subscription has Standard quota in Sweden Central, not West Europe.
// NOTE: gpt-5 may require GlobalStandard quota and/or a different region —
// verify availability in Azure (Foundry → Quotas) before deploying.
param foundryLocation = 'swedencentral'

// Cosmos write region is immutable. The live account in this tenant sits in
// North Europe, so adopting it requires this exact value. (deploy.ps1 auto-detects
// the live region; this is only for manual `az deployment` runs.)
param cosmosLocation = 'northeurope'

// resourceGroupName defaults to 'rg-<namePrefix>-dev-001'
// tenantId        defaults to the deploying subscription's tenant

// Foundry model deployment
param modelName = 'gpt-5'
param modelVersion = '2025-08-07'
param modelCapacity = 30

// apiImage / webImage / apiClientId / platformClientId are supplied at deploy
// time by deploy.ps1 (live image on re-run, Entra client ids on pass 2).
