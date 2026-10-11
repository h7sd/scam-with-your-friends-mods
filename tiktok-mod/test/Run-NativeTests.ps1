[CmdletBinding()]
param([string]$GameDirectory,[string]$DotnetPath)
$ErrorActionPreference='Stop'
$testRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $testRoot 'scripts/Game-Paths.ps1')
$testGameDir=Resolve-ScamGameDirectory -GameDirectory $GameDirectory
$testManaged=Join-Path $testGameDir 'Scam With Your Friends_Data/Managed'
$testCore=Join-Path $testGameDir 'BepInEx/core'
$testFramework=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
if(-not $DotnetPath){
    $testBundled=Join-Path $testRoot 'build/tools/dotnet/dotnet.exe'
    $DotnetPath=if(Test-Path -LiteralPath $testBundled){$testBundled}else{'dotnet'}
}
$testDotnet=(Get-Command -Name $DotnetPath -CommandType Application -ErrorAction Stop).Source
$testSdks=@(foreach($line in (& $testDotnet --list-sdks)){
    if($line -match '^(8\.\d+\.\d+)\s+\[(.+)\]$'){
        [pscustomobject]@{Version=[version]$Matches[1];Path=Join-Path $Matches[2] $Matches[1]}
    }
})
if($LASTEXITCODE -ne 0 -or $testSdks.Count -eq 0){throw 'Install a .NET8 SDK or pass -DotnetPath.'}
$testSdk=$testSdks|Sort-Object Version -Descending|Select-Object -First 1
$testCompiler=Join-Path $testSdk.Path 'Roslyn/bincore/csc.dll'
$testJson=Join-Path $testSdk.Path 'Containers/tasks/net472/Newtonsoft.Json.dll'
if(-not(Test-Path -LiteralPath $testJson)){$testJson=Join-Path $testSdk.Path 'Sdks/Microsoft.NET.Sdk/tools/net472/Newtonsoft.Json.dll'}
$testOut=Join-Path $testRoot 'build/tiktok-tests/native'
New-Item -ItemType Directory -Path $testOut -Force|Out-Null
$testExe=Join-Path $testOut 'TikTok.NativeTests.exe'
$testRefs=@('mscorlib.dll','System.dll','System.Core.dll')|ForEach-Object{Join-Path $testFramework $_}
$testRefs+=@('UnityEngine.dll','UnityEngine.CoreModule.dll','UnityEngine.UIElementsModule.dll','UnityEngine.UI.dll',
    'UnityEngine.UIModule.dll','UnityEngine.InputLegacyModule.dll','UnityEngine.AudioModule.dll','Unity.InputSystem.dll',
    'Assembly-CSharp.dll','ScriptsAssDef.dll','Mirror.dll','UniTask.dll','Newtonsoft.Json.dll',
    'VoltstroStudios.UnityWebBrowser.dll','VoltstroStudios.UnityWebBrowser.Shared.dll',
    'VoltstroStudios.UnityWebBrowser.Communication.Pipes.dll','VoltRpc.dll','VoltRpc.Communication.Pipes.dll')|ForEach-Object{Join-Path $testManaged $_}
$testRefs+=Join-Path $testCore '0Harmony.dll'
$testArgs=@('-nologo','-target:exe','-langversion:7.3','-nostdlib+','-noconfig',('-out:'+$testExe))
$testArgs+=$testRefs|ForEach-Object{'-r:'+$_}
$testArgs+=@('NativeProgram.cs','NativeStubs.cs')|ForEach-Object{Join-Path $PSScriptRoot $_}
$testArgs+=@('NativeApi.cs','CatalogHooks.cs','BrowserHooks.cs')|ForEach-Object{Join-Path (Split-Path $PSScriptRoot -Parent) ('src/'+$_)}
& $testDotnet $testCompiler @testArgs
if($LASTEXITCODE -ne 0){throw 'TikTok native harness compilation failed.'}
& $testExe $testGameDir $testJson $testRoot
if($LASTEXITCODE -ne 0){throw "TikTok native harness failed with exit $LASTEXITCODE"}
