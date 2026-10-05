<#
.SYNOPSIS
    Runs the LabFlow ETL package with dtexec and prints the run it recorded.
.EXAMPLE
    ./run.ps1
    ./run.ps1 -LookbackVisits 0          # only visits above the high-water mark
    ./run.ps1 -LegacyServer OTHERHOST
.NOTES
    dtexec needs the Integration Services feature of SQL Server (Developer or
    Standard edition and up). Without it a package only runs inside Visual Studio.
#>
param(
    [string]$LegacyServer = '.',
    [string]$LegacyDatabase = 'Laboratory',
    [string]$LabFlowServer = '.',
    [string]$LabFlowDatabase = 'LabFlow',
    [int]$LookbackVisits = 200
)
$ErrorActionPreference = 'Stop'
$dtexec = "$env:ProgramFiles\Microsoft SQL Server\150\DTS\Binn\DTExec.exe"
$package = Join-Path $PSScriptRoot 'LabFlowETL\LabFlowETL.dtsx'

# The settings are package variables: dtexec's /Par only reaches parameters
# of catalog-deployed packages, /SET reaches variables of a package file.
function Set-Param([string]$name, $value) {
    '/SET', "\Package.Variables[User::$name].Properties[Value];$value"
}
& $dtexec /F $package /Rep EW `
    (Set-Param LegacyServer $LegacyServer) (Set-Param LegacyDatabase $LegacyDatabase) `
    (Set-Param LabFlowServer $LabFlowServer) (Set-Param LabFlowDatabase $LabFlowDatabase) `
    (Set-Param LookbackVisits $LookbackVisits)
$code = $LASTEXITCODE

& sqlcmd -S $LabFlowServer -E -d $LabFlowDatabase -W -s ' | ' -Q "SET NOCOUNT ON; SELECT TOP (1) RunId, Status, DurationSec, VisitsExtracted, ResultsExtracted, ResultsInserted, ResultsUpdated, ResultsSkipped, ResultsRejected, Warnings, Message FROM etl.vw_RunHistory ORDER BY RunId DESC"
exit $code
