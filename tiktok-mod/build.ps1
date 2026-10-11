[CmdletBinding()]
param([string]$GameDir, [string]$CscDll, [string]$DotnetPath)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $GameDir) { $GameDir = $env:SWYG_GAME_DIR }
if (-not $GameDir) {
    foreach ($name in @('Scam With Your Friends', 'Scam With Your Friends Playtest')) {
        $candidate = Join-Path "${env:ProgramFiles(x86)}\Steam\steamapps\common" $name
        if (Test-Path -LiteralPath (Join-Path $candidate 'Scam With Your Friends_Data\Managed\Assembly-CSharp.dll')) {
            $GameDir = $candidate
            break
        }
    }
}
if (-not $GameDir) { throw 'Game not found. Pass -GameDir or set SWYG_GAME_DIR.' }
$managed = Join-Path $GameDir 'Scam With Your Friends_Data\Managed'
$core = Join-Path $GameDir 'BepInEx\core'
$libraryRoot = Join-Path $repoRoot 'upstream\AI-Backend\vendor\ScamWYF.Modding.Core'
$portableDotnet = Join-Path $repoRoot 'build\tools\dotnet'
if (-not $DotnetPath) {
    $portableExecutable = Join-Path $portableDotnet 'dotnet.exe'
    if (Test-Path -LiteralPath $portableExecutable -PathType Leaf) { $DotnetPath = $portableExecutable }
    else {
        $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
        if ($dotnetCommand) { $DotnetPath = $dotnetCommand.Source }
    }
}
if (-not $DotnetPath -or -not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
    throw 'The dotnet runtime is missing. Pass -DotnetPath.'
}
if (-not $CscDll) {
    $compilerCandidates = @()
    foreach ($runtimeRoot in @($portableDotnet, (Join-Path $env:ProgramFiles 'dotnet'))) {
        $compilerCandidates += @(Get-ChildItem -Path (Join-Path $runtimeRoot 'sdk\*\Roslyn\bincore\csc.dll') -ErrorAction SilentlyContinue)
    }
    $compiler = $compilerCandidates | Sort-Object FullName -Descending | Select-Object -First 1
    if ($compiler) { $CscDll = $compiler.FullName }
}
if (-not $CscDll -or -not (Test-Path -LiteralPath $CscDll -PathType Leaf)) {
    throw 'Roslyn compiler not found. Pass -CscDll or obtain a .NET SDK.'
}

$commonReferences = @(
    'mscorlib.dll', 'System.dll', 'System.Core.dll', 'UnityEngine.dll', 'UnityEngine.CoreModule.dll',
    'UnityEngine.IMGUIModule.dll', 'UnityEngine.UIElementsModule.dll', 'UnityEngine.TextRenderingModule.dll',
    'UnityEngine.TextCoreTextEngineModule.dll', 'UnityEngine.TextCoreFontEngineModule.dll', 'Unity.InputSystem.dll'
) | ForEach-Object { Join-Path $managed $_ }
$commonReferences += (Join-Path $core 'BepInEx.dll'), (Join-Path $core '0Harmony.dll')
$pluginReferences = @(
    'Assembly-CSharp.dll', 'ScriptsAssDef.dll', 'Mirror.dll', 'UniTask.dll', 'Newtonsoft.Json.dll',
    'UnityEngine.UI.dll', 'UnityEngine.UIModule.dll', 'UnityEngine.ImageConversionModule.dll',
    'UnityEngine.InputLegacyModule.dll', 'UnityEngine.AudioModule.dll', 'UnityEngine.UnityWebRequestModule.dll',
    'VoltstroStudios.UnityWebBrowser.dll', 'VoltstroStudios.UnityWebBrowser.Shared.dll',
    'VoltstroStudios.UnityWebBrowser.Communication.Pipes.dll', 'VoltRpc.dll', 'VoltRpc.Communication.Pipes.dll'
) | ForEach-Object { Join-Path $managed $_ }
$output = Join-Path $PSScriptRoot 'bin'

function Build-TikTokLibrary {
    param([string]$Name, [string]$Sources, [string]$Version, [string[]]$ExtraReferences)
    $sourceFiles = @(Get-ChildItem -LiteralPath $Sources -Filter '*.cs' -Recurse | ForEach-Object FullName)
    if ($sourceFiles.Count -eq 0) { throw "No C# sources found in $Sources" }
    $references = @($commonReferences)
    if ($ExtraReferences) { $references += $ExtraReferences }
    foreach ($reference in $references) {
        if (-not (Test-Path -LiteralPath $reference -PathType Leaf)) { throw "Missing build reference: $reference" }
    }
    $objDirectory = Join-Path $PSScriptRoot ('obj\' + $Name)
    New-Item -ItemType Directory -Path $objDirectory,$output -Force | Out-Null
    $buildInfo = Join-Path $objDirectory 'BuildInfo.g.cs'
    @"
[assembly: System.Reflection.AssemblyVersion("$Version")]
[assembly: System.Reflection.AssemblyFileVersion("$Version")]
internal static class PluginBuildInfo { public const string Version = "$Version"; public const string Informational = "$Version"; }
"@ | Set-Content -LiteralPath $buildInfo -Encoding UTF8
    $compilerArguments = @('-target:library', ('-out:' + (Join-Path $output ($Name + '.dll'))),
        '-nostdlib+', '-noconfig', '-optimize+', '-deterministic+', '-langversion:7.3', '-warn:4', '-nologo', '-debug:portable')
    $compilerArguments += $references | ForEach-Object { '-r:' + $_ }
    $compilerArguments += $sourceFiles + $buildInfo
    & $DotnetPath $CscDll @compilerArguments
    if ($LASTEXITCODE -ne 0) { throw "$Name compilation failed." }
    Write-Host "Built $Name"
}

$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Build-TikTokLibrary -Name 'ScamWYF.Modding.Core' -Sources (Join-Path $libraryRoot 'src') -Version '1.0.4'
$pluginReferences += Join-Path $output 'ScamWYF.Modding.Core.dll'
Build-TikTokLibrary -Name 'ScamWYF.TikTokPocket' -Sources (Join-Path $PSScriptRoot 'src') -Version '1.0.0' -ExtraReferences $pluginReferences

# Read metadata without loading the plugin or any game assembly into this build process.
Add-Type -Path (Join-Path $core 'Mono.Cecil.dll')
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $output 'ScamWYF.TikTokPocket.dll'))
try {
    $plugins = @()
    foreach ($type in $assembly.MainModule.Types) {
        foreach ($attribute in $type.CustomAttributes) {
            if ($attribute.AttributeType.FullName -eq 'BepInEx.BepInPlugin') { $plugins += $attribute }
        }
    }
    if ($plugins.Count -ne 1) { throw 'Expected exactly one BepInEx plugin.' }
    $plugin = $plugins[0]
    if ([string]$plugin.ConstructorArguments[0].Value -ne 'com.community.scamwyf.tiktokpocket' -or
        [string]$plugin.ConstructorArguments[1].Value -ne 'TikTok Pocket' -or
        [string]$plugin.ConstructorArguments[2].Value -ne '1.0.0') {
        throw 'Unexpected TikTok Pocket plugin identity or version.'
    }
    Write-Host 'Verified plugin: TikTok Pocket 1.0.0'
}
finally { $assembly.Dispose() }
Write-Host 'Build only: no game files were changed. Game and browser libraries are references only.'
