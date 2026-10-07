<#Stages built mods and the bridge into dist without installing them or copying private configuration.#>
[CmdletBinding()]
param([string]$ProjectDirectory = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Game-Paths.ps1')
$root = [IO.Path]::GetFullPath($ProjectDirectory)
$dist = Join-Path $root 'dist'
$files = [ordered]@{
    'BepInEx/core/ScamWYF.Modding.Core.dll' = 'upstream/AI-Backend/vendor/ScamWYF.Modding.Core/bin/ScamWYF.Modding.Core.dll'
    'BepInEx/plugins/ScamWYF.AiBackend.dll' = 'upstream/AI-Backend/bin/ScamWYF.AiBackend.dll'
    'BepInEx/plugins/ScamWYF.ElevenLabsAgents.dll' = 'mod/bin/ScamWYF.ElevenLabsAgents.dll'
    'BepInEx/plugins/ScamWYF.RequestedPayout.dll' = 'payout-mod/bin/ScamWYF.RequestedPayout.dll'
    'Licenses/AI-Backend.txt' = 'upstream/AI-Backend/LICENSE'
    'Licenses/Modding-Core.txt' = 'upstream/AI-Backend/vendor/ScamWYF.Modding.Core/LICENSE'
    'Licenses/Launcher.txt' = 'upstream/Launcher/LICENSE'
    'README.md' = 'README.md'
    'LICENSE' = 'LICENSE'
    'THIRD-PARTY-NOTICES.md' = 'THIRD-PARTY-NOTICES.md'
    'payout-mod/README.md' = 'payout-mod/README.md'
    'Start-ElevenLabs.cmd' = 'Start-ElevenLabs.cmd'
}
foreach ($name in @('Install-Mod.ps1','Install-PayoutMod.ps1','Game-Paths.ps1','Start-Backend.ps1','Start-Bridge.ps1')) {
    $files['scripts/' + $name] = 'scripts/' + $name
}
foreach ($relative in $files.Values) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)) { throw "Build input missing: $relative" }
}
if (-not (Test-Path -LiteralPath (Join-Path $root 'bridge/node_modules/ws/package.json'))) { throw 'Run npm ci in bridge before staging the release.' }
foreach ($entry in $files.GetEnumerator()) {
    $destination = Get-ScamChildPath $dist $entry.Key
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [IO.File]::Copy((Join-Path $root $entry.Value), $destination, $true)
}
$bridgeRoot = Join-Path $root 'bridge'
foreach ($file in Get-ChildItem -LiteralPath $bridgeRoot -File -Recurse -Force) {
    $relative = $file.FullName.Substring($bridgeRoot.Length + 1).Replace('\','/')
    if ($relative.StartsWith('runtime/') -or $relative.StartsWith('caller-profiles.json') -or
        ($relative.StartsWith('.env') -and $relative -ne '.env.example')) { continue }
    $destination = Get-ScamChildPath $dist ('bridge/' + $relative)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [IO.File]::Copy($file.FullName, $destination, $true)
}
Write-Output 'Staged both mods and bridge. Private environment, caller profiles and runtime files were not copied.'
