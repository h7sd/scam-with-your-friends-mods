<#
Requested Payout post-install verification and usage notes. Licence notices are
prepended to the release copy by Package-CatalogueMods.ps1.
#>
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$GameDir)
$ErrorActionPreference='Stop'
$payoutGameRoot=[IO.Path]::GetFullPath($GameDir)
$payoutInstalledPath=Join-Path $payoutGameRoot 'BepInEx/plugins/ScamWYF.RequestedPayout.dll'
if(-not (Test-Path -LiteralPath $payoutInstalledPath -PathType Leaf)){throw 'Requested Payout is not installed in the selected game folder.'}
$payoutInstalledIdentity=[Reflection.AssemblyName]::GetAssemblyName($payoutInstalledPath)
if($payoutInstalledIdentity.Name -ne 'ScamWYF.RequestedPayout' -or $payoutInstalledIdentity.Version -ne [version]'1.2.0.0'){throw 'This setup script expects Requested Payout 1.2.0.'}
Write-Output 'Requested Payout 1.2.0 is installed. No additional setup or downloads are needed.'
Write-Output 'Credit card: your stated price is added to the original reward after successful verification.'
Write-Output 'Gift card: your stated price replaces the original reward after the correct fictional code is verified.'
Write-Output 'In multiplayer, the host must install the mod. Open F1 to adjust its settings.'
