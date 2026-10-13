param([string]$DotNet = 'dotnet')
$ErrorActionPreference = 'Stop'
$workerRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$publishRoot = [IO.Path]::GetFullPath((Join-Path $workerRoot 'publish'))
if (-not $publishRoot.StartsWith($workerRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid publish path.' }
if ((Test-Path -LiteralPath $publishRoot) -and ((Get-Item -LiteralPath $publishRoot).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unexpected publish reparse point.' }
if (Test-Path -LiteralPath $publishRoot) {
    Get-ChildItem -LiteralPath $publishRoot -Recurse -Directory | ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unexpected publish reparse point.' }
    }
}
Push-Location $workerRoot
try {
    $sdkVersion = (& $DotNet --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -ne '8.0.425') { throw '.NET SDK 8.0.425 is required.' }
    & $DotNet publish ScamWYF.TikTokBrowser.csproj -c Release -r win-x64 --self-contained true -o $publishRoot --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Worker publish failed.' }
    # Remove only known old SDK documentation from earlier local publishes.
    foreach ($obsoleteName in @('Microsoft.Web.WebView2.Core.xml','Microsoft.Web.WebView2.WinForms.xml','Microsoft.Web.WebView2.Wpf.xml')) {
        $obsoletePath = Join-Path $publishRoot $obsoleteName
        if (Test-Path -LiteralPath $obsoletePath) {
            if ((Get-Item -LiteralPath $obsoletePath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unexpected publish reparse point.' }
            Remove-Item -LiteralPath $obsoletePath
        }
    }
    $files = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File | Where-Object { $_.Name -ne 'worker-files.json' } | Sort-Object FullName | ForEach-Object {
        if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unexpected publish reparse point.' }
        $filePath = [IO.Path]::GetFullPath($_.FullName)
        $publishBoundary = $publishRoot.TrimEnd('\') + '\'
        if (-not $filePath.StartsWith($publishBoundary,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected publish path.' }
        $relative = $filePath.Substring($publishBoundary.Length).Replace('\','/')
        $allowed = $relative -ceq 'ScamWYF.TikTokBrowser.exe' -or $relative -ceq 'ScamWYF.TikTokBrowser.deps.json' -or $relative -ceq 'ScamWYF.TikTokBrowser.runtimeconfig.json' -or
            $relative -cmatch '\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\.dll\z' -or
            $relative -cmatch '\Aruntimes/win-x64/native/[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\.dll\z' -or
            $relative -cmatch '\ALicenses/[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\.(txt|md)\z'
        if (-not $allowed) { throw ('Unexpected publish file: ' + $relative) }
        [pscustomobject][ordered]@{path=$relative;size=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
    if ($files.Count -lt 1 -or $files.Count -gt 512 -or ($files | Measure-Object -Property size -Sum).Sum -gt 268435456) { throw 'Unexpected worker payload size.' }
    $manifest = [ordered]@{schemaVersion=1;appVersion='1.0.1';executable='ScamWYF.TikTokBrowser.exe';files=$files}
    [IO.File]::WriteAllText((Join-Path $publishRoot 'worker-files.json'),($manifest | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
    Write-Output ('Published ' + $files.Count + ' verified worker files to ' + $publishRoot)
} finally { Pop-Location }
