[CmdletBinding()]
param([string]$ProjectDirectory = (Split-Path -Parent $PSScriptRoot))
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
Push-Location $bridgePath
try {
    Write-Output 'Starting the ElevenLabs Agents bridge. Keep this terminal open while playing.'
    & $nodeCommand.Source $serverPath
    if ($LASTEXITCODE -ne 0) { throw "The bridge stopped with exit code $LASTEXITCODE." }
}
finally { Pop-Location }
