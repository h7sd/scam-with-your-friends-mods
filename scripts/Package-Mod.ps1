[CmdletBinding()]
param([string]$ProjectDirectory = (Split-Path -Parent $PSScriptRoot), [string]$PackageDirectory, [string]$FileName = 'SWYF-Mods-1.1.2.zip')
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath($ProjectDirectory)
$packageRoot = if ($PackageDirectory) { [IO.Path]::GetFullPath($PackageDirectory) } else { Join-Path $projectRoot 'dist' }
if ([IO.Path]::GetFileName($FileName) -ne $FileName -or -not $FileName.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) { throw 'Package filename must be a ZIP filename without a directory.' }
$zipPath = Join-Path $projectRoot $FileName
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stream = [IO.File]::Open($zipPath, [IO.FileMode]::Create)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in (Get-ChildItem -LiteralPath $packageRoot -File -Recurse)) {
        $relative = $file.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
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
    foreach ($required in @('Launcher/Setup/setup.ps1','Start-ElevenLabs.cmd','bridge/src/agents.js','BepInEx/plugins/ScamWYF.ElevenLabsAgents.dll')) {
        if ($names -notcontains $required) { throw "Package is incomplete: $required" }
    }
    if (@($names | Where-Object { $_.StartsWith('bridge/runtime/') -or $_.StartsWith('bridge/caller-profiles.json') -or ($_.StartsWith('bridge/.env') -and $_ -ne 'bridge/.env.example') }).Count) {
        throw 'Private backend files were included.'
    }
    Write-Output "Package verified: $($names.Count) files; credentials and runtime files excluded."
}
finally { $verification.Dispose() }
Get-Item -LiteralPath $zipPath | Select-Object Name,Length
