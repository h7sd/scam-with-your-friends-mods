<#
Included DLL licence notices:
MIT License

Copyright (c) 2026 SWYF Community Mods contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.


MIT License

Copyright (c) 2026 Ras_rap

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

#>
<#
Requested Payout post-install verification and usage notes. Licence notices are
prepended to the release copy by Package-CatalogueMods.ps1.
#>
[CmdletBinding(SupportsShouldProcess=$true)]
param(
    [Parameter(Mandatory=$true)][string]$GameDir,
    [string]$DependencyDirectory
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version 2
# Embedded into each public post-install script by Package-CatalogueMods.ps1.
# Dependency notice scripts are inspected as data and are never executed.
$catalogueDependencyDefinitions=@{
    Core=@{Name='ScamWYF.Modding.Core';Version=[version]'1.0.4.0';Archive='Modding-Core-1.0.4.zip';Notice='post-install-modding-core.ps1';Relative='BepInEx/core/ScamWYF.Modding.Core.dll';ArchiveSha256='05d37f80d2974dba30fd6b9b6edf92620400b7a8249b6f6ea2ec16f5ad5ac644';DllSha256='e06ebf6da079ecc7dcc00845f76b9b86860b1a370479cd24f13af5b42dda5392'}
    AiBackend=@{Name='ScamWYF.AiBackend';Version=[version]'1.1.0.0';Archive='AI-Backend-1.1.0.zip';Notice='post-install-ai-backend.ps1';Relative='BepInEx/plugins/ScamWYF.AiBackend.dll';ArchiveSha256='e7f4a3a0f77a28f8c52c48d642c31bf18055c222c566e453d7851513a957d4f6';DllSha256='69c82a19b38e62759d6686ff696f24158e28bf26c7fc31a49d6ef518ed33f60a'}
}
function Assert-CatalogueDependencyPath([string]$Root,[string]$Path) {
    $boundary=[IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    $target=[IO.Path]::GetFullPath($Path)
    if($target -ine $boundary -and -not $target.StartsWith($boundary+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {throw 'A dependency setup path would escape its intended folder.'}
    $current=$target
    while($current) {
        if(Test-Path -LiteralPath $current) {
            if(([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {throw 'Dependency setup does not follow symbolic links or junctions.'}
        }
        $next=[IO.Path]::GetDirectoryName($current)
        if($next -eq $current) {break};$current=$next
    }
}
function Get-CatalogueDependencyChild([string]$Root,[string]$Relative) {
    $target=[IO.Path]::GetFullPath((Join-Path $Root $Relative.Replace('/',[IO.Path]::DirectorySeparatorChar)))
    Assert-CatalogueDependencyPath $Root $target
    return $target
}
function Assert-CatalogueDependencyClosed {
    foreach($name in @('Scam With Your Friends','Scam With Your Friends Playtest')) {
        if(Get-Process -Name $name -ErrorAction SilentlyContinue) {throw 'Close Scam With Your Friends before installing shared mod dependencies.'}
    }
}
function Assert-CatalogueDependencyHash([string]$Path,[string]$Expected) {
    if($Expected -cnotmatch '\A[0-9a-f]{64}\z') {throw 'This setup build is missing its pinned dependency digests.'}
    if(-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Expected) {throw 'A dependency file does not match its pinned SHA-256 digest.'}
}
function Download-CatalogueDependency([string]$Url,[string]$Destination) {
    Add-Type -AssemblyName System.Net.Http
    $handler=[Net.Http.HttpClientHandler]::new();$handler.AllowAutoRedirect=$false
    $client=[Net.Http.HttpClient]::new($handler);$client.Timeout=[TimeSpan]::FromSeconds(120)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('ScamWYF-Catalogue-Setup/1.0.0')
    $timeout=[Threading.CancellationTokenSource]::new();$timeout.CancelAfter([TimeSpan]::FromMinutes(3))
    $oldTls=[Net.ServicePointManager]::SecurityProtocol;[Net.ServicePointManager]::SecurityProtocol=$oldTls -bor [Net.SecurityProtocolType]::Tls12
    $response=$null
    try {
        $uri=[Uri]$Url
        for($redirect=0;$redirect -le 5;$redirect++) {
            if($uri.Scheme -cne 'https' -or $uri.UserInfo -or $uri.Port -ne 443 -or @('github.com','release-assets.githubusercontent.com','objects.githubusercontent.com') -cnotcontains $uri.DnsSafeHost) {throw 'Unexpected dependency download host.'}
            $response=$client.GetAsync($uri,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$timeout.Token).GetAwaiter().GetResult()
            if([int]$response.StatusCode -in @(301,302,303,307,308)) {
                if($null -eq $response.Headers.Location -or $redirect -eq 5) {throw 'Unexpected dependency download redirect.'}
                $uri=[Uri]::new($uri,$response.Headers.Location);$response.Dispose();$response=$null;continue
            }
            if(-not $response.IsSuccessStatusCode -or ($null -ne $response.Content.Headers.ContentLength -and $response.Content.Headers.ContentLength -gt 4194304)) {throw 'Unavailable or oversized dependency archive.'}
            $input=$response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $output=[IO.File]::Open($Destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try {
                $buffer=[byte[]]::new(65536);[long]$total=0
                while(($count=$input.ReadAsync($buffer,0,$buffer.Length,$timeout.Token).GetAwaiter().GetResult()) -gt 0) {
                    $total+=$count;if($total -gt 4194304) {throw 'Oversized dependency archive.'};$output.Write($buffer,0,$count)
                }
                if($total -le 0) {throw 'Empty dependency archive.'}
            } finally {$output.Dispose();$input.Dispose()}
            return
        }
        throw 'Unavailable dependency archive.'
    } catch {throw 'A pinned mod dependency could not be downloaded safely. Run this post-install script again when the release is available, or provide -DependencyDirectory with the original dependency ZIPs.'}
    finally {if($null -ne $response) {$response.Dispose()};$client.Dispose();$handler.Dispose();$timeout.Dispose();[Net.ServicePointManager]::SecurityProtocol=$oldTls}
}
function Expand-CatalogueDependency([string]$ArchivePath,[string]$Destination,$Definition) {
    # Authenticate the complete archive before enumerating or reading any entry.
    Assert-CatalogueDependencyHash $ArchivePath $Definition.ArchiveSha256
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive=[IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $dllName=$Definition.Name+'.dll'
        if($archive.Entries.Count -ne 2) {throw 'A dependency archive must contain exactly its DLL and notice script.'}
        $entries=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach($entry in $archive.Entries) {
            if($entry.FullName -cnotin @($dllName,$Definition.Notice) -or $entries.ContainsKey($entry.FullName) -or $entry.Length -le 0 -or $entry.Length -gt 2097152 -or ($entry.FullName -ceq $Definition.Notice -and $entry.Length -gt 65536) -or (($entry.ExternalAttributes -shr 16) -band 61440) -in @(40960,16384) -or ($entry.ExternalAttributes -band 1024) -ne 0) {throw 'The dependency archive contains unexpected or unsafe entries.'}
            $entries.Add($entry.FullName,$entry)
        }
        if(-not $entries.ContainsKey($dllName) -or -not $entries.ContainsKey($Definition.Notice)) {throw 'The dependency archive is incomplete.'}
        foreach($name in @($dllName,$Definition.Notice)) {
            $entry=$entries[$name];$target=Get-CatalogueDependencyChild $Destination $name
            $input=$entry.Open();$output=[IO.File]::Open($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try {
                $buffer=[byte[]]::new(32768);[long]$written=0
                while(($count=$input.Read($buffer,0,$buffer.Length)) -gt 0) {$written+=$count;if($written -gt $entry.Length) {throw 'A dependency entry exceeds its declared size.'};$output.Write($buffer,0,$count)}
                if($written -ne $entry.Length) {throw 'A dependency entry is truncated.'}
            } finally {$output.Dispose();$input.Dispose()}
        }
        $dll=Get-CatalogueDependencyChild $Destination $dllName
        Assert-CatalogueDependencyHash $dll $Definition.DllSha256
        $identity=[Reflection.AssemblyName]::GetAssemblyName($dll)
        if($identity.Name -cne $Definition.Name -or $identity.Version -ne $Definition.Version) {throw 'The dependency DLL does not have its expected assembly identity.'}
        $notice=Get-CatalogueDependencyChild $Destination $Definition.Notice
        $null=[Text.UTF8Encoding]::new($false,$true).GetString([IO.File]::ReadAllBytes($notice))
        return $dll
    } finally {$archive.Dispose()}
}
function Remove-CatalogueDependencyTemporary([string]$Root,[string]$Path) {
    Assert-CatalogueDependencyPath $Root $Path
    if([IO.Path]::GetDirectoryName($Path) -ine $Root -or [IO.Path]::GetFileName($Path) -cnotmatch '\A[0-9a-f]{32}\z') {throw 'Invalid dependency temporary cleanup path.'}
    $pending=[Collections.Generic.Queue[string]]::new();$pending.Enqueue($Path)
    while($pending.Count) {foreach($entry in Get-ChildItem -LiteralPath $pending.Dequeue() -Force) {Assert-CatalogueDependencyPath $Path $entry.FullName;if($entry.PSIsContainer) {$pending.Enqueue($entry.FullName)}}}
    Remove-Item -LiteralPath $Path -Recurse -Force
}
function Install-CatalogueDependency([string]$GameDir,[string]$Name,[string]$DependencyDirectory,[Management.Automation.PSCmdlet]$Caller) {
    if(-not $catalogueDependencyDefinitions.ContainsKey($Name)) {throw 'Unknown catalogue dependency.'}
    $definition=$catalogueDependencyDefinitions[$Name]
    foreach($pin in @($definition.ArchiveSha256,$definition.DllSha256)) {if($pin -cnotmatch '\A[0-9a-f]{64}\z') {throw 'This setup build is missing its pinned dependency digests.'}}
    $gameRoot=[IO.Path]::GetFullPath($GameDir).TrimEnd('\','/');Assert-CatalogueDependencyPath $gameRoot $gameRoot
    if(-not (Test-Path -LiteralPath $gameRoot -PathType Container)) {throw 'The selected game folder does not exist.'}
    $target=Get-CatalogueDependencyChild $gameRoot $definition.Relative
    if(Test-Path -LiteralPath $target -PathType Container) {throw 'A dependency DLL destination is occupied by a folder.'}
    if(Test-Path -LiteralPath $target -PathType Leaf) {
        $identity=[Reflection.AssemblyName]::GetAssemblyName($target)
        if($identity.Name -cne $definition.Name) {throw 'An existing dependency DLL has an unexpected assembly name. Existing files were preserved.'}
        if(($Name -ceq 'Core' -and $identity.Version -ge $definition.Version) -or ($Name -ceq 'AiBackend' -and $identity.Version -eq $definition.Version)) {Write-Verbose ('Keeping installed '+$identity.Name+' '+$identity.Version);return $true}
        if($Name -ceq 'AiBackend' -and $identity.Version -gt $definition.Version) {throw 'This release expects AI Backend 1.1.0. A newer AI Backend is already installed; it was preserved. Install a compatible ElevenLabs Agents release instead of downgrading it.'}
    }
    if(-not $Caller.ShouldProcess($target,'Install the pinned shared '+$definition.Name+' dependency and retain a backup of any replaced DLL')) {return $false}
    Assert-CatalogueDependencyClosed
    $tempRoot=[IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'ScamWYF-Catalogue-Dependencies')).TrimEnd('\','/')
    $temporary=Get-CatalogueDependencyChild $tempRoot ([guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($temporary)|Out-Null
    try {
        if($DependencyDirectory) {
            $offlineRoot=[IO.Path]::GetFullPath($DependencyDirectory).TrimEnd('\','/');Assert-CatalogueDependencyPath $offlineRoot $offlineRoot
            $archive=Get-CatalogueDependencyChild $offlineRoot $definition.Archive
        } else {
            $archive=Get-CatalogueDependencyChild $temporary $definition.Archive
            $url='https://github.com/h7sd/scam-with-your-friends-mods/releases/download/catalogue-dependencies-v1.0.0/'+$definition.Archive
            Download-CatalogueDependency $url $archive
        }
        if(-not (Test-Path -LiteralPath $archive -PathType Leaf) -or (Get-Item -LiteralPath $archive).Length -le 0 -or (Get-Item -LiteralPath $archive).Length -gt 4194304) {throw 'The dependency archive is missing or too large.'}
        $verifiedDll=Expand-CatalogueDependency $archive $temporary $definition
        Assert-CatalogueDependencyClosed
        Assert-CatalogueDependencyPath $gameRoot $target
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))|Out-Null
        $sibling=Get-CatalogueDependencyChild ([IO.Path]::GetDirectoryName($target)) ('.catalogue-dependency-'+[guid]::NewGuid().ToString('N')+'.tmp')
        try {
            [IO.File]::Copy($verifiedDll,$sibling,$false)
            if(Test-Path -LiteralPath $target -PathType Leaf) {
                # Validate again immediately before replacing; another mod installer may have updated it.
                $current=[Reflection.AssemblyName]::GetAssemblyName($target)
                if($current.Name -cne $definition.Name) {throw 'The dependency destination changed during setup. It was preserved.'}
                if(($Name -ceq 'Core' -and $current.Version -ge $definition.Version) -or ($Name -ceq 'AiBackend' -and $current.Version -eq $definition.Version)) {return $true}
                if($Name -ceq 'AiBackend' -and $current.Version -gt $definition.Version) {throw 'A newer AI Backend appeared during setup. It was preserved.'}
                $backup=Get-CatalogueDependencyChild $gameRoot ('BepInEx/catalogue-dependency-backups/'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff')+'-'+[guid]::NewGuid().ToString('N')+'/'+$definition.Name+'.dll')
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($backup))|Out-Null
                [IO.File]::Replace($sibling,$target,$backup)
                Write-Verbose ('Previous dependency DLL retained at '+$backup)
            } else {[IO.File]::Move($sibling,$target)}
            Assert-CatalogueDependencyHash $target $definition.DllSha256
        } finally {if(Test-Path -LiteralPath $sibling -PathType Leaf) {Assert-CatalogueDependencyPath $gameRoot $sibling;Remove-Item -LiteralPath $sibling -Force}}
        return $true
    } finally {if(Test-Path -LiteralPath $temporary -PathType Container) {Remove-CatalogueDependencyTemporary $tempRoot $temporary}}
}

$payoutGameRoot=[IO.Path]::GetFullPath($GameDir).TrimEnd('\','/');Assert-CatalogueDependencyPath $payoutGameRoot $payoutGameRoot
$payoutInstalledPath=Get-CatalogueDependencyChild $payoutGameRoot 'BepInEx/plugins/ScamWYF.RequestedPayout.dll'
if(-not (Test-Path -LiteralPath $payoutInstalledPath -PathType Leaf)){throw 'Requested Payout is not installed in the selected game folder.'}
$payoutInstalledIdentity=[Reflection.AssemblyName]::GetAssemblyName($payoutInstalledPath)
if($payoutInstalledIdentity.Name -ne 'ScamWYF.RequestedPayout' -or $payoutInstalledIdentity.Version -ne [version]'1.2.0.0'){throw 'This setup script expects Requested Payout 1.2.0.'}
$coreReady=Install-CatalogueDependency -GameDir $payoutGameRoot -Name Core -DependencyDirectory $DependencyDirectory -Caller $PSCmdlet
if(-not $coreReady) {return}
Write-Output 'Requested Payout 1.2.0 and its shared Core library are installed.'
Write-Output 'Credit card: your stated price is added to the original reward after successful verification.'
Write-Output 'Gift card: your stated price replaces the original reward after the correct fictional code is verified.'
Write-Output 'In multiplayer, the host must install the mod. Open F1 to adjust its settings.'
