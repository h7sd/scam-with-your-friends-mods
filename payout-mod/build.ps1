[CmdletBinding()]
param([string]$GameDir,[string]$CscDll)
$ErrorActionPreference='Stop'
$repoRoot=Split-Path $PSScriptRoot -Parent
if(-not $GameDir){$GameDir=$env:SWYG_GAME_DIR}
if(-not $GameDir){
    foreach($name in @('Scam With Your Friends','Scam With Your Friends Playtest')){
        $candidate=Join-Path "${env:ProgramFiles(x86)}\Steam\steamapps\common" $name
        if(Test-Path -LiteralPath (Join-Path $candidate 'Scam With Your Friends_Data\Managed\Assembly-CSharp.dll')){$GameDir=$candidate;break}
    }
}
if(-not $GameDir){throw 'Game not found. Pass -GameDir or set SWYG_GAME_DIR.'}
$managed=Join-Path $GameDir 'Scam With Your Friends_Data\Managed'
$core=Join-Path $GameDir 'BepInEx\core'
$libraryRoot=Join-Path $repoRoot 'upstream\AI-Backend\vendor\ScamWYF.Modding.Core'
$portableDotnet=Join-Path $repoRoot 'build\tools\dotnet'
if(Test-Path -LiteralPath $portableDotnet){
    $env:PATH=$portableDotnet+[IO.Path]::PathSeparator+$env:PATH
    $env:DOTNET_ROOT=$portableDotnet
    if(-not $CscDll){$CscDll=(Get-ChildItem -Path (Join-Path $portableDotnet 'sdk\*\Roslyn\bincore\csc.dll') | Sort-Object FullName -Descending | Select-Object -First 1).FullName}
}
if(-not $CscDll){$CscDll=(Get-ChildItem -Path "${env:ProgramFiles}\dotnet\sdk\*\Roslyn\bincore\csc.dll" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1).FullName}
if(-not $CscDll){throw 'Roslyn compiler not found. Pass -CscDll.'}
$references=@('mscorlib.dll','System.dll','System.Core.dll','UnityEngine.dll','UnityEngine.CoreModule.dll',
    'UnityEngine.IMGUIModule.dll','UnityEngine.UIElementsModule.dll','UnityEngine.TextRenderingModule.dll',
    'UnityEngine.TextCoreTextEngineModule.dll','UnityEngine.TextCoreFontEngineModule.dll','Unity.InputSystem.dll') | ForEach-Object {Join-Path $managed $_}
$references+=(Join-Path $core 'BepInEx.dll'),(Join-Path $core '0Harmony.dll')
$output=Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $output -Force | Out-Null
function Build-Library {
    param([string]$Name,[string]$Sources,[string]$Version,[string[]]$Extra)
    $obj=Join-Path $PSScriptRoot ('obj\'+$Name)
    New-Item -ItemType Directory -Path $obj -Force | Out-Null
    $buildInfo=Join-Path $obj 'BuildInfo.g.cs'
    @"
[assembly: System.Reflection.AssemblyVersion("$Version")]
[assembly: System.Reflection.AssemblyFileVersion("$Version")]
internal static class PluginBuildInfo {public const string Version="$Version";public const string Informational="$Version";}
"@ | Set-Content -LiteralPath $buildInfo -Encoding UTF8
    $allRefs=@($references)
    if($Extra){$allRefs+=$Extra}
    foreach($reference in $allRefs){if(-not(Test-Path -LiteralPath $reference)){throw "Missing reference: $reference"}}
    $args=@('-target:library',('-out:'+(Join-Path $output ($Name+'.dll'))),'-nostdlib+','-noconfig','-optimize+','-langversion:7.3','-warn:4','-nologo','-debug:portable')
    $args+=$allRefs | ForEach-Object {'-r:'+$_}
    $args+=Get-ChildItem -LiteralPath $Sources -Filter '*.cs' -Recurse | ForEach-Object FullName
    $args+=$buildInfo
    & dotnet $CscDll @args
    if($LASTEXITCODE -ne 0){throw "$Name compilation failed."}
    Write-Host "Built $Name"
}
Build-Library -Name 'ScamWYF.Modding.Core' -Sources (Join-Path $libraryRoot 'src') -Version '1.0.4'
$extra=@('Newtonsoft.Json.dll','Assembly-CSharp.dll','ScriptsAssDef.dll','Mirror.dll','UniTask.dll') | ForEach-Object {Join-Path $managed $_}
$extra+=Join-Path $output 'ScamWYF.Modding.Core.dll'
Build-Library -Name 'ScamWYF.RequestedPayout' -Sources (Join-Path $PSScriptRoot 'src') -Version '1.2.0' -Extra $extra
Add-Type -Path (Join-Path $core 'Mono.Cecil.dll')
$assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $output 'ScamWYF.RequestedPayout.dll'))
try {
    $found=0
    foreach($type in $assembly.MainModule.Types){foreach($attribute in $type.CustomAttributes){
        if($attribute.AttributeType.FullName -eq 'BepInEx.BepInPlugin'){
            $found++;$version=[version]::Parse([string]$attribute.ConstructorArguments[2].Value)
            Write-Host "Verified plugin: $($attribute.ConstructorArguments[1].Value) $version"
        }
    }}
    if($found -ne 1){throw 'Expected exactly one BepInEx plugin.'}
} finally {$assembly.Dispose()}
Write-Host 'Build only: no game files were changed. Use the installer/launcher to install after closing the game.'
