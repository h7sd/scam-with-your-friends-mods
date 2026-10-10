<# Installs the requested-payout plugin; existing files are backed up and restored on failure. #>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$GameDirectory,
    [string]$PackageDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Game-Paths.ps1')
$gamePath = Resolve-ScamGameDirectory $GameDirectory
Assert-ScamGameClosed
$packagePath = [IO.Path]::GetFullPath($PackageDirectory)
$loader = Get-ScamChildPath $gamePath 'BepInEx\core\BepInEx.dll'
if (-not (Test-Path -LiteralPath $loader -PathType Leaf)) { throw 'Complete BepInEx Setup before installing Requested Payout.' }
if (-not (Test-Path -LiteralPath (Get-ScamChildPath $gamePath 'winhttp.dll') -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Get-ScamChildPath $gamePath 'unstripped_corlib\mscorlib.dll') -PathType Leaf)) {
    throw 'Game modding setup is incomplete. Complete Setup in the launcher first.'
}
$pluginRelative = 'BepInEx\plugins\ScamWYF.RequestedPayout.dll'
$pluginSource = Get-ScamChildPath $packagePath $pluginRelative
if (-not (Test-Path -LiteralPath $pluginSource -PathType Leaf)) { throw 'Package is missing ScamWYF.RequestedPayout.dll.' }
$pluginAssembly = [Reflection.AssemblyName]::GetAssemblyName($pluginSource)
if ($pluginAssembly.Name -ne 'ScamWYF.RequestedPayout') { throw 'Unexpected payout plugin assembly.' }
$copyRelatives = [Collections.Generic.List[string]]::new()
$copyRelatives.Add($pluginRelative)
$coreRelative = 'BepInEx\core\ScamWYF.Modding.Core.dll'
$coreDestination = Get-ScamChildPath $gamePath $coreRelative
$coreRequired = -not (Test-Path -LiteralPath $coreDestination -PathType Leaf)
if (-not $coreRequired) {
    $coreRequired = [Reflection.AssemblyName]::GetAssemblyName($coreDestination).Version -lt [version]'1.0.4.0'
}
if ($coreRequired) {
    $coreSource = Get-ScamChildPath $packagePath $coreRelative
    if (-not (Test-Path -LiteralPath $coreSource -PathType Leaf) -or
        [Reflection.AssemblyName]::GetAssemblyName($coreSource).Version -lt [version]'1.0.4.0') {
        throw 'Package is missing a compatible shared mod library (1.0.4 or newer).'
    }
    $copyRelatives.Add($coreRelative)
}
$disabledRelative = 'BepInEx\plugins_disabled\ScamWYF.RequestedPayout.dll'
$backupRelatives = @($copyRelatives) + @($disabledRelative)
Write-Output "Game folder: $gamePath"
Write-Output "Installs Requested Payout $($pluginAssembly.Version)."
if (-not $PSCmdlet.ShouldProcess($gamePath, 'Back up existing payout files and install Requested Payout')) { return }
Assert-ScamGameClosed
$backupRelative = 'BepInEx\requested-payout-backups\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$backupPath = Get-ScamChildPath $gamePath $backupRelative
$existing = @{}
$written = [Collections.Generic.List[string]]::new()
foreach ($relative in $backupRelatives) {
    $destination = Get-ScamChildPath $gamePath $relative
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        $backupFile = Get-ScamChildPath $backupPath $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backupFile)) | Out-Null
        [IO.File]::Copy($destination, $backupFile, $false)
        $existing[$relative] = $backupFile
    }
}
try {
    foreach ($relative in $copyRelatives) {
        $destination = Get-ScamChildPath $gamePath $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        $written.Add($relative)
        [IO.File]::Copy((Get-ScamChildPath $packagePath $relative), $destination, $true)
    }
    $disabled = Get-ScamChildPath $gamePath $disabledRelative
    if (Test-Path -LiteralPath $disabled -PathType Leaf) { $written.Add($disabledRelative); Remove-Item -LiteralPath $disabled -Force }
}
catch {
    foreach ($relative in $written) {
        $destination = Get-ScamChildPath $gamePath $relative
        if ($existing.ContainsKey($relative)) { [IO.File]::Copy($existing[$relative], $destination, $true) }
        elseif (Test-Path -LiteralPath $destination -PathType Leaf) { Remove-Item -LiteralPath $destination -Force }
    }
    throw 'Requested Payout installation failed; changed files were restored from the backup.'
}
Write-Output 'Installed Requested Payout. Start a new call after launching the game.'
if ($existing.Count -gt 0) { Write-Output "Previous payout files backed up in: $backupPath" }
