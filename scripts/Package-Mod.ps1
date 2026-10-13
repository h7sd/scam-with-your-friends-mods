[CmdletBinding()]
param([string]$ProjectDirectory = (Split-Path -Parent $PSScriptRoot), [string]$PackageDirectory, [string]$FileName = 'SWYF-Mods-1.3.0.zip',[switch]$IncludeTikTok)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Game-Paths.ps1')
if($IncludeTikTok){. (Join-Path $PSScriptRoot 'TikTok-WorkerPackage.ps1')}
$projectRoot = [IO.Path]::GetFullPath($ProjectDirectory)
$packageRoot = if ($PackageDirectory) { [IO.Path]::GetFullPath($PackageDirectory) } else { Join-Path $projectRoot 'dist' }
if($IncludeTikTok){
$packageWorker = Get-TikTokWorkerPackage -WorkerRoot (Join-Path $packageRoot 'BepInEx/plugins/TikTokPocket/BrowserWorker') -Version '1.0.1'
$packagePlugin = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $packageRoot 'BepInEx/plugins/ScamWYF.TikTokPocket.dll'))
if ($packagePlugin.Name -ne 'ScamWYF.TikTokPocket' -or $packagePlugin.Version -ne [version]'1.0.1.0') { throw 'Stage TikTok Pocket 1.0.1 before packaging.' }
}
$packageBepFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($relative in @('BepInEx/core/ScamWYF.Modding.Core.dll','BepInEx/plugins/ScamWYF.AiBackend.dll',
    'BepInEx/plugins/ScamWYF.ElevenLabsAgents.dll','BepInEx/plugins/ScamWYF.RequestedPayout.dll')) { $null = $packageBepFiles.Add($relative) }
if($IncludeTikTok){
foreach($relative in @('BepInEx/plugins/ScamWYF.TikTokPocket.dll','BepInEx/plugins/TikTokPocket/tiktok-pocket.png','BepInEx/plugins/TikTokPocket/BrowserWorker/worker-files.json')){$null=$packageBepFiles.Add($relative)}
foreach ($workerFile in $packageWorker.Files) { $null = $packageBepFiles.Add('BepInEx/plugins/TikTokPocket/BrowserWorker/'+$workerFile.Path) }
}
if ([IO.Path]::GetFileName($FileName) -ne $FileName -or -not $FileName.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) { throw 'Package filename must be a ZIP filename without a directory.' }
$zipPath = Join-Path $projectRoot $FileName
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stream = [IO.File]::Open($zipPath, [IO.FileMode]::Create)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in (Get-ChildItem -LiteralPath $packageRoot -File -Recurse)) {
        $relative = $file.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
        if(-not $IncludeTikTok -and $relative -match '(?i)TikTok|webview-worker'){continue}
        if ($relative.StartsWith('BepInEx/',[StringComparison]::OrdinalIgnoreCase) -and -not $packageBepFiles.Contains($relative)) { continue }
        if ($relative.StartsWith('bridge/runtime/')) { continue }
        if ($relative.StartsWith('bridge/caller-profiles.json')) { continue }
        if ($relative.StartsWith('bridge/.env') -and $relative -ne 'bridge/.env.example') { continue }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative,
            [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally { $archive.Dispose(); $stream.Dispose() }
$verification = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $names = @($verification.Entries | ForEach-Object FullName)
    $requiredFiles=@('Launcher/Setup/setup.ps1','Start-ElevenLabs.cmd','bridge/src/agents.js','BepInEx/plugins/ScamWYF.ElevenLabsAgents.dll','BepInEx/plugins/ScamWYF.RequestedPayout.dll')
    if($IncludeTikTok){$requiredFiles+=@('BepInEx/plugins/ScamWYF.TikTokPocket.dll',
        'BepInEx/plugins/TikTokPocket/tiktok-pocket.png','BepInEx/plugins/TikTokPocket/BrowserWorker/worker-files.json',
        'BepInEx/plugins/TikTokPocket/BrowserWorker/ScamWYF.TikTokBrowser.exe',
        'scripts/Install-TikTokMod.ps1','scripts/TikTok-WorkerPackage.ps1','tiktok-mod/README.md')}
    foreach ($required in $requiredFiles) {
        if ($names -notcontains $required) { throw "Package is incomplete: $required" }
    }
    if(-not $IncludeTikTok -and @($names|Where-Object{$_ -match '(?i)TikTok|webview-worker'}).Count){throw 'The default bundle must exclude legacy TikTok files.'}
    if (@($names | Where-Object { $_.StartsWith('bridge/runtime/') -or $_.StartsWith('bridge/caller-profiles.json') -or ($_.StartsWith('bridge/.env') -and $_ -ne 'bridge/.env.example') }).Count) {
        throw 'Private backend files were included.'
    }
    Write-Output "Package verified: $($names.Count) files; credentials and runtime files excluded."
}
finally { $verification.Dispose() }
Get-Item -LiteralPath $zipPath | Select-Object Name,Length
