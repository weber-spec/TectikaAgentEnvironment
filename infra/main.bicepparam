using 'main.bicep'

// Tenant-specific values — change `namePrefix` per tenant for clean, unique names.
param namePrefix = 'agentteam'
param location = 'westeurope'

// resourceGroupName defaults to 'rg-<namePrefix>-dev-001'
// tenantId        defaults to the deploying subscription's tenant

// Foundry model deployment
param modelName = 'gpt-4o'
param modelVersion = '2024-11-20'
param modelCapacity = 30

// apiImage / webImage / apiClientId / platformClientId are supplied at deploy
// time by deploy.ps1 (live image on re-run, Entra client ids on pass 2).
