<#
Requested Payout post-install verification and usage notes. Licence notices are
prepended to the release copy by Package-CatalogueMods.ps1.
#>
[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [Parameter(Mandatory=$true)][string]$GameDir,
    [string]$DependencyDirectory
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2
__DEPENDENCY_SETUP_HELPER__
$payoutGameRoot=[IO.Path]::GetFullPath($GameDir).TrimEnd('\','/');Assert-CatalogueDependencyPath $payoutGameRoot $payoutGameRoot
$payoutInstalledPath=Get-CatalogueDependencyChild $payoutGameRoot 'BepInEx/plugins/ScamWYF.RequestedPayout.dll'
if(-not (Test-Path -LiteralPath $payoutInstalledPath -PathType Leaf)){throw 'Requested Payout is not installed in the selected game folder.'}
$payoutInstalledIdentity=[Reflection.AssemblyName]::GetAssemblyName($payoutInstalledPath)
if($payoutInstalledIdentity.Name -ne 'ScamWYF.RequestedPayout' -or $payoutInstalledIdentity.Version -ne [version]'1.2.0.0'){throw 'This setup script expects Requested Payout 1.2.0.'}
$coreReady=Install-CatalogueDependency -GameDir $payoutGameRoot -Name Core -DependencyDirectory $DependencyDirectory -Caller $PSCmdlet
if(-not $coreReady) {return}
Write-Output 'Requested Payout 1.2.0 and its shared Core library are installed.'
Write-Output 'Credit card: your stated price is added to the original reward after successful verification.'
Write-Output 'Gift card: your stated price replaces the original reward after the correct fictional code is verified.'
Write-Output 'In multiplayer, the host must install the mod. Open F1 to adjust its settings.'
