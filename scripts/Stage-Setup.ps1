<#Copies the official, redistributable Setup payload beside a launcher; never runs Setup.#>
[CmdletBinding()]
param(
    [string]$SetupPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'upstream\Setup'),
    [string]$LauncherDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\Launcher')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Game-Paths.ps1')
$setupSource = [IO.Path]::GetFullPath($SetupPath)
$launcherRoot = [IO.Path]::GetFullPath($LauncherDirectory)
$setupDestination = Get-ScamChildPath $launcherRoot 'Setup'
$required = @(
    'setup.ps1', 'GameDir.ps1', 'install-bepinex.ps1', 'LICENSE', 'README.md',
    'tools\unstrip-awaiters.ps1', 'tools\find-vtable-breaks.ps1', 'tools\patch-preloader.ps1',
    'tools\compare-corlib.ps1', 'tools\scan-missing-apis.ps1', 'vendor\README.md',
    'vendor\corlib\mscorlib.dll', 'vendor\corlib\System.dll', 'vendor\corlib\System.Core.dll',
    'vendor\corlib\System.Configuration.dll', 'vendor\corlib\System.Numerics.dll',
    'vendor\corlib\System.Security.dll', 'vendor\corlib\System.Xml.dll',
    'vendor\corlib\Mono.Security.dll', 'vendor\corlib\LICENSE-Mono',
    'vendor\doorstop\winhttp.dll', 'vendor\doorstop\.doorstop_version',
    'vendor\doorstop\doorstop_config.ini', 'vendor\doorstop\LICENSE'
)
foreach ($relative in $required) {
    $file = Get-ScamChildPath $setupSource $relative
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) {
        throw "The official Setup checkout is missing a required payload file: $relative"
    }
}
$doorstopConfig = [IO.File]::ReadAllText((Join-Path $setupSource 'vendor\doorstop\doorstop_config.ini'))
if ($doorstopConfig -notmatch 'dll_search_path_override\s*=\s*unstripped_corlib') {
    throw 'The Setup Doorstop configuration does not point at unstripped_corlib.'
}
$sourcePrefix = $setupSource.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$files = @(
    foreach ($item in Get-ChildItem -LiteralPath $setupSource -Force) {
        if ($item.Name -in @('.git', '.github', '.gitignore', '.gitattributes')) { continue }
        if ($item.PSIsContainer) { Get-ChildItem -LiteralPath $item.FullName -File -Recurse -Force }
        else { $item }
    }
)
foreach ($file in $files) {
    $relative = $file.FullName.Substring($sourcePrefix.Length)
    $target = Get-ScamChildPath $setupDestination $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    [IO.File]::Copy($file.FullName, $target, $true)
}
$version = 'official Setup checkout (source archive)'
$gitCommand = Get-Command git -ErrorAction SilentlyContinue
if ($gitCommand -and (Test-Path -LiteralPath (Join-Path $setupSource '.git'))) {
    $description = & $gitCommand.Source -C $setupSource describe --tags --always --dirty 2>$null
    if ($LASTEXITCODE -eq 0 -and $description) { $version = [string]$description }
}
[IO.File]::WriteAllText((Join-Path $setupDestination 'SETUP-VERSION.txt'), $version + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
Write-Output "Staged official Setup payload: $($files.Count) files, version $version"
