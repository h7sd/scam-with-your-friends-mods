[CmdletBinding()]
param([string]$ProjectDirectory = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath($ProjectDirectory)
$entries = [ordered]@{
    'BepInEx/plugins/ScamWYF.RequestedPayout.dll' = 'payout-mod\bin\ScamWYF.RequestedPayout.dll'
    'BepInEx/core/ScamWYF.Modding.Core.dll' = 'dist\BepInEx\core\ScamWYF.Modding.Core.dll'
    'scripts/Install-PayoutMod.ps1' = 'scripts\Install-PayoutMod.ps1'
    'scripts/Game-Paths.ps1' = 'scripts\Game-Paths.ps1'
    'README.md' = 'payout-mod\README.md'
    'LICENSE' = 'LICENSE'
    'Licenses/ScamWYF.Modding.Core-LICENSE.txt' = 'upstream\AI-Backend\vendor\ScamWYF.Modding.Core\LICENSE'
}
foreach ($relative in $entries.Values) {
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot $relative) -PathType Leaf)) { throw "Package input missing: $relative" }
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = Join-Path $projectRoot 'Requested-Payout-SWYF-1.2.0.zip'
$stream = [IO.File]::Open($zipPath, [IO.FileMode]::Create)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($entryName in $entries.Keys) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $projectRoot $entries[$entryName]), $entryName,
            [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally { $archive.Dispose(); $stream.Dispose() }
$verification = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $actualNames = @($verification.Entries | ForEach-Object FullName)
    if ($actualNames.Count -ne $entries.Count -or @($entries.Keys | Where-Object { $actualNames -notcontains $_ }).Count) {
        throw 'Payout package contents are incomplete.'
    }
    Write-Output "Requested Payout package verified: $($actualNames.Count) files."
}
finally { $verification.Dispose() }
Get-Item -LiteralPath $zipPath | Select-Object Name,Length
