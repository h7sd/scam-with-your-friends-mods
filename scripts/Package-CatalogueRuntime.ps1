[CmdletBinding()]
param([string]$ProjectDirectory=(Split-Path -Parent $PSScriptRoot),[switch]$IncludeTikTok)
$ErrorActionPreference='Stop'
if($IncludeTikTok){. (Join-Path $PSScriptRoot 'TikTok-WorkerPackage.ps1')}
$catalogueRuntimeRoot=[IO.Path]::GetFullPath($ProjectDirectory)
$catalogueOutput=Join-Path $catalogueRuntimeRoot 'build/catalogue-packages'
[IO.Directory]::CreateDirectory($catalogueOutput)|Out-Null
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Write-CatalogueRuntimeZip([string]$ArchivePath,[Collections.IDictionary]$Entries){
    $stream=[IO.File]::Open($ArchivePath,[IO.FileMode]::Create)
    $archive=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach($entry in $Entries.GetEnumerator()){
            if(-not (Test-Path -LiteralPath $entry.Value -PathType Leaf)){throw ('Runtime source missing: '+$entry.Key)}
            $source=[IO.Path]::GetFullPath($entry.Value)
            $boundary=$catalogueRuntimeRoot.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
            if(-not $source.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase)){throw 'A runtime source escapes the project folder.'}
            $ancestor=$source
            while($ancestor){if(([IO.File]::GetAttributes($ancestor) -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Runtime packaging does not follow links or junctions.'};$ancestor=[IO.Path]::GetDirectoryName($ancestor)}
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$entry.Value,$entry.Key,[IO.Compression.CompressionLevel]::Optimal)|Out-Null
        }
    } finally {$archive.Dispose();$stream.Dispose()}
    $read=[IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {if($read.Entries.Count -ne $Entries.Count -or @($read.Entries | Where-Object {$Entries.Keys -cnotcontains $_.FullName}).Count){throw 'Runtime ZIP inventory mismatch.'}}
    finally {$read.Dispose()}
}
if($IncludeTikTok){
$worker=Get-TikTokWorkerPackage -WorkerRoot (Join-Path $catalogueRuntimeRoot 'webview-worker/publish')
$tikTokEntries=[ordered]@{
    'TikTokPocket/tiktok-pocket.png'=(Join-Path $catalogueRuntimeRoot 'tiktok-mod/assets/tiktok-pocket.png')
    'TikTokPocket/LICENSE.txt'=(Join-Path $catalogueRuntimeRoot 'LICENSE')
    'TikTokPocket/BrowserWorker/worker-files.json'=$worker.ManifestPath
}
foreach($file in $worker.Files){$tikTokEntries['TikTokPocket/BrowserWorker/'+$file.Path]=$file.FullPath}
$tikTokArchive=Join-Path $catalogueOutput 'TikTok-Pocket-Runtime-1.0.1-win-x64.zip'
Write-CatalogueRuntimeZip $tikTokArchive $tikTokEntries
}

$bridgeEntries=[ordered]@{
    'Start-ElevenLabs.cmd'=(Join-Path $catalogueRuntimeRoot 'Start-ElevenLabs.cmd')
    'scripts/Start-Backend.ps1'=(Join-Path $catalogueRuntimeRoot 'scripts/Start-Backend.ps1')
    'scripts/Start-Bridge.ps1'=(Join-Path $catalogueRuntimeRoot 'scripts/Start-Bridge.ps1')
    'LICENSE'=(Join-Path $catalogueRuntimeRoot 'LICENSE')
}
$bridgeRoot=Join-Path $catalogueRuntimeRoot 'bridge'
foreach($file in Get-ChildItem -LiteralPath $bridgeRoot -File -Recurse -Force | Sort-Object FullName){
    $relative=$file.FullName.Substring($bridgeRoot.Length+1).Replace('\','/')
    if($relative.StartsWith('runtime/') -or $relative.StartsWith('caller-profiles.json') -or
        ($relative.StartsWith('.env') -and $relative -cne '.env.example')){continue}
    if($relative -cne '.env.example' -and $relative -cne 'package.json' -and $relative -cne 'package-lock.json' -and
        -not $relative.StartsWith('src/') -and -not $relative.StartsWith('public/') -and -not $relative.StartsWith('node_modules/')){continue}
    if($relative.Split('/') | Where-Object {$_ -match '^\.env' -and $_ -cne '.env.example'}){continue}
    $bridgeEntries['bridge/'+$relative]=$file.FullName
}
foreach($required in @('bridge/src/server.js','bridge/node_modules/ws/package.json','bridge/node_modules/ajv/package.json','bridge/.env.example')){
    if(-not $bridgeEntries.Contains($required)){throw ('Bridge runtime incomplete: '+$required)}
}
$bridgeFileList=@(foreach($entry in $bridgeEntries.GetEnumerator()){
    [ordered]@{path=$entry.Key;size=(Get-Item -LiteralPath $entry.Value).Length;sha256=(Get-FileHash -LiteralPath $entry.Value).Hash.ToLowerInvariant()}
})
$bridgeManifestPath=Join-Path $catalogueOutput 'bridge-files.json'
[IO.File]::WriteAllText($bridgeManifestPath,([ordered]@{schemaVersion=1;version='1.0.1';files=$bridgeFileList}|ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
$bridgeEntries['bridge-files.json']=$bridgeManifestPath
$bridgeArchive=Join-Path $catalogueOutput 'ElevenLabs-Agents-Bridge-1.0.1.zip'
Write-CatalogueRuntimeZip $bridgeArchive $bridgeEntries
$runtimeDigests=[ordered]@{
    bridgeArchiveSha256=(Get-FileHash -LiteralPath $bridgeArchive).Hash.ToLowerInvariant()
    bridgeManifestSha256=(Get-FileHash -LiteralPath $bridgeManifestPath).Hash.ToLowerInvariant()
}
if($IncludeTikTok){
    $runtimeDigests['tikTokArchiveSha256']=(Get-FileHash -LiteralPath $tikTokArchive).Hash.ToLowerInvariant()
    $runtimeDigests['tikTokManifestSha256']=(Get-FileHash -LiteralPath $worker.ManifestPath).Hash.ToLowerInvariant()
    $runtimeDigests['tikTokIconSha256']=(Get-FileHash -LiteralPath $tikTokEntries['TikTokPocket/tiktok-pocket.png']).Hash.ToLowerInvariant()
}
[IO.File]::WriteAllText((Join-Path $catalogueOutput 'runtime-digests.json'),($runtimeDigests|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
Write-Output ('Bridge runtime: '+$bridgeEntries.Count+' entries. Private settings excluded.')
if($IncludeTikTok){Write-Output ('Optional legacy TikTok runtime: '+$tikTokEntries.Count+' entries.');Get-Item -LiteralPath $tikTokArchive|Select-Object Name,Length}
Get-Item -LiteralPath $bridgeArchive | Select-Object Name,Length
