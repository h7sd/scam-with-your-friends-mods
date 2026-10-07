<# Isolated filesystem checks. Never uses the real game's files as destinations. #>
[CmdletBinding()]
param([string]$ProjectDirectory = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath($ProjectDirectory)
$fixtureRoot = Join-Path $projectRoot ('build\payout-installer-check-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$fixtureGame = Join-Path $fixtureRoot 'game'
$fixturePackage = Join-Path $fixtureRoot 'package'
$installer = Join-Path $projectRoot 'scripts\Install-PayoutMod.ps1'
$dllSource = Join-Path $projectRoot 'payout-mod\bin\ScamWYF.RequestedPayout.dll'
$coreSource = Join-Path $projectRoot 'dist\BepInEx\core\ScamWYF.Modding.Core.dll'
if (-not (Test-Path -LiteralPath $dllSource)) { throw 'Build the payout mod before running its installer checks.' }
foreach ($folder in @('BepInEx\core','BepInEx\plugins','BepInEx\config','unstripped_corlib')) {
    [IO.Directory]::CreateDirectory((Join-Path $fixtureGame $folder)) | Out-Null
}
foreach ($folder in @('BepInEx\core','BepInEx\plugins')) { [IO.Directory]::CreateDirectory((Join-Path $fixturePackage $folder)) | Out-Null }
foreach ($relative in @('Scam With Your Friends.exe','winhttp.dll','BepInEx\core\BepInEx.dll','unstripped_corlib\mscorlib.dll')) {
    [IO.File]::WriteAllText((Join-Path $fixtureGame $relative), 'fixture')
}
$pluginDestination = Join-Path $fixtureGame 'BepInEx\plugins\ScamWYF.RequestedPayout.dll'
[IO.File]::Copy($dllSource, (Join-Path $fixturePackage 'BepInEx\plugins\ScamWYF.RequestedPayout.dll'))
[IO.File]::Copy($coreSource, (Join-Path $fixturePackage 'BepInEx\core\ScamWYF.Modding.Core.dll'))
[IO.File]::Copy($coreSource, (Join-Path $fixtureGame 'BepInEx\core\ScamWYF.Modding.Core.dll'))
$voiceConfig = Join-Path $fixtureGame 'BepInEx\config\com.community.scamwyf.elevenlabsagents.cfg'
[IO.File]::WriteAllText($voiceConfig, '[Voice]' + [Environment]::NewLine + 'Enabled = true')
$voiceHash = (Get-FileHash -LiteralPath $voiceConfig).Hash
$coreHash = (Get-FileHash -LiteralPath (Join-Path $fixtureGame 'BepInEx\core\ScamWYF.Modding.Core.dll')).Hash

# Override only the process lookup in this test's child scope. The installer is otherwise unchanged.
& {
    function Get-Process { [CmdletBinding()]param([string]$Name) return }
    & $installer -GameDirectory $fixtureGame -PackageDirectory $fixturePackage -WhatIf | Out-Null
    if (Test-Path -LiteralPath $pluginDestination) { throw 'WhatIf wrote the plugin.' }
    & $installer -GameDirectory $fixtureGame -PackageDirectory $fixturePackage | Out-Null
    if ((Get-FileHash -LiteralPath $pluginDestination).Hash -ne (Get-FileHash -LiteralPath $dllSource).Hash) { throw 'Installed plugin differs from the build.' }
    if ((Get-FileHash -LiteralPath $voiceConfig).Hash -ne $voiceHash) { throw 'Voice configuration changed.' }
    if ((Get-FileHash -LiteralPath (Join-Path $fixtureGame 'BepInEx\core\ScamWYF.Modding.Core.dll')).Hash -ne $coreHash) { throw 'Existing compatible shared core changed.' }
    [IO.File]::WriteAllText($pluginDestination, 'old-payout-plugin')
    & $installer -GameDirectory $fixtureGame -PackageDirectory $fixturePackage | Out-Null
    $backups = @(Get-ChildItem -LiteralPath (Join-Path $fixtureGame 'BepInEx\requested-payout-backups') -File -Recurse)
    if ($backups.Count -ne 1 -or [IO.File]::ReadAllText($backups[0].FullName) -ne 'old-payout-plugin') { throw 'Previous plugin backup is missing or wrong.' }
}

# An active game must stop the installation before creating any backup or writing a file.
$beforeBackups = @(Get-ChildItem -LiteralPath (Join-Path $fixtureGame 'BepInEx\requested-payout-backups') -Directory).Count
& {
    function Get-Process { [CmdletBinding()]param([string]$Name) [pscustomobject]@{ Name=$Name } }
    $rejected = $false
    try { & $installer -GameDirectory $fixtureGame -PackageDirectory $fixturePackage | Out-Null }
    catch { if ($_.Exception.Message -match 'Close Scam') { $rejected = $true } else { throw } }
    if (-not $rejected) { throw 'Installer allowed a running game.' }
}
if (@(Get-ChildItem -LiteralPath (Join-Path $fixtureGame 'BepInEx\requested-payout-backups') -Directory).Count -ne $beforeBackups) { throw 'Running-game rejection still created a backup.' }
Write-Output 'Payout installer checks passed: WhatIf, installation, preserved voice/core, backup, active-game rejection.'
