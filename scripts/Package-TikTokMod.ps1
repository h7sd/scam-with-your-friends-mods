[CmdletBinding()]
param([string]$ProjectDirectory = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$tikTokRoot = [IO.Path]::GetFullPath($ProjectDirectory)
$tikTokEntries = [ordered]@{
    'BepInEx/plugins/ScamWYF.TikTokPocket.dll' = 'tiktok-mod/bin/ScamWYF.TikTokPocket.dll'
    'BepInEx/plugins/TikTokPocket/tiktok-pocket.png' = 'tiktok-mod/assets/tiktok-pocket.png'
    'BepInEx/core/ScamWYF.Modding.Core.dll' = 'dist/BepInEx/core/ScamWYF.Modding.Core.dll'
    'scripts/Install-TikTokMod.ps1' = 'scripts/Install-TikTokMod.ps1'
    'scripts/Game-Paths.ps1' = 'scripts/Game-Paths.ps1'
    'README.md' = 'tiktok-mod/README.md'
    'LICENSE' = 'LICENSE'
    'Licenses/ScamWYF.Modding.Core-LICENSE.txt' = 'upstream/AI-Backend/vendor/ScamWYF.Modding.Core/LICENSE'
}
foreach($inputFile in $tikTokEntries.Values){if(-not (Test-Path -LiteralPath (Join-Path $tikTokRoot $inputFile) -PathType Leaf)){throw "Package input missing: $inputFile"}}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$tikTokZipPath = Join-Path $tikTokRoot 'TikTok-Pocket-SWYF-1.0.0.zip'
$tikTokZipStream = [IO.File]::Open($tikTokZipPath,[IO.FileMode]::Create)
$tikTokArchive = [IO.Compression.ZipArchive]::new($tikTokZipStream,[IO.Compression.ZipArchiveMode]::Create)
try {
    foreach($entry in $tikTokEntries.GetEnumerator()){
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($tikTokArchive,(Join-Path $tikTokRoot $entry.Value),$entry.Key,[IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {$tikTokArchive.Dispose(); $tikTokZipStream.Dispose()}
$tikTokVerification = [IO.Compression.ZipFile]::OpenRead($tikTokZipPath)
try {
    $tikTokNames = @($tikTokVerification.Entries | ForEach-Object FullName)
    if($tikTokNames.Count -ne $tikTokEntries.Count -or @($tikTokEntries.Keys | Where-Object {$tikTokNames -notcontains $_}).Count){throw 'TikTok Pocket package contents are incomplete.'}
    Write-Output "TikTok Pocket package verified: $($tikTokNames.Count) files; browser profiles are excluded."
}
finally {$tikTokVerification.Dispose()}
Get-Item -LiteralPath $tikTokZipPath | Select-Object Name,Length
