<#Starts the local backend in the background and opens its setup page.#>
[CmdletBinding()]
param(
    [string]$ProjectDirectory = (Split-Path -Parent $PSScriptRoot),
    [ValidateRange(1, 65535)][int]$Port = 8765,
    [switch]$NoBrowser
)
$ErrorActionPreference = 'Stop'
$bridgePath = Join-Path ([IO.Path]::GetFullPath($ProjectDirectory)) 'bridge'
$serverPath = Join-Path $bridgePath 'src\server.js'
if (-not (Test-Path -LiteralPath $serverPath -PathType Leaf)) { throw 'The bridge source is missing from this package.' }
$nodeCommand = Get-Command node -ErrorAction SilentlyContinue
if (-not $nodeCommand) { throw 'Node.js 22.13 or newer is required. Install Node.js from nodejs.org first.' }
$version = & $nodeCommand.Source -p 'process.versions.node'
if ($LASTEXITCODE -ne 0 -or [version]$version -lt [version]'22.13.0') { throw 'Node.js 22.13 or newer is required.' }
if (-not (Test-Path -LiteralPath (Join-Path $bridgePath 'node_modules\ws') -PathType Container)) {
    throw 'Bridge dependencies are missing. Run npm ci in the bridge folder first.'
}
$baseUrl = "http://127.0.0.1:$Port"
function Get-BackendHealth {
    try { return Invoke-RestMethod -Uri "$baseUrl/health" -TimeoutSec 2 }
    catch { return $null }
}
$health = Get-BackendHealth
if ($health -and $health.service -ne 'scamwyf-elevenlabs-agents-bridge') {
    throw "Port $Port is already being used by another service. Close it or select a different port."
}
if (-not $health) {
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
    try { $probe.Start() }
    catch { throw "Port $Port is already occupied. The existing service is not the ElevenLabs Agents bridge." }
    finally { $probe.Stop() }
    $runtimePath = Join-Path $bridgePath 'runtime'
    [IO.Directory]::CreateDirectory($runtimePath) | Out-Null
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $stdoutPath = Join-Path $runtimePath "$stamp-stdout.log"
    $stderrPath = Join-Path $runtimePath "$stamp-stderr.log"
    $oldPort = $env:PORT
    try {
        $env:PORT = [string]$Port
        $process = Start-Process -FilePath $nodeCommand.Source -ArgumentList ('"' + $serverPath + '"') -WorkingDirectory $bridgePath -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru
    }
    finally { $env:PORT = $oldPort }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        if ($process.HasExited) { throw "The backend exited during startup. Check its local log: $stderrPath" }
        Start-Sleep -Milliseconds 200
        $health = Get-BackendHealth
    } while (-not $health -and [DateTime]::UtcNow -lt $deadline)
    if (-not $health -or $health.service -ne 'scamwyf-elevenlabs-agents-bridge') {
        throw "The backend did not start at $baseUrl. Check its local logs in $runtimePath"
    }
}
Write-Output "ElevenLabs Agents backend is running at $baseUrl"
if (-not $NoBrowser) { Start-Process -FilePath $baseUrl }
