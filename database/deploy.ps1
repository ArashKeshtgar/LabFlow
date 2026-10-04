<#
.SYNOPSIS
    Builds the LabFlow database from the numbered scripts in this folder.
.EXAMPLE
    ./deploy.ps1                 # create if missing, then run schema + seeds
    ./deploy.ps1 -Recreate       # drop and rebuild from scratch
    ./deploy.ps1 -NoDemo         # reference data only, no synthetic patients
#>
param(
    [string]$Server = '.',
    [string]$Database = 'LabFlow',
    [switch]$Recreate,
    [switch]$NoDemo
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

function Invoke-Sql([string]$db, [string]$file, [string]$query) {
    $sqlArgs = @('-S', $Server, '-E', '-d', $db, '-b', '-I', '-f', '65001')   # scripts are UTF-8
    if ($file) { $sqlArgs += @('-i', $file) } else { $sqlArgs += @('-Q', $query) }
    & sqlcmd @sqlArgs
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed ($db): $file$query" }
}

if ($Recreate) {
    Write-Host "Dropping $Database..."
    Invoke-Sql 'master' $null "IF DB_ID(N'$Database') IS NOT NULL BEGIN ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Database]; END"
}

# 001 names the database through a sqlcmd variable (the only script that needs it).
& sqlcmd -S $Server -E -d master -b -I -f 65001 -i (Join-Path $here '001_database.sql') -v "DatabaseName=$Database"
if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed (master): 001_database.sql" }

$schemaExists = & sqlcmd -S $Server -E -d $Database -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.schemas WHERE name = 'lab'"
if ($schemaExists.Trim() -eq '1') {
    Write-Host "$Database already has a schema. Use -Recreate to rebuild." -ForegroundColor Yellow
    return
}

$scripts = Get-ChildItem $here -Filter '*.sql' |
    Where-Object { $_.Name -match '^\d{3}_' -and $_.Name -ne '001_database.sql' } |
    Sort-Object Name
if (-not $scripts) { throw "No numbered scripts found in $here" }
if ($NoDemo) { $scripts = $scripts | Where-Object Name -notlike '*_seed_demo.sql' }

foreach ($s in $scripts) {
    Write-Host "Running $($s.Name)..."
    Invoke-Sql $Database $s.FullName $null
}
Write-Host "$Database is ready." -ForegroundColor Green
