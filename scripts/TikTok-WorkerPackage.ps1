<# Shared validation for the exact TikTok Pocket browser-worker payload. #>

function Test-TikTokWorkerRelativePath {
    param([string]$Relative)
    if ([string]::IsNullOrEmpty($Relative) -or $Relative.Length -gt 200) { return $false }
    if ($Relative -ceq 'ScamWYF.TikTokBrowser.exe' -or
        $Relative -ceq 'ScamWYF.TikTokBrowser.deps.json' -or
        $Relative -ceq 'ScamWYF.TikTokBrowser.runtimeconfig.json') { return $true }
    return $Relative -cmatch '\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\.dll\z' -or
        $Relative -cmatch '\Aruntimes/win-x64/native/[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\.dll\z' -or
        $Relative -cmatch '\ALicenses/[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\.(txt|md)\z'
}

function Assert-TikTokWorkerPath {
    param([string]$Root, [string]$Path)
    $workerBoundary = [IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    $workerTarget = [IO.Path]::GetFullPath($Path)
    if ($workerTarget -ne $workerBoundary -and
        -not $workerTarget.StartsWith($workerBoundary + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'A browser-worker path would escape its package or game folder.'
    }
    $workerCurrent = $workerBoundary
    $workerSegments = @('')
    if ($workerTarget.Length -gt $workerBoundary.Length) {
        $workerSegments += $workerTarget.Substring($workerBoundary.Length+1).Split([IO.Path]::DirectorySeparatorChar)
    }
    foreach ($workerSegment in $workerSegments) {
        if ($workerSegment) { $workerCurrent = Join-Path $workerCurrent $workerSegment }
        if (Test-Path -LiteralPath $workerCurrent) {
            if (([IO.File]::GetAttributes($workerCurrent) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Browser-worker file operations do not follow symbolic links or junctions.'
            }
        }
    }
}

function Get-TikTokWorkerPackage {
    param([Parameter(Mandatory=$true)][string]$WorkerRoot, [string]$Version='1.0.1')
    $workerRootPath = [IO.Path]::GetFullPath($WorkerRoot).TrimEnd('\','/')
    $workerManifestPath = Join-Path $workerRootPath 'worker-files.json'
    Assert-TikTokWorkerPath -Root $workerRootPath -Path $workerManifestPath
    if (-not (Test-Path -LiteralPath $workerManifestPath -PathType Leaf)) { throw 'The TikTok browser-worker manifest is missing.' }
    if ((Get-Item -LiteralPath $workerManifestPath).Length -gt 131072) { throw 'The browser-worker manifest is too large.' }
    try { $workerManifest = [IO.File]::ReadAllText($workerManifestPath) | ConvertFrom-Json }
    catch { throw 'The browser-worker manifest is not valid JSON.' }
    if ($null -eq $workerManifest -or @($workerManifest.PSObject.Properties.Name).Count -ne 4 -or
        @($workerManifest.PSObject.Properties.Name | Where-Object { @('schemaVersion','appVersion','executable','files') -cnotcontains $_ }).Count -ne 0) {
        throw 'Unexpected browser-worker manifest fields.'
    }
    foreach ($workerField in @('schemaVersion','appVersion','executable','files')) {
        if (-not $workerManifest.PSObject.Properties[$workerField]) { throw 'The browser-worker manifest is incomplete.' }
    }
    if (($workerManifest.schemaVersion -isnot [int] -and $workerManifest.schemaVersion -isnot [long]) -or
        $workerManifest.schemaVersion -ne 1 -or $workerManifest.appVersion -cne $Version -or
        $workerManifest.executable -cne 'ScamWYF.TikTokBrowser.exe') { throw 'Unexpected browser-worker identity or version.' }
    if ($workerManifest.files -isnot [array]) { throw 'Browser-worker files must be an array.' }
    $workerEntries = @($workerManifest.files)
    if ($workerEntries.Count -lt 1 -or $workerEntries.Count -gt 512) { throw 'Unexpected browser-worker file count.' }
    $workerSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $workerFiles = [Collections.Generic.List[object]]::new()
    [long]$workerTotal = 0
    foreach ($workerEntry in $workerEntries) {
        if ($null -eq $workerEntry -or @($workerEntry.PSObject.Properties.Name).Count -ne 3 -or
            @($workerEntry.PSObject.Properties.Name | Where-Object { @('path','size','sha256') -cnotcontains $_ }).Count -ne 0) {
            throw 'Unexpected browser-worker file fields.'
        }
        foreach ($workerField in @('path','size','sha256')) {
            if ($null -eq $workerEntry -or -not $workerEntry.PSObject.Properties[$workerField]) { throw 'A browser-worker file entry is incomplete.' }
        }
        if ($workerEntry.path -isnot [string] -or -not (Test-TikTokWorkerRelativePath $workerEntry.path) -or
            -not $workerSeen.Add($workerEntry.path)) { throw 'The browser-worker manifest contains an unexpected or duplicate path.' }
        if (($workerEntry.size -isnot [int] -and $workerEntry.size -isnot [long]) -or
            $workerEntry.size -le 0 -or $workerEntry.size -gt 268435456 -or
            $workerEntry.sha256 -isnot [string] -or $workerEntry.sha256 -cnotmatch '\A[0-9a-f]{64}\z') {
            throw 'Invalid browser-worker file length or digest.'
        }
        $workerTotal += [long]$workerEntry.size
        if ($workerTotal -gt 268435456) { throw 'The browser-worker payload exceeds the package size limit.' }
        $workerFullPath = Join-Path $workerRootPath $workerEntry.path.Replace('/',[IO.Path]::DirectorySeparatorChar)
        Assert-TikTokWorkerPath -Root $workerRootPath -Path $workerFullPath
        if (-not (Test-Path -LiteralPath $workerFullPath -PathType Leaf) -or
            (Get-Item -LiteralPath $workerFullPath).Length -ne $workerEntry.size -or
            (Get-FileHash -LiteralPath $workerFullPath -Algorithm SHA256).Hash -ine $workerEntry.sha256) {
            throw ('A browser-worker file is missing or differs from its manifest: '+$workerEntry.path)
        }
        $workerFiles.Add([pscustomobject]@{ Path=$workerEntry.path; FullPath=$workerFullPath; Size=[long]$workerEntry.size; Sha256=$workerEntry.sha256.ToLowerInvariant() })
    }
    if (-not $workerSeen.Contains('ScamWYF.TikTokBrowser.exe')) { throw 'The browser-worker executable is not listed in its manifest.' }
    return [pscustomobject]@{ ManifestPath=$workerManifestPath; Executable='ScamWYF.TikTokBrowser.exe'; Files=$workerFiles.ToArray(); TotalSize=$workerTotal }
}
