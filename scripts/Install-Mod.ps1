<#
Installs the three locally built DLLs and localhost configuration. Existing files are backed up.
Use -WhatIf to review the destination without writing to the game.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$GameDirectory,
    [string]$PackageDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'),
    [ValidateRange(1, 65535)][int]$Port = 8765
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Game-Paths.ps1')

function Update-ModConfig {
    param([string]$Path, [Collections.IDictionary]$Sections)
    $lines = [Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        foreach ($line in [IO.File]::ReadAllLines($Path)) { $lines.Add($line) }
    }
    foreach ($section in $Sections.Keys) {
        $sectionStart = -1
        $sectionEnd = $lines.Count
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^\s*\[([^\]]+)\]\s*$') {
                if ($sectionStart -ge 0) { $sectionEnd = $i; break }
                if ($Matches[1] -eq $section) { $sectionStart = $i }
            }
        }
        if ($sectionStart -lt 0) {
            if ($lines.Count -gt 0) { $lines.Add('') }
            $lines.Add("[$section]")
            $sectionStart = $lines.Count - 1
            $sectionEnd = $lines.Count
        }
        foreach ($key in $Sections[$section].Keys) {
            $pattern = '^\s*' + [regex]::Escape([string]$key) + '\s*='
            $found = $false
            for ($i = $sectionStart + 1; $i -lt $sectionEnd; $i++) {
                if ($lines[$i] -match $pattern) {
                    $lines[$i] = "$key = $($Sections[$section][$key])"
                    $found = $true
                }
            }
            if (-not $found) {
                $lines.Insert($sectionEnd, "$key = $($Sections[$section][$key])")
                $sectionEnd++
            }
        }
    }
    [IO.File]::WriteAllLines($Path, $lines, [Text.UTF8Encoding]::new($false))
}

$gamePath = Resolve-ScamGameDirectory $GameDirectory
Assert-ScamGameClosed
$bepinexRoot = Get-ScamChildPath $gamePath 'BepInEx'
if (-not (Test-Path -LiteralPath (Join-Path $bepinexRoot 'core\BepInEx.dll') -PathType Leaf)) {
    throw 'BepInEx is missing. Complete Setup in the original launcher before installing this mod.'
}
if (-not (Test-Path -LiteralPath (Join-Path $gamePath 'winhttp.dll') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $gamePath 'unstripped_corlib\mscorlib.dll') -PathType Leaf)) {
    throw 'Game modding setup is incomplete. Complete Setup in the original launcher first.'
}
$packagePath = [IO.Path]::GetFullPath($PackageDirectory)
$relativeDlls = @(
    'BepInEx\core\ScamWYF.Modding.Core.dll',
    'BepInEx\plugins\ScamWYF.AiBackend.dll',
    'BepInEx\plugins\ScamWYF.ElevenLabsAgents.dll'
)
foreach ($relative in $relativeDlls) {
    $sourcePath = Get-ScamChildPath $packagePath $relative
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw "Build output is missing: $relative" }
    [void][Reflection.AssemblyName]::GetAssemblyName($sourcePath)
}
$configRelatives = @(
    'BepInEx\config\com.community.scamwyf.aibackend.cfg',
    'BepInEx\config\com.community.scamwyf.elevenlabsagents.cfg'
)
$misplaced = @(
    'BepInEx\plugins\ScamWYF.Modding.Core.dll',
    'BepInEx\plugins_disabled\ScamWYF.Modding.Core.dll',
    'BepInEx\plugins_disabled\ScamWYF.AiBackend.dll',
    'BepInEx\plugins_disabled\ScamWYF.ElevenLabsAgents.dll'
)
$allRelatives = @($relativeDlls) + @($configRelatives) + @($misplaced)
Write-Output "Game folder: $gamePath"
Write-Output 'Installs ElevenLabs Agents, AI Backend, shared library, and local bridge settings.'
if (-not $PSCmdlet.ShouldProcess($gamePath, 'Back up existing mod files and install ElevenLabs Agents')) { return }

Assert-ScamGameClosed
$backupRelative = 'BepInEx\elevenlabs-agents-backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$backupPath = Get-ScamChildPath $gamePath $backupRelative
$existing = @{}
$written = [Collections.Generic.List[string]]::new()
foreach ($relative in $allRelatives) {
    $destination = Get-ScamChildPath $gamePath $relative
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        $backupFile = Get-ScamChildPath $backupPath $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backupFile)) | Out-Null
        [IO.File]::Copy($destination, $backupFile, $false)
        $existing[$relative] = $backupFile
    }
}
try {
    foreach ($relative in $relativeDlls) {
        $sourcePath = Get-ScamChildPath $packagePath $relative
        $destination = Get-ScamChildPath $gamePath $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        $written.Add($relative)
        [IO.File]::Copy($sourcePath, $destination, $true)
    }
    foreach ($relative in $misplaced) {
        $destination = Get-ScamChildPath $gamePath $relative
        if (Test-Path -LiteralPath $destination -PathType Leaf) {
            $written.Add($relative)
            Remove-Item -LiteralPath $destination -Force
        }
    }
    [IO.Directory]::CreateDirectory((Get-ScamChildPath $gamePath 'BepInEx\config')) | Out-Null
    $written.Add($configRelatives[0])
    Update-ModConfig (Get-ScamChildPath $gamePath $configRelatives[0]) ([ordered]@{
        '1 - General' = [ordered]@{ Mode = 'OpenAiCompatible'; Debug = 'false'; LogRequestBodies = 'false' }
        '2 - Endpoint' = [ordered]@{ BaseUrl = "http://127.0.0.1:$Port/v1"; ChatPath = '/chat/completions'; ApiKey = ''; ExtraHeaders = ''; TimeoutSeconds = '45'; MaxAttempts = '1'; AllowInvalidCertificates = 'false' }
        '3 - Model' = [ordered]@{ Model = 'elevenlabs-agent'; ProviderName = 'ElevenLabs Agents' }
    })
    $written.Add($configRelatives[1])
    Update-ModConfig (Get-ScamChildPath $gamePath $configRelatives[1]) ([ordered]@{
        Bridge = [ordered]@{ Enabled = 'true'; BaseUrl = "http://127.0.0.1:$Port"; TimeoutSeconds = '45' }
        Voice = [ordered]@{ Enabled = 'true' }
        Recognition = [ordered]@{ Enabled = 'true' }
    })
}
catch {
    foreach ($relative in $written) {
        $destination = Get-ScamChildPath $gamePath $relative
        if ($existing.ContainsKey($relative)) { [IO.File]::Copy($existing[$relative], $destination, $true) }
        elseif (Test-Path -LiteralPath $destination -PathType Leaf) { Remove-Item -LiteralPath $destination -Force }
    }
    throw 'Installation failed; changed mod files were restored from the backup.'
}
Write-Output 'Installed ElevenLabs Agents. Open Mods and click Rescan in the launcher.'
if ($existing.Count -gt 0) { Write-Output "Existing files backed up in: $backupPath" }
Write-Output 'Start the bridge with scripts\Start-Bridge.ps1 before launching the game.'
