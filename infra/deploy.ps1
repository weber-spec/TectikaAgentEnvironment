<#
.SYNOPSIS
    TectikaAgents — Azure Infrastructure Deployment
.DESCRIPTION
    Run once to provision all Azure resources.
    Prerequisites: az cli logged in, correct subscription accessible.
    After running, copy the printed values into GitHub Secrets.
#>
param(
    [Parameter(Mandatory)][string]$SubscriptionId,
    [Parameter(Mandatory)][string]$TenantId,
    [Parameter(Mandatory)][string]$GitHubOrg,
    [Parameter(Mandatory)][string]$GitHubRepo
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Resource names ─────────────────────────────────────────────────────────────
$LOCATION          = "westeurope"
$RG                = "rg-agentteam-dev-001"
$ACR_NAME          = "tacragentteam"
$KV_NAME           = "kv-agentteam"
$COSMOS_ACCOUNT    = "cosmos-agentteam"
$SB_NAMESPACE      = "sb-agentteam"
$LOG_WORKSPACE     = "law-agentteam"
$AI_NAME           = "ai-agentteam"
$STORAGE_ACCOUNT   = "stagentteamflows"
$CAE_NAME          = "cae-agentteam"
$CA_API_NAME       = "ca-agentteam-api"
$CA_WORKFLOWS_NAME = "ca-agentteam-workflows"
$CA_WEB_NAME       = "ca-agentteam-web"
$MI_API_NAME       = "mi-agentteam-api"
$MI_WORKFLOWS_NAME = "mi-agentteam-workflows"

# ── 0. Subscription ────────────────────────────────────────────────────────────
Write-Host "`n==> Setting subscription..."
az account set --subscription $SubscriptionId

# ── 1. Resource Group ──────────────────────────────────────────────────────────
Write-Host "==> Creating resource group $RG..."
az group create --name $RG --location $LOCATION

# ── 2. Container Registry ──────────────────────────────────────────────────────
Write-Host "==> Creating ACR $ACR_NAME..."
az acr create `
    --resource-group $RG `
    --name           $ACR_NAME `
    --sku            Basic `
    --admin-enabled  false

$ACR_LOGIN_SERVER = (az acr show --name $ACR_NAME --query loginServer -o tsv)
$ACR_ID           = (az acr show --name $ACR_NAME --query id -o tsv)
Write-Host "    ACR: $ACR_LOGIN_SERVER"

# ── 3. Log Analytics + Application Insights ────────────────────────────────────
Write-Host "==> Creating Log Analytics workspace..."
az monitor log-analytics workspace create `
    --resource-group $RG `
    --workspace-name $LOG_WORKSPACE `
    --location       $LOCATION

$LAW_ID = (az monitor log-analytics workspace show `
    --resource-group $RG --workspace-name $LOG_WORKSPACE --query id -o tsv)

Write-Host "==> Creating Application Insights..."
az monitor app-insights component create `
    --app              $AI_NAME `
    --resource-group   $RG `
    --location         $LOCATION `
    --workspace        $LAW_ID `
    --application-type web

$AI_CONN_STR = (az monitor app-insights component show `
    --app $AI_NAME --resource-group $RG --query connectionString -o tsv)

# ── 4. Storage Account (Durable Functions) ─────────────────────────────────────
Write-Host "==> Creating Storage Account $STORAGE_ACCOUNT..."
az storage account create `
    --name                      $STORAGE_ACCOUNT `
    --resource-group            $RG `
    --location                  $LOCATION `
    --sku                       Standard_LRS `
    --kind                      StorageV2 `
    --allow-blob-public-access  false `
    --min-tls-version           TLS1_2

$STORAGE_CONN_STR = (az storage account show-connection-string `
    --name $STORAGE_ACCOUNT --resource-group $RG --query connectionString -o tsv)

# ── 5. Key Vault ───────────────────────────────────────────────────────────────
Write-Host "==> Creating Key Vault $KV_NAME..."
az keyvault create `
    --name                      $KV_NAME `
    --resource-group            $RG `
    --location                  $LOCATION `
    --sku                       standard `
    --enable-rbac-authorization true

$KV_URI = "https://${KV_NAME}.vault.azure.net/"
$KV_ID  = (az keyvault show --name $KV_NAME --resource-group $RG --query id -o tsv)

# ── 6. Cosmos DB (Serverless) ──────────────────────────────────────────────────
Write-Host "==> Creating Cosmos DB account $COSMOS_ACCOUNT (serverless)..."
az cosmosdb create `
    --name                      $COSMOS_ACCOUNT `
    --resource-group            $RG `
    --locations                 regionName=$LOCATION failoverPriority=0 isZoneRedundant=false `
    --capabilities              EnableServerless `
    --default-consistency-level Session

$COSMOS_ENDPOINT = (az cosmosdb show `
    --name $COSMOS_ACCOUNT --resource-group $RG --query documentEndpoint -o tsv)
$COSMOS_ID = (az cosmosdb show `
    --name $COSMOS_ACCOUNT --resource-group $RG --query id -o tsv)

Write-Host "==> Creating Cosmos DB database and containers..."
az cosmosdb sql database create `
    --account-name   $COSMOS_ACCOUNT `
    --resource-group $RG `
    --name           "tectikaagents"

$containers = @(
    @{ name = "tasks";     partition = "/id"     },
    @{ name = "agents";    partition = "/id"     },
    @{ name = "runs";      partition = "/taskId" },
    @{ name = "approvals"; partition = "/runId"  },
    @{ name = "audit";     partition = "/runId"  }
)
foreach ($c in $containers) {
    az cosmosdb sql container create `
        --account-name       $COSMOS_ACCOUNT `
        --resource-group     $RG `
        --database-name      "tectikaagents" `
        --name               $c.name `
        --partition-key-path $c.partition 2>$null
    Write-Host "    Container: $($c.name)"
}

# ── 7. Service Bus (Standard — required for Topics) ────────────────────────────
Write-Host "==> Creating Service Bus namespace $SB_NAMESPACE (Standard)..."
az servicebus namespace create `
    --name           $SB_NAMESPACE `
    --resource-group $RG `
    --location       $LOCATION `
    --sku            Standard

$SB_FQDN = "${SB_NAMESPACE}.servicebus.windows.net"
$SB_ID   = (az servicebus namespace show `
    --name $SB_NAMESPACE --resource-group $RG --query id -o tsv)

az servicebus topic create `
    --name                        "agent-events" `
    --namespace-name              $SB_NAMESPACE `
    --resource-group              $RG `
    --default-message-time-to-live P14D

az servicebus topic subscription create `
    --name           "api-sub" `
    --topic-name     "agent-events" `
    --namespace-name $SB_NAMESPACE `
    --resource-group $RG

az servicebus queue create `
    --name           "task-trigger" `
    --namespace-name $SB_NAMESPACE `
    --resource-group $RG

az servicebus queue create `
    --name           "approvals" `
    --namespace-name $SB_NAMESPACE `
    --resource-group $RG

# ── 8. Managed Identities ──────────────────────────────────────────────────────
Write-Host "==> Creating Managed Identities..."
az identity create --name $MI_API_NAME       --resource-group $RG --location $LOCATION
az identity create --name $MI_WORKFLOWS_NAME --resource-group $RG --location $LOCATION

$MI_API_CLIENT_ID = (az identity show --name $MI_API_NAME       --resource-group $RG --query clientId    -o tsv)
$MI_API_PRINCIPAL = (az identity show --name $MI_API_NAME       --resource-group $RG --query principalId -o tsv)
$MI_WF_CLIENT_ID  = (az identity show --name $MI_WORKFLOWS_NAME --resource-group $RG --query clientId    -o tsv)
$MI_WF_PRINCIPAL  = (az identity show --name $MI_WORKFLOWS_NAME --resource-group $RG --query principalId -o tsv)

$MI_API_RESOURCE_ID = (az identity show --name $MI_API_NAME       --resource-group $RG --query id -o tsv)
$MI_WF_RESOURCE_ID  = (az identity show --name $MI_WORKFLOWS_NAME --resource-group $RG --query id -o tsv)

# ── 9. RBAC Role Assignments ───────────────────────────────────────────────────
Write-Host "==> Assigning roles..."

$COSMOS_DATA_CONTRIBUTOR = "/subscriptions/$SubscriptionId/resourceGroups/$RG/providers/Microsoft.DocumentDB/databaseAccounts/$COSMOS_ACCOUNT/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"
$COSMOS_SCOPE            = "/subscriptions/$SubscriptionId/resourceGroups/$RG/providers/Microsoft.DocumentDB/databaseAccounts/$COSMOS_ACCOUNT"

foreach ($principal in @($MI_API_PRINCIPAL, $MI_WF_PRINCIPAL)) {
    # Cosmos DB data plane
    az cosmosdb sql role assignment create `
        --account-name       $COSMOS_ACCOUNT `
        --resource-group     $RG `
        --role-definition-id $COSMOS_DATA_CONTRIBUTOR `
        --principal-id       $principal `
        --scope              $COSMOS_SCOPE

    # Service Bus
    az role assignment create `
        --assignee   $principal `
        --role       "Azure Service Bus Data Owner" `
        --scope      $SB_ID

    # Key Vault
    az role assignment create `
        --assignee   $principal `
        --role       "Key Vault Secrets User" `
        --scope      $KV_ID

    # ACR Pull (Container Apps pull images)
    az role assignment create `
        --assignee   $principal `
        --role       "AcrPull" `
        --scope      $ACR_ID
}

# ── 10. Container Apps Environment ─────────────────────────────────────────────
Write-Host "==> Creating Container Apps Environment $CAE_NAME..."
az containerapp env create `
    --name               $CAE_NAME `
    --resource-group     $RG `
    --location           $LOCATION `
    --logs-workspace-id  $LAW_ID

# ── 11. Container Apps (placeholder image — CI/CD will update) ─────────────────
Write-Host "==> Creating Container App: API..."
az containerapp create `
    --name             $CA_API_NAME `
    --resource-group   $RG `
    --environment      $CAE_NAME `
    --image            "mcr.microsoft.com/azuredocs/containerapps-helloworld:latest" `
    --target-port      8080 `
    --ingress          external `
    --min-replicas     1 `
    --max-replicas     5 `
    --cpu              0.5 `
    --memory           "1Gi" `
    --user-assigned    $MI_API_RESOURCE_ID `
    --registry-server  $ACR_LOGIN_SERVER `
    --registry-identity $MI_API_RESOURCE_ID `
    --env-vars `
        "ASPNETCORE_ENVIRONMENT=Production" `
        "MockDatabase__Enabled=false" `
        "CosmosDb__AccountEndpoint=$COSMOS_ENDPOINT" `
        "CosmosDb__DatabaseName=tectikaagents" `
        "ServiceBus__Namespace=$SB_FQDN" `
        "ServiceBus__AgentEventsTopic=agent-events" `
        "ServiceBus__TaskTriggerQueue=task-trigger" `
        "ServiceBus__ApprovalsQueue=approvals" `
        "KeyVault__VaultUri=$KV_URI" `
        "APPLICATIONINSIGHTS_CONNECTION_STRING=$AI_CONN_STR" `
        "AZURE_CLIENT_ID=$MI_API_CLIENT_ID" `
        "AzureAd__Instance=https://login.microsoftonline.com/" `
        "AzureAd__TenantId=$TenantId"

Write-Host "==> Creating Container App: Workflows (internal)..."
az containerapp create `
    --name             $CA_WORKFLOWS_NAME `
    --resource-group   $RG `
    --environment      $CAE_NAME `
    --image            "mcr.microsoft.com/azuredocs/containerapps-helloworld:latest" `
    --target-port      80 `
    --ingress          internal `
    --min-replicas     1 `
    --max-replicas     3 `
    --cpu              0.5 `
    --memory           "1Gi" `
    --user-assigned    $MI_WF_RESOURCE_ID `
    --registry-server  $ACR_LOGIN_SERVER `
    --registry-identity $MI_WF_RESOURCE_ID `
    --env-vars `
        "AzureWebJobsStorage=$STORAGE_CONN_STR" `
        "FUNCTIONS_WORKER_RUNTIME=dotnet-isolated" `
        "CosmosDb__AccountEndpoint=$COSMOS_ENDPOINT" `
        "CosmosDb__DatabaseName=tectikaagents" `
        "ServiceBus__Namespace=$SB_FQDN" `
        "ServiceBus__AgentEventsTopic=agent-events" `
        "ServiceBus__TaskTriggerQueue=task-trigger" `
        "ServiceBus__ApprovalsQueue=approvals" `
        "APPLICATIONINSIGHTS_CONNECTION_STRING=$AI_CONN_STR" `
        "AZURE_CLIENT_ID=$MI_WF_CLIENT_ID"

$WF_FQDN = (az containerapp show `
    --name $CA_WORKFLOWS_NAME --resource-group $RG `
    --query properties.configuration.ingress.fqdn -o tsv)

# Wire Workflows URL into API
az containerapp update `
    --name           $CA_API_NAME `
    --resource-group $RG `
    --set-env-vars   "DurableFunctions__StartUrl=https://${WF_FQDN}/api/pipelines/start"

Write-Host "==> Creating Container App: Web..."
az containerapp create `
    --name           $CA_WEB_NAME `
    --resource-group $RG `
    --environment    $CAE_NAME `
    --image          "mcr.microsoft.com/azuredocs/containerapps-helloworld:latest" `
    --target-port    3000 `
    --ingress        external `
    --min-replicas   1 `
    --max-replicas   3 `
    --cpu            0.25 `
    --memory         "0.5Gi" `
    --env-vars       "NODE_ENV=production"

# ── 12. OIDC App Registration for GitHub Actions ───────────────────────────────
Write-Host "==> Creating App Registration for GitHub Actions OIDC..."
$GH_APP_ID = (az ad app create --display-name "sp-agentteam-github" --query appId -o tsv)
$GH_SP_OID = (az ad sp create --id $GH_APP_ID --query id -o tsv)

az role assignment create `
    --assignee-object-id       $GH_SP_OID `
    --assignee-principal-type  ServicePrincipal `
    --role                     "Contributor" `
    --scope                    "/subscriptions/$SubscriptionId/resourceGroups/$RG"

az role assignment create `
    --assignee-object-id       $GH_SP_OID `
    --assignee-principal-type  ServicePrincipal `
    --role                     "AcrPush" `
    --scope                    $ACR_ID

$fedCred = @{
    name        = "github-main"
    issuer      = "https://token.actions.githubusercontent.com"
    subject     = "repo:${GitHubOrg}/${GitHubRepo}:ref:refs/heads/main"
    audiences   = @("api://AzureADTokenExchange")
    description = "GitHub Actions main branch"
} | ConvertTo-Json -Compress

az ad app federated-credential create --id $GH_APP_ID --parameters $fedCred

# ── Done ───────────────────────────────────────────────────────────────────────
$API_FQDN = (az containerapp show `
    --name $CA_API_NAME --resource-group $RG `
    --query properties.configuration.ingress.fqdn -o tsv)
$WEB_FQDN = (az containerapp show `
    --name $CA_WEB_NAME --resource-group $RG `
    --query properties.configuration.ingress.fqdn -o tsv)

Write-Host ""
Write-Host "============================================================"
Write-Host "DEPLOYMENT COMPLETE"
Write-Host ""
Write-Host "GitHub Secrets to set:"
Write-Host "  AZURE_CLIENT_ID       = $GH_APP_ID"
Write-Host "  AZURE_TENANT_ID       = $TenantId"
Write-Host "  AZURE_SUBSCRIPTION_ID = $SubscriptionId"
Write-Host "  ACR_LOGIN_SERVER      = $ACR_LOGIN_SERVER"
Write-Host ""
Write-Host "After first CI/CD deploy, set:"
Write-Host "  NEXT_PUBLIC_API_URL   = https://$API_FQDN"
Write-Host ""
Write-Host "App URLs (placeholder image until CI/CD runs):"
Write-Host "  API  https://$API_FQDN"
Write-Host "  Web  https://$WEB_FQDN"
Write-Host "============================================================"
