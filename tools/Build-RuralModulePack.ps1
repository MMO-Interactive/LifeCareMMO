<#
.SYNOPSIS
    Regenerates the rural town module pack (assets/modules/rural) with Blender.

.EXAMPLE
    ./tools/Build-RuralModulePack.ps1
    ./tools/Build-RuralModulePack.ps1 -NoRender
    ./tools/Build-RuralModulePack.ps1 -Sheets roofs,walls_stone
#>
param(
    [string]$Blender = "C:\Program Files\Blender Foundation\Blender 5.1\blender.exe",
    [string]$Out = "assets/modules/rural",
    [switch]$NoRender,
    [switch]$NoTown,
    [string[]]$Sheets
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path $Blender)) { throw "Blender not found at '$Blender'. Pass -Blender <path to blender.exe>." }

$scriptArgs = @("--out", (Join-Path $root $Out))
if ($NoRender) { $scriptArgs += "--no-render" }
if ($NoTown) { $scriptArgs += "--no-town" }
if ($Sheets) { $scriptArgs += @("--sheets", ($Sheets -join ",")) }

Push-Location $root
try {
    # Blender prints warnings on stderr; don't let Windows PowerShell treat them as terminating errors.
    $ErrorActionPreference = "Continue"
    & $Blender -b --factory-startup -P (Join-Path $root "tools/blender/rural_modules/build_rural_pack.py") -- @scriptArgs
    if ($LASTEXITCODE -ne 0) { throw "Blender exited with $LASTEXITCODE." }
    Remove-Item (Join-Path $root "$Out/rural_modules.blend1") -ErrorAction SilentlyContinue
}
finally { Pop-Location }
