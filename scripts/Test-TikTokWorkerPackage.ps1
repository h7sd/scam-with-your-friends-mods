<# Exercises the package trust boundary using synthetic files, never a browser profile. #>
[CmdletBinding()]
param([string]$ProjectDirectory = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'TikTok-WorkerPackage.ps1')
$workerTestRoot = Join-Path ([IO.Path]::GetFullPath($ProjectDirectory)) ('build/worker-package-test-'+[guid]::NewGuid().ToString('N').Substring(0,8))
[IO.Directory]::CreateDirectory($workerTestRoot) | Out-Null
$workerTestExe = Join-Path $workerTestRoot 'ScamWYF.TikTokBrowser.exe'
[IO.File]::WriteAllText($workerTestExe,'synthetic-executable-never-run')
$workerTestHash = (Get-FileHash -LiteralPath $workerTestExe).Hash.ToLowerInvariant()
$workerTestSize = (Get-Item -LiteralPath $workerTestExe).Length
$workerTestManifest = Join-Path $workerTestRoot 'worker-files.json'
$workerTestValid = [ordered]@{schemaVersion=1; appVersion='1.0.1'; executable='ScamWYF.TikTokBrowser.exe'; files=@([ordered]@{path='ScamWYF.TikTokBrowser.exe'; size=$workerTestSize; sha256=$workerTestHash})}
function Write-WorkerTestManifest($value){[IO.File]::WriteAllText($workerTestManifest,($value | ConvertTo-Json -Depth 5))}
function Assert-WorkerTestRejected([string]$label,$value){
    Write-WorkerTestManifest $value
    $rejected=$false
    try {Get-TikTokWorkerPackage -WorkerRoot $workerTestRoot | Out-Null} catch {$rejected=$true}
    if(-not $rejected){throw ('Accepted invalid worker manifest: '+$label)}
}
Write-WorkerTestManifest $workerTestValid
$workerTestRead = Get-TikTokWorkerPackage -WorkerRoot $workerTestRoot
if($workerTestRead.Files.Count -ne 1 -or $workerTestRead.TotalSize -ne $workerTestSize){throw 'Valid worker manifest was not read correctly.'}
foreach($badPath in @('../outside.dll','Profile/Cookies','runtimes/win-x86/native/a.dll','C:/outside.dll','Licenses/../outside.txt','ScamWYF.TikTokBrowser.EXE')){
    $invalid = [ordered]@{schemaVersion=1; appVersion='1.0.1'; executable='ScamWYF.TikTokBrowser.exe'; files=@([ordered]@{path=$badPath; size=$workerTestSize; sha256=$workerTestHash})}
    Assert-WorkerTestRejected $badPath $invalid
}
if(Test-TikTokWorkerRelativePath ("payload.dll"+[char]10)){throw 'Accepted a trailing newline in a worker path.'}
foreach($badSize in @(0,-1,1.5,268435457)){
    $invalid = [ordered]@{schemaVersion=1; appVersion='1.0.1'; executable='ScamWYF.TikTokBrowser.exe'; files=@([ordered]@{path='ScamWYF.TikTokBrowser.exe'; size=$badSize; sha256=$workerTestHash})}
    Assert-WorkerTestRejected 'size' $invalid
}
$invalid = [ordered]@{schemaVersion=1; appVersion='1.0.0'; executable='ScamWYF.TikTokBrowser.exe'; files=$workerTestValid.files}
Assert-WorkerTestRejected 'version' $invalid
$invalid = [ordered]@{schemaVersion=1; appVersion='1.0.1'; executable='ScamWYF.TikTokBrowser.exe'; files=$workerTestValid.files; profile='unexpected'}
Assert-WorkerTestRejected 'unknown manifest field' $invalid
$invalid = [ordered]@{schemaVersion=1; appVersion='1.0.1'; executable='ScamWYF.TikTokBrowser.exe'; files=@($workerTestValid.files[0],$workerTestValid.files[0])}
Assert-WorkerTestRejected 'duplicate path' $invalid
Write-WorkerTestManifest $workerTestValid
[IO.File]::WriteAllText($workerTestExe,'modified')
$rejected=$false
try {Get-TikTokWorkerPackage -WorkerRoot $workerTestRoot | Out-Null} catch {$rejected=$true}
if(-not $rejected){throw 'Accepted a modified worker executable.'}
$outsideRejected=$false
try {Assert-TikTokWorkerPath -Root $workerTestRoot -Path (Join-Path $workerTestRoot '../outside.dll')} catch {$outsideRejected=$true}
if(-not $outsideRejected){throw 'Accepted a destination outside the worker directory.'}
Write-Output 'Worker package checks passed: valid identity, six forbidden paths, trailing newline, four invalid lengths, version, unknown field, duplicate path, tampered file, destination boundary.'
