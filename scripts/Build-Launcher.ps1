<#
Builds a separate launcher with bundled local ElevenLabs Agents and Wunschsumme entries.
The upstream checkout stays untouched. Build the AI DLLs into dist/BepInEx and the payout DLL first.
Use -OutputDirectory to build a release beside the running launcher without replacing its executable.
#>
[CmdletBinding()]
param(
    [string]$ProjectDirectory = (Split-Path -Parent $PSScriptRoot),
    [string]$DotnetPath,
    [string]$NugetSource,
    [string]$SetupPath,
    [string]$OutputDirectory,
    [string]$PayoutModPath
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Game-Paths.ps1')
$projectRoot = [IO.Path]::GetFullPath($ProjectDirectory)
if (-not $SetupPath) { $SetupPath = Join-Path $projectRoot 'upstream\Setup' }
if (-not (Test-Path -LiteralPath (Join-Path $SetupPath 'setup.ps1') -PathType Leaf)) {
    throw 'The official Setup checkout is required. Place it at upstream\Setup or pass -SetupPath.'
}
$upstream = Join-Path $projectRoot 'upstream\Launcher'
$dist = Join-Path $projectRoot 'dist'
if (-not $PayoutModPath) { $PayoutModPath = Join-Path $projectRoot 'payout-mod\bin\ScamWYF.RequestedPayout.dll' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $dist 'Launcher' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$build = Join-Path $projectRoot ('build\launcher-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
if (-not $DotnetPath) {
    $portable = Join-Path $projectRoot 'build\tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $portable -PathType Leaf) { $DotnetPath = $portable }
    else {
        $command = Get-Command dotnet -ErrorAction SilentlyContinue
        if (-not $command) { throw 'Building the launcher requires a .NET SDK.' }
        $DotnetPath = $command.Source
    }
}
$sdks = & $DotnetPath --list-sdks
if (-not $sdks) { throw 'The dotnet command has no installed SDK. Use -DotnetPath with a portable .NET SDK.' }
[IO.Directory]::CreateDirectory($build) | Out-Null
[IO.Directory]::CreateDirectory($output) | Out-Null
Copy-Item -LiteralPath (Join-Path $upstream 'src') -Destination (Join-Path $build 'src') -Recurse
Copy-Item -LiteralPath (Join-Path $upstream 'vendor') -Destination (Join-Path $build 'vendor') -Recurse
Copy-Item -LiteralPath (Join-Path $upstream 'Launcher.csproj') -Destination (Join-Path $build 'Launcher.csproj')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'launcher\LocalModPackage.cs') -Destination (Join-Path $build 'src\LocalModPackage.cs')

function Replace-LauncherSource {
    param([string]$Relative, [string]$Before, [string]$After)
    $path = Get-ScamChildPath $build $Relative
    $source = [IO.File]::ReadAllText($path)
    $source = $source.Replace("`r`n", "`n")
    $Before = $Before.Replace("`r`n", "`n")
    $After = $After.Replace("`r`n", "`n")
    if (-not $source.Contains($Before)) { throw "The upstream launcher changed: patch not found in $Relative" }
    [IO.File]::WriteAllText($path, $source.Replace($Before, $After), [Text.UTF8Encoding]::new($false))
}

Replace-LauncherSource 'src\views\GetModsView.xaml.cs' '                var problems = new List<string>();' @'
                var problems = new List<string>();
                found.AddRange(LocalModPackage.Available(problems));
'@
Replace-LauncherSource 'src\views\GetModsView.xaml.cs' 'Checking GitHub for the latest releases...' 'Checking the bundled package and GitHub releases...'
Replace-LauncherSource 'src\views\GetModsView.xaml.cs' '            if (_rows.Count == 0 && !_busy) CheckForUpdates();' @'
            if (_rows.Count == 0 && !_busy)
            {
                try
                {
                    var problems = new List<string>();
                    foreach (var local in LocalModPackage.Available(problems))
                        _rows.Add(new ReleaseRow(local, DescribeInstalled(local.Source)));
                    Summary.Text = _rows.Count + " bundled local package(s) ready. Check for updates to load the GitHub catalogue.";
                    if (problems.Count > 0) Summary.Text += " " + string.Join(" ", problems.ToArray());
                    if (_rows.Count > 0) List.SelectedIndex = 0;
                    UpdateButtons();
                }
                catch (Exception ex) { Summary.Text = ex.Message; }
            }
'@
Replace-LauncherSource 'src\views\GetModsView.xaml.cs' ' mod(s) checked against GitHub.' ' mod(s) available from the local package and GitHub.'
Replace-LauncherSource 'src\views\GetModsView.xaml.cs' '                    "There is no undo and no backup. A replaced file cannot be recovered from here.",' @'
                    (LocalModPackage.IsLocal(plan.Release)
                        ? LocalModPackage.BackupDescription(plan.Release)
                        : "There is no undo and no backup. A replaced file cannot be recovered from here."),
'@
Replace-LauncherSource 'src\ModInstaller.cs' @'
        var folder = WorkingFolder(token);

        if (status != null) status("Downloading "
'@ @'
        if (LocalModPackage.IsLocalAsset(asset)) return LocalModPackage.Fetch(asset, token, status);
        var folder = WorkingFolder(token);

        if (status != null) status("Downloading "
'@
Replace-LauncherSource 'src\ModInstaller.cs' @'
        public static string[] Apply(InstallPlan plan)
        {
            var written = new List<string>();
'@ @'
        public static string[] Apply(InstallPlan plan)
        {
            if (LocalModPackage.IsLocal(plan.Release)) return LocalModPackage.Install(plan);
            var written = new List<string>();
'@
Replace-LauncherSource 'src\ModInstaller.cs' '            text.AppendLine("Files come from " + plan.Release.Package.Name + " on GitHub, " +' '            text.AppendLine("Files come from " + plan.Release.Package.Name + (LocalModPackage.IsLocal(plan.Release) ? " bundled with this launcher, " : " on GitHub, ") +'
Replace-LauncherSource 'src\MainWindow.xaml.cs' 'Scam With Your Friends - modding ' 'Scam With Your Friends - ElevenLabs Agents + Wunschsumme - Launcher '
Replace-LauncherSource 'src\views\GetModsView.xaml' 'Mods are downloaded over https and checked against the SHA-256 GitHub publishes for them.' 'The ElevenLabs Agents and Wunschsumme packages are bundled locally with this launcher. Other mods are downloaded over https and checked against the SHA-256 GitHub publishes for them.'

$packageFolder = Join-Path $output 'LocalPackages'
[IO.Directory]::CreateDirectory($packageFolder) | Out-Null
$packages = @(
    [pscustomobject]@{ Name = 'ElevenLabs-Agents-1.0.1.zip'; Files = @(
        (Join-Path $dist 'BepInEx\plugins\ScamWYF.ElevenLabsAgents.dll'),
        (Join-Path $dist 'BepInEx\plugins\ScamWYF.AiBackend.dll'),
        (Join-Path $dist 'BepInEx\core\ScamWYF.Modding.Core.dll')
    ) },
    [pscustomobject]@{ Name = 'Wunschsumme-1.1.1.zip'; Files = @(
        [IO.Path]::GetFullPath($PayoutModPath),
        (Join-Path $dist 'BepInEx\core\ScamWYF.Modding.Core.dll')
    ) }
)
foreach ($package in $packages) {
    foreach ($file in $package.Files) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Build the mod first; missing $file" }
    }
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($package in $packages) {
    $zipPath = Join-Path $packageFolder $package.Name
    $zipStream = [IO.File]::Open($zipPath, [IO.FileMode]::Create)
    $archive = [IO.Compression.ZipArchive]::new($zipStream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $package.Files) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file, [IO.Path]::GetFileName($file), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $archive.Dispose(); $zipStream.Dispose() }
}

$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$buildArguments = @('build', (Join-Path $build 'Launcher.csproj'), '-c', 'Release', '-o', $output, '-p:Version=1.1.1', '-p:InformationalVersion=1.1.1-localmods')
if ($NugetSource) { $buildArguments += @('--source', $NugetSource, '-p:NuGetAudit=false') }
& $DotnetPath @buildArguments
if ($LASTEXITCODE -ne 0) { throw 'The local launcher build failed.' }
& (Join-Path $PSScriptRoot 'Stage-Setup.ps1') -SetupPath $SetupPath -LauncherDirectory $output
Copy-Item -LiteralPath (Join-Path $upstream 'LICENSE') -Destination (Join-Path $output 'UPSTREAM-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $upstream 'vendor\README.md') -Destination (Join-Path $output 'MONO-CECIL-NOTICE.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses\Mono.Cecil-LICENSE.txt') -Destination (Join-Path $output 'Mono.Cecil-LICENSE.txt')
$distScripts = Join-Path ([IO.Path]::GetDirectoryName($output)) 'scripts'
[IO.Directory]::CreateDirectory($distScripts) | Out-Null
foreach ($file in @('Install-Mod.ps1', 'Install-PayoutMod.ps1', 'Game-Paths.ps1', 'Start-Bridge.ps1', 'Start-Backend.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $distScripts $file) -Force
}
Write-Output "Built local launcher: $(Join-Path $output 'ScamWYF.Launcher.exe')"
