<#Stages built mods and the bridge into dist without installing them or copying private configuration.#>
[CmdletBinding()]
param([string]$ProjectDirectory = (Split-Path -Parent $PSScriptRoot),[switch]$IncludeTikTok)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Game-Paths.ps1')
if($IncludeTikTok){. (Join-Path $PSScriptRoot 'TikTok-WorkerPackage.ps1')}
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
if($IncludeTikTok){
$files['BepInEx/plugins/ScamWYF.TikTokPocket.dll']='tiktok-mod/bin/ScamWYF.TikTokPocket.dll'
$files['BepInEx/plugins/TikTokPocket/tiktok-pocket.png']='tiktok-mod/assets/tiktok-pocket.png'
$files['tiktok-mod/README.md']='tiktok-mod/README.md'
foreach($name in @('Install-TikTokMod.ps1','TikTok-WorkerPackage.ps1')){$files['scripts/'+$name]='scripts/'+$name}
$workerSource = Get-TikTokWorkerPackage -WorkerRoot (Join-Path $root 'webview-worker/publish') -Version '1.0.1'
$files['BepInEx/plugins/TikTokPocket/BrowserWorker/worker-files.json'] = 'webview-worker/publish/worker-files.json'
foreach ($workerFile in $workerSource.Files) {
    $files['BepInEx/plugins/TikTokPocket/BrowserWorker/'+$workerFile.Path] = 'webview-worker/publish/'+$workerFile.Path
}
}
foreach ($relative in $files.Values) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)) { throw "Build input missing: $relative" }
}
if (-not (Test-Path -LiteralPath (Join-Path $root 'bridge/node_modules/ws/package.json'))) { throw 'Run npm ci in bridge before staging the release.' }
foreach ($entry in $files.GetEnumerator()) {
    $destination = Get-ScamChildPath $dist $entry.Key
    $ancestor=$destination
    while($ancestor){if(Test-Path -LiteralPath $ancestor){if(([IO.File]::GetAttributes($ancestor) -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Release staging does not follow links or junctions.'}};$ancestor=[IO.Path]::GetDirectoryName($ancestor)}
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [IO.File]::Copy((Join-Path $root $entry.Value), $destination, $true)
}
$legacyPayoutReadme = Get-ScamChildPath $dist 'Wunschsumme-README.md'
if (Test-Path -LiteralPath $legacyPayoutReadme -PathType Leaf) { Remove-Item -LiteralPath $legacyPayoutReadme }
$bridgeRoot = Join-Path $root 'bridge'
foreach ($file in Get-ChildItem -LiteralPath $bridgeRoot -File -Recurse -Force) {
    $relative = $file.FullName.Substring($bridgeRoot.Length + 1).Replace('\','/')
    if ($relative.StartsWith('runtime/') -or $relative.StartsWith('caller-profiles.json') -or
        ($relative.StartsWith('.env') -and $relative -ne '.env.example')) { continue }
    $destination = Get-ScamChildPath $dist ('bridge/' + $relative)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [IO.File]::Copy($file.FullName, $destination, $true)
}
Write-Output 'Staged ElevenLabs Agents, Requested Payout and the bridge. Existing generated assets were left in place; private settings and runtime files were not copied.'
if($IncludeTikTok){Write-Output 'Also staged the explicitly requested legacy TikTok Pocket mod.'}
