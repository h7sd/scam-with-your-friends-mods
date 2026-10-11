<# Isolated installer checks; destinations are only under the workspace's build folder. #>
[CmdletBinding()]
param([string]$ProjectDirectory = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$testTikTokRoot = [IO.Path]::GetFullPath($ProjectDirectory)
$testTikTokFixture = Join-Path $testTikTokRoot ('build/tiktok-installer-'+[guid]::NewGuid().ToString('N').Substring(0,8))
$testTikTokGame = Join-Path $testTikTokFixture 'game'
$testTikTokPackage = Join-Path $testTikTokFixture 'package'
$testTikTokInstaller = Join-Path $testTikTokRoot 'scripts/Install-TikTokMod.ps1'
$testTikTokPlugin = 'BepInEx/plugins/ScamWYF.TikTokPocket.dll'
$testTikTokIcon = 'BepInEx/plugins/TikTokPocket/tiktok-pocket.png'
$testTikTokCore = 'BepInEx/core/ScamWYF.Modding.Core.dll'
foreach($relative in @('BepInEx/core','BepInEx/plugins/TikTokPocket','BepInEx/config','unstripped_corlib')){
    [IO.Directory]::CreateDirectory((Join-Path $testTikTokGame $relative)) | Out-Null
}
foreach($relative in @('BepInEx/core','BepInEx/plugins/TikTokPocket')){[IO.Directory]::CreateDirectory((Join-Path $testTikTokPackage $relative)) | Out-Null}
foreach($relative in @('Scam With Your Friends.exe','winhttp.dll','BepInEx/core/BepInEx.dll','unstripped_corlib/mscorlib.dll')){
    [IO.File]::WriteAllText((Join-Path $testTikTokGame $relative),'fixture')
}
[IO.File]::Copy((Join-Path $testTikTokRoot 'tiktok-mod/bin/ScamWYF.TikTokPocket.dll'),(Join-Path $testTikTokPackage $testTikTokPlugin))
[IO.File]::Copy((Join-Path $testTikTokRoot 'tiktok-mod/assets/tiktok-pocket.png'),(Join-Path $testTikTokPackage $testTikTokIcon))
foreach($directory in @($testTikTokGame,$testTikTokPackage)){
    [IO.File]::Copy((Join-Path $testTikTokRoot ('dist/'+$testTikTokCore)),(Join-Path $directory $testTikTokCore))
}
$testTikTokProtected = @(
    (Join-Path $testTikTokGame 'BepInEx/config/com.community.scamwyf.elevenlabsagents.cfg'),
    (Join-Path $testTikTokGame 'BepInEx/config/com.community.scamwyf.requestedpayout.cfg'),
    (Join-Path $testTikTokGame 'BepInEx/plugins/TikTokPocket/Profile/local-session-fixture')
)
foreach($protected in $testTikTokProtected){[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($protected)) | Out-Null; [IO.File]::WriteAllText($protected,'unchanged-fixture')}
$testTikTokCoreHash = (Get-FileHash -LiteralPath (Join-Path $testTikTokGame $testTikTokCore)).Hash
& {
    function Get-Process {[CmdletBinding()]param([string]$Name) return}
    & $testTikTokInstaller -GameDirectory $testTikTokGame -PackageDirectory $testTikTokPackage -WhatIf | Out-Null
    if(Test-Path -LiteralPath (Join-Path $testTikTokGame $testTikTokPlugin)){throw 'WhatIf wrote the TikTok plugin.'}
    & $testTikTokInstaller -GameDirectory $testTikTokGame -PackageDirectory $testTikTokPackage | Out-Null
    foreach($relative in @($testTikTokPlugin,$testTikTokIcon)){
        if((Get-FileHash -LiteralPath (Join-Path $testTikTokPackage $relative)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $testTikTokGame $relative)).Hash){throw 'Installed TikTok file differs from package.'}
    }
    foreach($protected in $testTikTokProtected){if([IO.File]::ReadAllText($protected) -ne 'unchanged-fixture'){throw 'Installer changed configuration or browser data.'}}
    if((Get-FileHash -LiteralPath (Join-Path $testTikTokGame $testTikTokCore)).Hash -ne $testTikTokCoreHash){throw 'Compatible shared mod library changed.'}
    [IO.File]::WriteAllText((Join-Path $testTikTokGame $testTikTokPlugin),'old-plugin-fixture')
    & $testTikTokInstaller -GameDirectory $testTikTokGame -PackageDirectory $testTikTokPackage | Out-Null
    $oldPluginBackup = @(Get-ChildItem -LiteralPath (Join-Path $testTikTokGame 'BepInEx/tiktok-pocket-backups') -File -Recurse | Where-Object {$_.Name -eq 'ScamWYF.TikTokPocket.dll' -and [IO.File]::ReadAllText($_.FullName) -eq 'old-plugin-fixture'})
    if($oldPluginBackup.Count -ne 1){throw 'Existing TikTok plugin was not backed up.'}
    [IO.File]::WriteAllText((Join-Path $testTikTokPackage $testTikTokIcon),'not-a-png')
    $beforeBadIcon = (Get-FileHash -LiteralPath (Join-Path $testTikTokGame $testTikTokPlugin)).Hash
    $badIconRejected = $false
    try {& $testTikTokInstaller -GameDirectory $testTikTokGame -PackageDirectory $testTikTokPackage | Out-Null}
    catch {if($_.Exception.Message -match 'valid PNG'){$badIconRejected=$true}else{throw}}
    if(-not $badIconRejected -or (Get-FileHash -LiteralPath (Join-Path $testTikTokGame $testTikTokPlugin)).Hash -ne $beforeBadIcon){throw 'Invalid icon was not rejected before installation.'}
}
$testTikTokBeforeRunning = (Get-FileHash -LiteralPath (Join-Path $testTikTokGame $testTikTokPlugin)).Hash
& {
    function Get-Process {[CmdletBinding()]param([string]$Name) [pscustomobject]@{Name=$Name}}
    $runningRejected=$false
    try {& $testTikTokInstaller -GameDirectory $testTikTokGame -PackageDirectory $testTikTokPackage | Out-Null}
    catch {if($_.Exception.Message -match 'Close Scam'){$runningRejected=$true}else{throw}}
    if(-not $runningRejected){throw 'Installer allowed a running game.'}
}
if((Get-FileHash -LiteralPath (Join-Path $testTikTokGame $testTikTokPlugin)).Hash -ne $testTikTokBeforeRunning){throw 'Running-game rejection changed files.'}
Write-Output 'TikTok installer checks passed: WhatIf, plugin/icon hashes, preserved profiles/config/core, backups, invalid icon, active-game rejection.'
