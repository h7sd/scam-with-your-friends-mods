<# Installs TikTok Pocket and its original icon without touching browser profiles. #>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$GameDirectory,
    [string]$PackageDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Game-Paths.ps1')
. (Join-Path $PSScriptRoot 'TikTok-WorkerPackage.ps1')
$tikTokGame = Resolve-ScamGameDirectory $GameDirectory
Assert-ScamGameClosed
$tikTokPackage = [IO.Path]::GetFullPath($PackageDirectory)
foreach($requiredSetup in @('BepInEx/core/BepInEx.dll','winhttp.dll','unstripped_corlib/mscorlib.dll')){
    if(-not (Test-Path -LiteralPath (Get-ScamChildPath $tikTokGame $requiredSetup) -PathType Leaf)){
        throw 'Complete BepInEx Setup in the launcher before installing TikTok Pocket.'
    }
}
$tikTokPluginRelative = 'BepInEx/plugins/ScamWYF.TikTokPocket.dll'
$tikTokIconRelative = 'BepInEx/plugins/TikTokPocket/tiktok-pocket.png'
$tikTokPluginSource = Get-ScamChildPath $tikTokPackage $tikTokPluginRelative
$tikTokIconSource = Get-ScamChildPath $tikTokPackage $tikTokIconRelative
foreach($requiredFile in @($tikTokPluginSource,$tikTokIconSource)){
    if(-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)){throw 'The package is missing TikTok Pocket or its icon.'}
}
$tikTokAssembly = [Reflection.AssemblyName]::GetAssemblyName($tikTokPluginSource)
if($tikTokAssembly.Name -ne 'ScamWYF.TikTokPocket' -or $tikTokAssembly.Version -ne [version]'1.0.1.0'){throw 'Unexpected TikTok Pocket plugin identity or version.'}
$tikTokIconBytes = [IO.File]::ReadAllBytes($tikTokIconSource)
if($tikTokIconBytes.Length -lt 8 -or $tikTokIconBytes.Length -gt 1048576 -or
    [BitConverter]::ToString($tikTokIconBytes,0,8) -ne '89-50-4E-47-0D-0A-1A-0A'){
    throw 'The package icon must be a valid PNG smaller than 1 MiB.'
}
$tikTokCopies = [Collections.Generic.List[string]]::new()
$tikTokCopies.Add($tikTokPluginRelative)
$tikTokCopies.Add($tikTokIconRelative)
$tikTokWorkerRelative = 'BepInEx/plugins/TikTokPocket/BrowserWorker'
$tikTokWorkerPackage = Get-TikTokWorkerPackage -WorkerRoot (Get-ScamChildPath $tikTokPackage $tikTokWorkerRelative) -Version '1.0.1'
$tikTokCopies.Add($tikTokWorkerRelative+'/worker-files.json')
foreach($workerFile in $tikTokWorkerPackage.Files){$tikTokCopies.Add($tikTokWorkerRelative+'/'+$workerFile.Path)}
$tikTokCoreRelative = 'BepInEx/core/ScamWYF.Modding.Core.dll'
$tikTokCoreDestination = Get-ScamChildPath $tikTokGame $tikTokCoreRelative
$tikTokNeedsCore = -not (Test-Path -LiteralPath $tikTokCoreDestination -PathType Leaf)
if(-not $tikTokNeedsCore){$tikTokNeedsCore = [Reflection.AssemblyName]::GetAssemblyName($tikTokCoreDestination).Version -lt [version]'1.0.4.0'}
if($tikTokNeedsCore){
    $tikTokCoreSource = Get-ScamChildPath $tikTokPackage $tikTokCoreRelative
    if(-not (Test-Path -LiteralPath $tikTokCoreSource -PathType Leaf) -or
        [Reflection.AssemblyName]::GetAssemblyName($tikTokCoreSource).Version -lt [version]'1.0.4.0'){
        throw 'The package is missing the compatible shared mod library (1.0.4 or newer).'
    }
    $tikTokCopies.Add($tikTokCoreRelative)
}
$tikTokDisabledRelative = 'BepInEx/plugins_disabled/ScamWYF.TikTokPocket.dll'
foreach($relative in @($tikTokCopies)+@($tikTokDisabledRelative)){
    Assert-TikTokWorkerPath -Root $tikTokGame -Path (Get-ScamChildPath $tikTokGame $relative)
}
Write-Output "Game folder: $tikTokGame"
Write-Output "Installs TikTok Pocket $($tikTokAssembly.Version)."
Write-Output "Browser worker: $($tikTokWorkerPackage.Files.Count) manifest-verified files."
if(-not $PSCmdlet.ShouldProcess($tikTokGame,'Back up and install TikTok Pocket, its icon and its local browser worker')){return}
Assert-ScamGameClosed
if(Get-Process -Name 'ScamWYF.TikTokBrowser' -ErrorAction SilentlyContinue){throw 'Close TikTok Pocket and the game before updating the browser worker.'}
$tikTokBackup = Get-ScamChildPath $tikTokGame ('BepInEx/tiktok-pocket-backups/'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8))
$tikTokPrevious = @{}
$tikTokWritten = [Collections.Generic.List[string]]::new()
foreach($relative in @($tikTokCopies)+@($tikTokDisabledRelative)){
    $target = Get-ScamChildPath $tikTokGame $relative
    if(Test-Path -LiteralPath $target -PathType Leaf){
        $backup = Get-ScamChildPath $tikTokBackup $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup)) | Out-Null
        [IO.File]::Copy($target,$backup,$false)
        $tikTokPrevious[$relative] = $backup
    }
}
try {
    foreach($relative in $tikTokCopies){
        $target = Get-ScamChildPath $tikTokGame $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        $tikTokWritten.Add($relative)
        [IO.File]::Copy((Get-ScamChildPath $tikTokPackage $relative),$target,$true)
    }
    $disabled = Get-ScamChildPath $tikTokGame $tikTokDisabledRelative
    if(Test-Path -LiteralPath $disabled -PathType Leaf){$tikTokWritten.Add($tikTokDisabledRelative); Remove-Item -LiteralPath $disabled -Force}
}
catch {
    foreach($relative in $tikTokWritten){
        $target = Get-ScamChildPath $tikTokGame $relative
        if($tikTokPrevious.ContainsKey($relative)){[IO.File]::Copy($tikTokPrevious[$relative],$target,$true)}
        elseif(Test-Path -LiteralPath $target -PathType Leaf){Remove-Item -LiteralPath $target -Force}
    }
    throw 'TikTok Pocket installation failed; replaced files were restored from backup.'
}
Write-Output 'Installed TikTok Pocket. Buy it in Scamazon after starting the game.'
if($tikTokPrevious.Count -gt 0){Write-Output "Previous mod files backed up in: $tikTokBackup"}
