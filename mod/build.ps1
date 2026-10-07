[CmdletBinding()]
param([string]$GameDir, [string]$CscDll, [switch]$NoCopy)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$upstreamRoot = Join-Path $repoRoot 'upstream\AI-Backend'
$libraryRoot = Join-Path $upstreamRoot 'vendor\ScamWYF.Modding.Core'
if (-not $GameDir) { $GameDir = $env:SWYG_GAME_DIR }
if (-not $GameDir) {
    foreach ($name in @('Scam With Your Friends', 'Scam With Your Friends Playtest')) {
        $candidate = Join-Path "${env:ProgramFiles(x86)}\Steam\steamapps\common" $name
        if (Test-Path -LiteralPath (Join-Path $candidate 'Scam With Your Friends_Data\Managed\mscorlib.dll')) {
            $GameDir = $candidate
            break
        }
    }
}
if (-not $GameDir) { throw 'Game not found. Pass -GameDir or set SWYG_GAME_DIR.' }
$managed = Join-Path $GameDir 'Scam With Your Friends_Data\Managed'
$core = Join-Path $GameDir 'BepInEx\core'
$portableDotnet = Join-Path $repoRoot 'build\tools\dotnet'
if (-not $CscDll -and (Test-Path -LiteralPath $portableDotnet)) {
    $compiler = Get-ChildItem -Path (Join-Path $portableDotnet 'sdk\*\Roslyn\bincore\csc.dll') |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($compiler) { $CscDll = $compiler.FullName }
    $env:PATH = $portableDotnet + [IO.Path]::PathSeparator + $env:PATH
    $env:DOTNET_ROOT = $portableDotnet
}
if (-not $CscDll) {
    $compiler = Get-ChildItem -Path "${env:ProgramFiles}\dotnet\sdk\*\Roslyn\bincore\csc.dll" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($compiler) { $CscDll = $compiler.FullName }
}
if (-not $CscDll -or -not (Test-Path -LiteralPath $CscDll)) { throw 'Roslyn compiler missing. Pass -CscDll or install a .NET SDK.' }

$commonRefs = @(
    'mscorlib.dll', 'System.dll', 'System.Core.dll', 'UnityEngine.dll', 'UnityEngine.CoreModule.dll',
    'UnityEngine.IMGUIModule.dll', 'UnityEngine.UIElementsModule.dll', 'UnityEngine.TextRenderingModule.dll',
    'UnityEngine.TextCoreTextEngineModule.dll', 'UnityEngine.TextCoreFontEngineModule.dll', 'Unity.InputSystem.dll'
) | ForEach-Object { Join-Path $managed $_ }
$commonRefs += (Join-Path $core 'BepInEx.dll'), (Join-Path $core '0Harmony.dll')
$gameRefs = @('UnityEngine.UnityWebRequestModule.dll','Newtonsoft.Json.dll','UniTask.dll','Assembly-CSharp.dll',
    'ScriptsAssDef.dll','MetaVoiceChat.Core.dll','Mirror.dll') | ForEach-Object { Join-Path $managed $_ }

function Build-GameLibrary {
    param([string]$Name,[string]$SourceDir,[string]$OutDir,[string]$Version,[string[]]$ExtraRefs)
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    $objDir = Join-Path $PSScriptRoot ('obj\' + $Name)
    New-Item -ItemType Directory -Path $objDir -Force | Out-Null
    $buildInfo = Join-Path $objDir 'BuildInfo.g.cs'
    @"
[assembly: System.Reflection.AssemblyVersion("$Version")]
[assembly: System.Reflection.AssemblyFileVersion("$Version")]
internal static class PluginBuildInfo { public const string Version = "$Version"; public const string Informational = "$Version"; }
"@ | Set-Content -LiteralPath $buildInfo -Encoding UTF8
    $output = Join-Path $OutDir ($Name + '.dll')
    $references = @($commonRefs)
    if ($ExtraRefs) { $references += $ExtraRefs }
    foreach ($reference in $references) { if (-not (Test-Path -LiteralPath $reference)) { throw "Missing game reference $reference" } }
    $sources = @(Get-ChildItem -LiteralPath $SourceDir -Filter '*.cs' -Recurse | ForEach-Object FullName) + $buildInfo
    $compilerArgs = @('-target:library',"-out:$output",'-nostdlib+','-noconfig','-optimize+','-langversion:7.3','-warn:4','-nologo','-debug:portable')
    $compilerArgs += $references | ForEach-Object { '-r:' + $_ }
    $compilerArgs += $sources
    & dotnet $CscDll @compilerArgs
    if ($LASTEXITCODE -ne 0) { throw "$Name compilation failed." }
    Write-Host "Built $output"
}

$libraryOut = Join-Path $libraryRoot 'bin'
$upstreamOut = Join-Path $upstreamRoot 'bin'
$pluginOut = Join-Path $PSScriptRoot 'bin'
Build-GameLibrary -Name 'ScamWYF.Modding.Core' -SourceDir (Join-Path $libraryRoot 'src') -OutDir $libraryOut -Version '1.0.4'
$libraryDll = Join-Path $libraryOut 'ScamWYF.Modding.Core.dll'
Build-GameLibrary -Name 'ScamWYF.AiBackend' -SourceDir (Join-Path $upstreamRoot 'src') -OutDir $upstreamOut -Version '1.1.0' -ExtraRefs ($gameRefs + $libraryDll)
$upstreamDll = Join-Path $upstreamOut 'ScamWYF.AiBackend.dll'
Build-GameLibrary -Name 'ScamWYF.ElevenLabsAgents' -SourceDir (Join-Path $PSScriptRoot 'src') -OutDir $pluginOut -Version '1.0.1' -ExtraRefs ($gameRefs + $libraryDll + $upstreamDll)
$pluginDll = Join-Path $pluginOut 'ScamWYF.ElevenLabsAgents.dll'

# Read metadata without loading Unity assemblies. BepInEx accepts only numeric plugin versions.
Add-Type -Path (Join-Path $core 'Mono.Cecil.dll')
foreach ($dll in @($upstreamDll,$pluginDll)) {
    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($dll)
    try {
        $pluginCount = 0
        foreach ($type in $assembly.MainModule.Types) {
            foreach ($attribute in $type.CustomAttributes) {
                if ($attribute.AttributeType.FullName -ne 'BepInEx.BepInPlugin') { continue }
                $pluginCount++
                $parsed = [version]::Parse([string]$attribute.ConstructorArguments[2].Value)
                Write-Host "Verified plugin: $($attribute.ConstructorArguments[1].Value) $parsed"
            }
        }
        if ($pluginCount -ne 1) { throw "Expected exactly one BepInEx plugin in $dll." }
    }
    finally { $assembly.Dispose() }
}

if (-not $NoCopy) {
    $plugins = Join-Path $GameDir 'BepInEx\plugins'
    New-Item -ItemType Directory -Path $plugins -Force | Out-Null
    Copy-Item -LiteralPath $libraryDll -Destination $core -Force
    Copy-Item -LiteralPath $upstreamDll,$pluginDll -Destination $plugins -Force
    Write-Host 'Installed ElevenLabs Agents and its dependencies.'
}
