[CmdletBinding()]
param([string]$ProjectDirectory=(Split-Path -Parent $PSScriptRoot),[switch]$UseExistingRuntime)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'Package-CatalogueMods.ps1') -ProjectDirectory $ProjectDirectory -Mod TikTok -UseExistingRuntime:$UseExistingRuntime
