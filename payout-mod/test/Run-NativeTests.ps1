[CmdletBinding()]
param([string]$GameDirectory, [string]$DotnetPath)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $taskRoot 'scripts/Game-Paths.ps1')
$nativeTestGameDir = Resolve-ScamGameDirectory -GameDirectory $GameDirectory
$nativeTestManaged = Join-Path $nativeTestGameDir 'Scam With Your Friends_Data/Managed'
$nativeTestCore = Join-Path $nativeTestGameDir 'BepInEx/core'
$nativeTestFramework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$nativeTestBundledDotnet = Join-Path $taskRoot 'build/tools/dotnet/dotnet.exe'
if (-not $DotnetPath) {
    $DotnetPath = if (Test-Path -LiteralPath $nativeTestBundledDotnet -PathType Leaf) { $nativeTestBundledDotnet } else { 'dotnet' }
}
try { $nativeTestDotnet = (Get-Command -Name $DotnetPath -CommandType Application -ErrorAction Stop).Source }
catch { throw 'A .NET 8 SDK is required. Install it or pass -DotnetPath with the path to dotnet.exe.' }
$nativeTestSdks = @(
    foreach ($sdkLine in (& $nativeTestDotnet --list-sdks)) {
        if ($sdkLine -match '^(8\.\d+\.\d+)\s+\[(.+)\]$') {
            [pscustomobject]@{ Version = [version]$Matches[1]; Path = Join-Path $Matches[2] $Matches[1] }
        }
    }
)
if ($LASTEXITCODE -ne 0 -or $nativeTestSdks.Count -eq 0) { throw 'Install a .NET 8 SDK for the selected dotnet.exe runtime.' }
$nativeTestSdk = $nativeTestSdks | Sort-Object Version -Descending | Select-Object -First 1
$nativeTestCompiler = Join-Path $nativeTestSdk.Path 'Roslyn/bincore/csc.dll'
# Unity's Newtonsoft assembly is delay-signed. The test process preloads the
# signed SDK copy with the same public assembly identity, without changing the game.
$nativeTestJson = Join-Path $nativeTestSdk.Path 'Containers/tasks/net472/Newtonsoft.Json.dll'
if (-not (Test-Path -LiteralPath $nativeTestJson -PathType Leaf)) {
    $nativeTestJson = Join-Path $nativeTestSdk.Path 'Sdks/Microsoft.NET.Sdk/tools/net472/Newtonsoft.Json.dll'
}
foreach ($dependency in @($nativeTestCompiler, $nativeTestJson, (Join-Path $nativeTestFramework 'mscorlib.dll'))) {
    if (-not (Test-Path -LiteralPath $dependency -PathType Leaf)) { throw "Native test dependency not found: $dependency" }
}
$nativeTestOut = Join-Path $taskRoot 'build/payout-tests/native'
New-Item -ItemType Directory -Path $nativeTestOut -Force | Out-Null
$nativeTestExe = Join-Path $nativeTestOut 'Payout.NativeTests.exe'
$nativeTestRefs = @(
    (Join-Path $nativeTestFramework 'mscorlib.dll'),
    (Join-Path $nativeTestFramework 'System.dll'),
    (Join-Path $nativeTestFramework 'System.Core.dll'),
    (Join-Path $nativeTestManaged 'Assembly-CSharp.dll'),
    (Join-Path $nativeTestManaged 'ScriptsAssDef.dll'),
    (Join-Path $nativeTestManaged 'UnityEngine.CoreModule.dll'),
    (Join-Path $nativeTestManaged 'Newtonsoft.Json.dll'),
    (Join-Path $nativeTestCore '0Harmony.dll')
)
$nativeTestArgs = @('-nologo','-target:exe','-langversion:7.3','-nostdlib+','-noconfig',('-out:' + $nativeTestExe))
$nativeTestArgs += $nativeTestRefs | ForEach-Object { '-r:' + $_ }
$nativeTestArgs += @('NativeProgram.cs','NativeStubs.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$nativeTestArgs += @('PayoutHooks.cs','PriceBook.cs','SpokenPrice.cs') | ForEach-Object { Join-Path (Split-Path $PSScriptRoot -Parent) ('src/' + $_) }
& $nativeTestDotnet $nativeTestCompiler @nativeTestArgs
if ($LASTEXITCODE -ne 0) { throw 'Native payout test harness compilation failed.' }
& $nativeTestExe $nativeTestGameDir $nativeTestJson
if ($LASTEXITCODE -ne 0) { throw ('Native payout test harness failed with exit ' + $LASTEXITCODE) }
