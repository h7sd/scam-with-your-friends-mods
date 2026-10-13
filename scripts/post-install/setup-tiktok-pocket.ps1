<# Optional stock-launcher setup for TikTok Pocket 1.0.1.
   Installs only the pinned browser worker, original icon and licence notices.
   Existing browser profiles/configuration are never accessed or migrated.
   Requires the separately installed Microsoft Edge WebView2 Runtime.

   MIT License
   TikTok Pocket mod, original icon and setup:
   Copyright (c) 2026 SWYF Community Mods contributors
   Shared ScamWYF.Modding.Core library:
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
[CmdletBinding(SupportsShouldProcess = $true)]
param([Parameter(Mandatory = $true)][string]$GameDir)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$runtimeUrl = 'https://github.com/h7sd/scam-with-your-friends-mods/releases/download/catalogue-runtime-v1.0.1/TikTok-Pocket-Runtime-1.0.1-win-x64.zip'
$runtimeSha256 = '__TIKTOK_RUNTIME_SHA256__'
$manifestSha256 = '__TIKTOK_WORKER_MANIFEST_SHA256__'
$iconSha256 = '__TIKTOK_ICON_SHA256__'
$licenseSha256 = '__TIKTOK_LICENSE_SHA256__'
$workerFiles = @(
    'ScamWYF.TikTokBrowser.exe',
    'Licenses/CsWinRT-LICENSE.txt', 'Licenses/DEPENDENCIES.md',
    'Licenses/DotNet-LICENSE.txt', 'Licenses/DotNet-ThirdPartyNotices.txt',
    'Licenses/DotNet-WindowsDesktop-LICENSE.txt',
    'Licenses/Microsoft-WebView2-LICENSE.txt', 'Licenses/Microsoft-WebView2-NOTICE.txt',
    'Licenses/SharpGen-LICENSE.txt',
    'Licenses/System-IO-Pipelines-LICENSE.txt', 'Licenses/System-IO-Pipelines-NOTICES.txt',
    'Licenses/System-Text-Encodings-Web-LICENSE.txt', 'Licenses/System-Text-Encodings-Web-NOTICES.txt',
    'Licenses/System-Text-Json-LICENSE.txt', 'Licenses/System-Text-Json-NOTICES.txt',
    'Licenses/Vortice-Mathematics-LICENSE.txt', 'Licenses/Vortice-Windows-LICENSE.txt',
    'Licenses/Windows-SDK-LICENSE.txt'
)
$maximumBytes = 268435456

function Assert-Closed {
    foreach ($name in @('Scam With Your Friends', 'Scam With Your Friends Playtest', 'ScamWYF.TikTokBrowser')) {
        if (Get-Process -Name $name -ErrorAction SilentlyContinue) { throw 'Close Scam With Your Friends and TikTok Pocket before running setup.' }
    }
}

function Assert-Path {
    param([string]$Root, [string]$Path)
    $boundary = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $target = [IO.Path]::GetFullPath($Path)
    if ($target -ne $boundary -and -not $target.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A setup path would escape its intended folder.'
    }
    # Check ancestors too: a selected game/temp folder must not resolve through a junction.
    $current = $target
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Setup does not follow symbolic links or junctions.' }
        }
        $next = [IO.Path]::GetDirectoryName($current)
        if ($next -eq $current) { break }
        $current = $next
    }
}

function Child-Path {
    param([string]$Root, [string]$Relative)
    $target = [IO.Path]::GetFullPath((Join-Path $Root $Relative.Replace('/', [IO.Path]::DirectorySeparatorChar)))
    Assert-Path $Root $target
    return $target
}

function Assert-Hash {
    param([string]$Path, [string]$Expected)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Expected) {
        throw 'A TikTok runtime file does not match the pinned SHA-256 digest.'
    }
}

function Download-Runtime {
    param([string]$Destination)
    Add-Type -AssemblyName System.Net.Http
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(120)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('TikTokPocket-Setup/1.0.1')
    $timeout = [Threading.CancellationTokenSource]::new()
    $timeout.CancelAfter([TimeSpan]::FromMinutes(5))
    $oldTls = [Net.ServicePointManager]::SecurityProtocol
    [Net.ServicePointManager]::SecurityProtocol = $oldTls -bor [Net.SecurityProtocolType]::Tls12
    $response = $null
    try {
        $uri = [Uri]$runtimeUrl
        for ($redirect = 0; $redirect -le 5; $redirect++) {
            if ($uri.Scheme -cne 'https' -or $uri.UserInfo -or $uri.Port -ne 443 -or
                @('github.com', 'release-assets.githubusercontent.com', 'objects.githubusercontent.com') -cnotcontains $uri.DnsSafeHost) {
                throw 'The runtime download redirected outside the allowed HTTPS hosts.'
            }
            $response = $client.GetAsync($uri, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $timeout.Token).GetAwaiter().GetResult()
            if ([int]$response.StatusCode -in @(301, 302, 303, 307, 308)) {
                $location = $response.Headers.Location
                if ($null -eq $location -or $redirect -eq 5) { throw 'Unexpected runtime download redirect.' }
                $uri = [Uri]::new($uri, $location)
                $response.Dispose(); $response = $null
                continue
            }
            if (-not $response.IsSuccessStatusCode) { throw 'The pinned TikTok runtime download is unavailable.' }
            $contentLength = $response.Content.Headers.ContentLength
            if ($null -ne $contentLength -and $contentLength -gt $maximumBytes) { throw 'The runtime archive is too large.' }
            $source = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $output = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try {
                $buffer = [byte[]]::new(65536)
                [long]$total = 0
                while (($count = $source.ReadAsync($buffer, 0, $buffer.Length, $timeout.Token).GetAwaiter().GetResult()) -gt 0) {
                    $total += $count
                    if ($total -gt $maximumBytes) { throw 'The runtime archive is too large.' }
                    $output.Write($buffer, 0, $count)
                }
                if ($total -le 0) { throw 'The runtime archive is empty.' }
            }
            finally { $output.Dispose(); $source.Dispose() }
            return
        }
        throw 'The pinned TikTok runtime download is unavailable.'
    }
    catch { throw 'Could not download the pinned TikTok runtime safely. No game files were changed; try setup again when the release is available.' }
    finally {
        if ($null -ne $response) { $response.Dispose() }
        $client.Dispose(); $handler.Dispose(); $timeout.Dispose()
        [Net.ServicePointManager]::SecurityProtocol = $oldTls
    }
}

function Expand-Runtime {
    param([string]$ArchivePath, [string]$Destination)
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($relative in $workerFiles) { [void]$expected.Add('TikTokPocket/BrowserWorker/' + $relative) }
    foreach ($relative in @('TikTokPocket/BrowserWorker/worker-files.json', 'TikTokPocket/tiktok-pocket.png', 'TikTokPocket/LICENSE.txt')) { [void]$expected.Add($relative) }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        if ($archive.Entries.Count -ne $expected.Count) { throw 'The runtime archive has unexpected files.' }
        [long]$total = 0
        foreach ($entry in $archive.Entries) {
            if (-not $expected.Contains($entry.FullName) -or -not $seen.Add($entry.FullName) -or $entry.Length -le 0 -or $entry.Length -gt $maximumBytes) { throw 'The runtime archive has unexpected or duplicate paths.' }
            $total += $entry.Length
            if ($total -gt $maximumBytes -or (($entry.ExternalAttributes -shr 16) -band 61440) -eq 40960 -or ($entry.ExternalAttributes -band 1024) -ne 0) { throw 'The runtime archive has unsafe entries.' }
        }
        foreach ($entry in $archive.Entries) {
            $path = Child-Path $Destination $entry.FullName
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
            $entryStream = $entry.Open()
            $output = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try {
                $buffer = [byte[]]::new(65536)
                [long]$written = 0
                while (($count = $entryStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $written += $count
                    if ($written -gt $entry.Length) { throw 'A runtime archive entry exceeds its declared size.' }
                    $output.Write($buffer, 0, $count)
                }
                if ($written -ne $entry.Length) { throw 'A runtime archive entry is truncated.' }
            }
            finally { $output.Dispose(); $entryStream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}

function Assert-Manifest {
    param([string]$PackageRoot)
    $root = Child-Path $PackageRoot 'TikTokPocket/BrowserWorker'
    $path = Child-Path $root 'worker-files.json'
    Assert-Hash $path $manifestSha256
    if ((Get-Item -LiteralPath $path).Length -gt 131072) { throw 'The worker manifest is too large.' }
    try { $manifest = [IO.File]::ReadAllText($path) | ConvertFrom-Json }
    catch { throw 'Invalid worker manifest.' }
    if ($null -eq $manifest -or @($manifest.PSObject.Properties.Name).Count -ne 4 -or
        @($manifest.PSObject.Properties.Name | Where-Object { @('schemaVersion', 'appVersion', 'executable', 'files') -cnotcontains $_ }).Count -ne 0 -or
        ($manifest.schemaVersion -isnot [int] -and $manifest.schemaVersion -isnot [long]) -or $manifest.schemaVersion -ne 1 -or
        $manifest.appVersion -cne '1.0.1' -or $manifest.executable -cne 'ScamWYF.TikTokBrowser.exe' -or
        $manifest.files -isnot [array] -or $manifest.files.Count -ne $workerFiles.Count) { throw 'Unexpected worker manifest identity or inventory.' }
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($relative in $workerFiles) { [void]$expected.Add($relative) }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [long]$total = 0
    foreach ($file in $manifest.files) {
        if ($null -eq $file -or @($file.PSObject.Properties.Name).Count -ne 3 -or
            @($file.PSObject.Properties.Name | Where-Object { @('path', 'size', 'sha256') -cnotcontains $_ }).Count -ne 0 -or
            $file.path -isnot [string] -or -not $expected.Contains($file.path) -or -not $seen.Add($file.path) -or
            ($file.size -isnot [int] -and $file.size -isnot [long]) -or $file.size -le 0 -or $file.size -gt $maximumBytes -or
            $file.sha256 -isnot [string] -or $file.sha256 -cnotmatch '\A[0-9a-f]{64}\z') { throw 'Invalid worker manifest file.' }
        $total += [long]$file.size
        if ($total -gt $maximumBytes) { throw 'The worker payload is too large.' }
        $source = Child-Path $root $file.path
        Assert-Hash $source $file.sha256
        if ((Get-Item -LiteralPath $source).Length -ne $file.size) { throw 'A worker file has an unexpected length.' }
    }
    Assert-Hash (Child-Path $PackageRoot 'TikTokPocket/tiktok-pocket.png') $iconSha256
    Assert-Hash (Child-Path $PackageRoot 'TikTokPocket/LICENSE.txt') $licenseSha256
    $icon = [IO.File]::ReadAllBytes((Child-Path $PackageRoot 'TikTokPocket/tiktok-pocket.png'))
    if ($icon.Length -lt 8 -or $icon.Length -gt 1048576 -or [BitConverter]::ToString($icon, 0, 8) -ne '89-50-4E-47-0D-0A-1A-0A') { throw 'Invalid TikTok icon.' }
}

function Assert-ExistingWorker {
    param([string]$Root)
    if (-not (Test-Path -LiteralPath $Root)) { return }
    Assert-Path $Root $Root
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($relative in @($workerFiles) + @('worker-files.json')) { [void]$expected.Add($relative) }
    foreach ($entry in Get-ChildItem -LiteralPath $Root -Force) {
        Assert-Path $Root $entry.FullName
        if ($entry.PSIsContainer) {
            if ($entry.Name -cne 'Licenses') { throw 'The existing BrowserWorker contains unexpected directories. Preserve those files outside this runtime folder before rerunning setup.' }
            foreach ($child in Get-ChildItem -LiteralPath $entry.FullName -Force) {
                Assert-Path $Root $child.FullName
                if ($child.PSIsContainer -or -not $expected.Contains('Licenses/' + $child.Name)) { throw 'The existing BrowserWorker has unexpected files.' }
            }
        }
        elseif (-not $expected.Contains($entry.Name)) { throw 'The existing BrowserWorker has unexpected files.' }
    }
}

function Remove-OwnTemporaryFolder {
    param([string]$Root, [string]$Path)
    Assert-Path $Root $Path
    if ([IO.Path]::GetDirectoryName($Path) -ne $Root -or [IO.Path]::GetFileName($Path) -cnotmatch '\A[0-9a-f]{32}\z') { throw 'Invalid temporary cleanup path.' }
    $pending = [Collections.Generic.Queue[string]]::new(); $pending.Enqueue($Path)
    while ($pending.Count -gt 0) {
        foreach ($entry in Get-ChildItem -LiteralPath $pending.Dequeue() -Force) {
            Assert-Path $Path $entry.FullName
            if ($entry.PSIsContainer) { $pending.Enqueue($entry.FullName) }
        }
    }
    Remove-Item -LiteralPath $Path -Recurse -Force
}

Assert-Closed
foreach ($digest in @($runtimeSha256, $manifestSha256, $iconSha256, $licenseSha256)) {
    if ($digest -cnotmatch '\A[0-9a-f]{64}\z') { throw 'This setup build is missing its pinned release digest.' }
}
$game = [IO.Path]::GetFullPath($GameDir).TrimEnd('\', '/')
Assert-Path $game $game
foreach ($required in @('Scam With Your Friends.exe', 'winhttp.dll', 'BepInEx/core/BepInEx.dll', 'unstripped_corlib/mscorlib.dll')) {
    if (-not (Test-Path -LiteralPath (Child-Path $game $required) -PathType Leaf)) { throw 'Select the game folder and complete BepInEx Setup in the launcher first.' }
}
$plugin = Child-Path $game 'BepInEx/plugins/ScamWYF.TikTokPocket.dll'
if (-not (Test-Path -LiteralPath $plugin -PathType Leaf)) { throw 'Install the TikTok Pocket mod DLL in the launcher before running its setup.' }
$identity = [Reflection.AssemblyName]::GetAssemblyName($plugin)
if ($identity.Name -cne 'ScamWYF.TikTokPocket' -or $identity.Version -ne [version]'1.0.1.0') { throw 'This runtime setup requires TikTok Pocket 1.0.1.' }
$core = Child-Path $game 'BepInEx/core/ScamWYF.Modding.Core.dll'
if (-not (Test-Path -LiteralPath $core -PathType Leaf)) { throw 'Install the shared mod library through the launcher first.' }
$coreIdentity = [Reflection.AssemblyName]::GetAssemblyName($core)
if ($coreIdentity.Name -cne 'ScamWYF.Modding.Core' -or $coreIdentity.Version -lt [version]'1.0.4.0') { throw 'TikTok Pocket requires shared mod library 1.0.4 or newer.' }
$destination = Child-Path $game 'BepInEx/plugins/TikTokPocket'
Assert-ExistingWorker (Child-Path $destination 'BrowserWorker')
if (-not $PSCmdlet.ShouldProcess($game, 'Download and verify TikTok Pocket 1.0.1 runtime, then back up and install its browser worker, icon and licences')) { return }

$temporaryRoot = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'ScamWYF-TikTok-Setup')).TrimEnd('\', '/')
$temporary = Child-Path $temporaryRoot ([guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null
$package = Child-Path $temporary 'package'
try {
    Write-Output 'Downloading the pinned TikTok Pocket browser runtime...'
    $archive = Child-Path $temporary 'runtime.zip'
    Download-Runtime $archive
    Assert-Hash $archive $runtimeSha256
    Expand-Runtime $archive $package
    Assert-Manifest $package
    Assert-Closed
    Assert-ExistingWorker (Child-Path $destination 'BrowserWorker')
    $copies = @('tiktok-pocket.png', 'LICENSE.txt', 'BrowserWorker/worker-files.json') + @($workerFiles | ForEach-Object { 'BrowserWorker/' + $_ })
    $backup = Child-Path $game ('BepInEx/tiktok-pocket-backups/' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $previous = @{}
    $written = [Collections.Generic.List[string]]::new()
    foreach ($relative in $copies) {
        $target = Child-Path $destination $relative
        if (Test-Path -LiteralPath $target) {
            if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw 'A runtime destination is not a file.' }
            $saved = Child-Path $backup ('TikTokPocket/' + $relative)
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($saved)) | Out-Null
            [IO.File]::Copy($target, $saved, $false)
            $previous[$relative] = $saved
        }
    }
    Assert-Closed
    try {
        foreach ($relative in $copies) {
            $target = Child-Path $destination $relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            $written.Add($relative)
            [IO.File]::Copy((Child-Path $package ('TikTokPocket/' + $relative)), $target, $true)
        }
        foreach ($relative in $copies) {
            $source = Child-Path $package ('TikTokPocket/' + $relative)
            Assert-Hash (Child-Path $destination $relative) ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash)
        }
    }
    catch {
        $rollbackFailed = $false
        foreach ($relative in $written) {
            try {
                $target = Child-Path $destination $relative
                if ($previous.ContainsKey($relative)) { [IO.File]::Copy($previous[$relative], $target, $true) }
                elseif (Test-Path -LiteralPath $target -PathType Leaf) { Remove-Item -LiteralPath $target -Force }
            }
            catch { $rollbackFailed = $true }
        }
        if ($rollbackFailed) { throw ('Runtime installation failed. Some files could not be restored; previous files remain in: ' + $backup) }
        throw 'Runtime installation failed; replaced files were restored from backup.'
    }
    Write-Output 'TikTok Pocket browser worker and icon installed. Browser profiles and mod settings were preserved.'
    if ($previous.Count -gt 0) { Write-Output ('Previous runtime files backed up in: ' + $backup) }
    $runtimePresent = $false
    foreach ($key in @('HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}')) {
        $installed = Get-ItemProperty -LiteralPath $key -Name pv -ErrorAction SilentlyContinue
        if ($installed -and $installed.pv -and $installed.pv -ne '0.0.0.0') { $runtimePresent = $true }
    }
    if (-not $runtimePresent) { Write-Output 'Microsoft Edge WebView2 Runtime is also required. Install it yourself from https://developer.microsoft.com/microsoft-edge/webview2/ . Setup does not install it automatically.' }
}
finally {
    try { Remove-OwnTemporaryFolder $temporaryRoot $temporary }
    catch { Write-Warning ('Temporary runtime download retained at: ' + $temporary) }
}
