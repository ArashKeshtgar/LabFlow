<#
.SYNOPSIS
    Deploys the Azure Data Factory leg of the LabFlow ETL.
.DESCRIPTION
    1. Resource group + main.bicep (SQL server, free LabFlowDW database, factory,
       self-hosted IR, linked services, datasets, pipeline, trigger).
    2. sql/labflowdw.sql on LabFlowDW, plus a database user for the factory's
       managed identity (Entra token of the signed-in az account).
    3. Registers this machine as the self-hosted IR node (needs an elevated shell
       the first time; prints the manual steps otherwise).
    4. Optionally starts the daily trigger.
    The on-prem read-only password is read from %APPDATA%\LabFlow\adf_reader.secret
    (created with the adf_reader login), never from the repository.
.EXAMPLE
    ./deploy.ps1
    ./deploy.ps1 -StartTrigger
#>
param(
    [string]$ResourceGroup = 'labflow-etl-rg',
    [string]$Location = 'canadacentral',
    [string]$FactoryName = 'labflow-adf-arash',
    [string]$SqlServerName = 'labflow-arash-sql',
    [string]$DatabaseName = 'LabFlowDW',
    [switch]$StartTrigger
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

$secretFile = Join-Path $env:APPDATA 'LabFlow\adf_reader.secret'
if (-not (Test-Path $secretFile)) { throw "Missing $secretFile (create the adf_reader login first, see README)." }
$onPremPassword = (Get-Content $secretFile -Raw).Trim()

$me = az ad signed-in-user show --query '{upn:userPrincipalName, id:id}' -o json | ConvertFrom-Json
$ip = (Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 15).Trim()

Write-Host "==> 1/4 Resource group and Bicep ($ResourceGroup, $Location)"
az group create -n $ResourceGroup -l $Location -o none
$paramFile = Join-Path $env:TEMP "labflow-adf-params-$PID.json"
@{
    '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
    contentVersion = '1.0.0.0'
    parameters = @{
        location = @{ value = $Location }; factoryName = @{ value = $FactoryName }
        sqlServerName = @{ value = $SqlServerName }; databaseName = @{ value = $DatabaseName }
        sqlAdminLogin = @{ value = $me.upn }; sqlAdminObjectId = @{ value = $me.id }
        deployerIp = @{ value = $ip }; onPremPassword = @{ value = $onPremPassword }
    }
} | ConvertTo-Json -Depth 5 | Set-Content $paramFile -Encoding utf8
try {
    $out = az deployment group create -g $ResourceGroup -n labflow-adf -f (Join-Path $here 'main.bicep') `
        -p "@$paramFile" --query properties.outputs -o json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Bicep deployment failed.' }
} finally { Remove-Item $paramFile -ErrorAction SilentlyContinue }   # holds the password

Write-Host "==> 2/4 Database objects and the factory's user"
$token = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
function Invoke-AzureSql([string]$sql) {
    $cn = New-Object System.Data.SqlClient.SqlConnection("Server=tcp:$($out.sqlServerFqdn.value),1433;Database=$DatabaseName;Encrypt=True;Connection Timeout=120")
    $cn.AccessToken = $token
    $cn.Open()   # a paused serverless database resumes on the first connection
    try {
        foreach ($batch in ($sql -split '(?m)^\s*GO\s*$')) {
            if ($batch.Trim()) { $cmd = $cn.CreateCommand(); $cmd.CommandTimeout = 300; $cmd.CommandText = $batch; [void]$cmd.ExecuteNonQuery() }
        }
    } finally { $cn.Close() }
}
Invoke-AzureSql (Get-Content (Join-Path $here 'sql\labflowdw.sql') -Raw -Encoding utf8)
Invoke-AzureSql @"
IF USER_ID(N'$FactoryName') IS NULL CREATE USER [$FactoryName] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [$FactoryName];
ALTER ROLE db_datawriter ADD MEMBER [$FactoryName];
GRANT EXECUTE ON SCHEMA::etl TO [$FactoryName];
GRANT ALTER ON SCHEMA::stg TO [$FactoryName];   -- TRUNCATE in the pre-copy scripts
"@

Write-Host '==> 3/4 Self-hosted integration runtime node'
$irName = $out.integrationRuntimeName.value
$status = az datafactory integration-runtime get-status -g $ResourceGroup --factory-name $FactoryName -n $irName --query properties.state -o tsv
if ($status -eq 'Online') {
    Write-Host '  already registered and online'
} else {
    $key = az datafactory integration-runtime list-auth-key -g $ResourceGroup --factory-name $FactoryName -n $irName --query authKey1 -o tsv
    $dmgcmd = Get-ChildItem "$env:ProgramFiles\Microsoft Integration Runtime\*\Shared\dmgcmd.exe" | Select-Object -First 1
    $elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($dmgcmd -and $elevated) {
        & $dmgcmd.FullName -RegisterNewNode $key $env:COMPUTERNAME
    } else {
        Write-Host '  Not elevated: open "Microsoft Integration Runtime Configuration Manager", choose Register,' -ForegroundColor Yellow
        Write-Host '  and paste the key from:' -ForegroundColor Yellow
        Write-Host "  az datafactory integration-runtime list-auth-key -g $ResourceGroup --factory-name $FactoryName -n $irName --query authKey1 -o tsv" -ForegroundColor Yellow
    }
}

Write-Host '==> 4/4 Trigger'
if ($StartTrigger) {
    az datafactory trigger start -g $ResourceGroup --factory-name $FactoryName -n TR_Daily -o none
    Write-Host '  TR_Daily started (03:00 Toronto)'
} else { Write-Host '  TR_Daily left stopped (use -StartTrigger)' }

Write-Host "Done. Run once: az datafactory pipeline create-run -g $ResourceGroup --factory-name $FactoryName -n PL_LabFlow_DW_to_Azure" -ForegroundColor Green
