[CmdletBinding()]
param([string]$ProjectDirectory = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Package-CatalogueMods.ps1') -ProjectDirectory $ProjectDirectory -Mod RequestedPayout
