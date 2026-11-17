<#
.SYNOPSIS
  Starts every LifeCare server role as its own process on this machine: climate, accounts, one district host and the Gateway.
  Clients use only the Gateway: accounts address http://127.0.0.1:<GatewayHttpPort>.
  Each role can later move to its own machine by pointing the others at its address instead of 127.0.0.1.
#>
param(
    [string]$DataRoot = (Join-Path $PSScriptRoot '..\server-data'),
    [string]$Dotnet = (Join-Path $env:USERPROFILE '.cache\mmoi-dotnet\10.0.401\dotnet.exe'),
    [int]$WorldSeed = 73019,
    [int]$AccountsPort = 5080,
    [int]$ClimatePort = 5300,
    [int]$ConstructionPort = 5400,
    [int]$DistrictHttpPort = 5180,
    [int]$DistrictRelayPort = 5190,
    [int]$GatewayHttpPort = 5000,
    [int]$GatewayGamePort = 7000,
    [switch]$Build
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
if ($Build) { & $Dotnet build (Join-Path $repo 'LifeCareMMO.slnx') -c Release -nologo -v:q; if ($LASTEXITCODE) { throw 'Build failed.' } }
function Dll($project) { Join-Path $repo "src\Servers\$project\bin\Release\net10.0\$project.dll" }
function Start-Role($name, $dll, [string[]]$arguments) {
    $logs = Join-Path $DataRoot 'logs'; New-Item -ItemType Directory -Force $logs | Out-Null
    $p = Start-Process $Dotnet -ArgumentList (@("`"$dll`"") + $arguments) -WorkingDirectory (Split-Path $dll) -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $logs "$name.log") -RedirectStandardError (Join-Path $logs "$name.err.log")
    "{0,-9} pid {1}" -f $name, $p.Id
}
function Wait-Http($url, $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) { try { Invoke-RestMethod $url -TimeoutSec 2 | Out-Null; return } catch { Start-Sleep -Milliseconds 300 } }
    throw "$url did not answer within $seconds s."
}

Start-Role 'climate' (Dll 'LifeCare.Climate') @('--port', $ClimatePort, '--world-seed', $WorldSeed, '--data-root', (Join-Path $DataRoot 'climate'))
Start-Role 'accounts' (Dll 'StormChase.Server') @('--accounts-only', '--http-port', $AccountsPort, '--data-root', (Join-Path $DataRoot 'accounts'))
Wait-Http "http://127.0.0.1:$AccountsPort/health" 60
$env:LIFECARE_MASTER_KEY = (Get-Content (Join-Path $DataRoot 'accounts\master-server.key') -Raw).Trim()
Start-Role 'district' (Dll 'StormChase.Server') @('--http-port', $DistrictHttpPort, '--relay-port', $DistrictRelayPort, '--climate', "127.0.0.1:$ClimatePort",
    '--master-url', "http://127.0.0.1:$AccountsPort", '--master-key-env', 'LIFECARE_MASTER_KEY', '--world-seed', $WorldSeed, '--construction', "http://127.0.0.1:$ConstructionPort", '--server-name', 'LifeCare district', '--data-root', (Join-Path $DataRoot 'district'))
Start-Role 'construction' (Dll 'LifeCare.Construction.Service') @('--http-port', $ConstructionPort, '--accounts', "http://127.0.0.1:$AccountsPort", '--accounts-key-env', 'LIFECARE_MASTER_KEY', '--data-root', (Join-Path $DataRoot 'construction'))
Start-Role 'gateway' (Dll 'LifeCare.Gateway') @('--http-port', $GatewayHttpPort, '--game-port', $GatewayGamePort, '--accounts', "http://127.0.0.1:$AccountsPort",
    '--construction', "http://127.0.0.1:$ConstructionPort", '--data-root', (Join-Path $DataRoot 'gateway'))
Wait-Http "http://127.0.0.1:$GatewayHttpPort/gateway/health" 60
"Gateway ready at http://127.0.0.1:$GatewayHttpPort (game port $GatewayGamePort). Logs: $DataRoot\logs"
"Point the client at it:  `$env:MMOI_STORM_CHASE_ACCOUNTS_API = 'http://127.0.0.1:$GatewayHttpPort'"
