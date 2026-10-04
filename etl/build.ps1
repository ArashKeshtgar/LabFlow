<#
.SYNOPSIS
    Compiles the package builder and regenerates LabFlowETL/LabFlowETL.dtsx.
.DESCRIPTION
    Needs SQL Server 2019 Integration Services (v15 assemblies in the GAC) and
    the .NET Framework C# compiler. Run it after changing builder/BuildPackage.cs;
    the generated .dtsx is committed so the package can be opened without building.
#>
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$gac = "$env:WINDIR\Microsoft.NET\assembly"
$refs = @(
    "$gac\GAC_MSIL\Microsoft.SqlServer.ManagedDTS\v4.0_15.0.0.0__89845dcd8080cc91\Microsoft.SqlServer.ManagedDTS.dll",
    "$gac\GAC_MSIL\Microsoft.SqlServer.DTSPipelineWrap\v4.0_15.0.0.0__89845dcd8080cc91\Microsoft.SqlServer.DTSPipelineWrap.dll",
    "$gac\GAC_64\Microsoft.SqlServer.DTSRuntimeWrap\v4.0_15.0.0.0__89845dcd8080cc91\Microsoft.SqlServer.DTSRuntimeWrap.dll",
    "$env:ProgramFiles\Microsoft SQL Server\150\DTS\Tasks\Microsoft.SqlServer.SQLTask.dll"
)
foreach ($r in $refs) { if (-not (Test-Path $r)) { throw "Missing SSIS assembly: $r" } }

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$bin = Join-Path $here 'builder\bin'
New-Item -ItemType Directory -Force $bin | Out-Null
$exe = Join-Path $bin 'BuildPackage.exe'

& $csc /nologo /platform:x64 /target:exe "/out:$exe" ($refs | ForEach-Object { "/reference:$_" }) (Join-Path $here 'builder\BuildPackage.cs')
if ($LASTEXITCODE -ne 0) { throw 'Compile failed.' }

$out = Join-Path $here 'LabFlowETL\LabFlowETL.dtsx'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
& $exe $out
if ($LASTEXITCODE -ne 0) { throw 'Package generated but did not validate (see errors above).' }
