Set-StrictMode -Version Latest

function Resolve-ScamGameDirectory {
    param([string]$GameDirectory)

    if ($GameDirectory) {
        $resolved = [IO.Path]::GetFullPath($GameDirectory)
    }
    elseif ($env:SWYG_GAME_DIR) {
        $resolved = [IO.Path]::GetFullPath($env:SWYG_GAME_DIR)
    }
    else {
        $steamRoots = [Collections.Generic.List[string]]::new()
        foreach ($candidate in @(
            (Join-Path ${env:ProgramFiles(x86)} 'Steam'),
            (Join-Path $env:ProgramFiles 'Steam'),
            (Join-Path $env:LOCALAPPDATA 'Steam')
        )) {
            if (Test-Path -LiteralPath $candidate -PathType Container) { $steamRoots.Add($candidate) }
        }
        foreach ($keyPath in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam')) {
            $key = Get-ItemProperty -LiteralPath $keyPath -ErrorAction SilentlyContinue
            if ($key) {
                foreach ($property in @('SteamPath', 'InstallPath')) {
                    $value = $key.PSObject.Properties[$property]
                    if ($value -and $value.Value) { $steamRoots.Add([string]$value.Value) }
                }
            }
        }
        $libraries = [Collections.Generic.List[string]]::new()
        foreach ($steamRoot in @($steamRoots | Select-Object -Unique)) {
            $libraries.Add($steamRoot)
            $vdf = Join-Path $steamRoot 'steamapps\libraryfolders.vdf'
            if (Test-Path -LiteralPath $vdf -PathType Leaf) {
                $text = [IO.File]::ReadAllText($vdf)
                foreach ($match in [regex]::Matches($text, '"path"\s*"([^"]+)"')) {
                    $libraries.Add($match.Groups[1].Value.Replace('\\', '\'))
                }
            }
        }
        $games = @(
            foreach ($library in @($libraries | Select-Object -Unique)) {
                foreach ($name in @('Scam With Your Friends', 'Scam With Your Friends Playtest')) {
                    $candidate = Join-Path $library (Join-Path 'steamapps\common' $name)
                    if (Test-Path -LiteralPath (Join-Path $candidate 'Scam With Your Friends.exe') -PathType Leaf) {
                        [IO.Path]::GetFullPath($candidate)
                    }
                }
            }
        ) | Sort-Object -Unique
        $games = @($games)
        if ($games.Count -ne 1) {
            throw 'No unique game installation was found. Pass -GameDirectory with the game folder.'
        }
        $resolved = [string]$games[0]
    }

    if (-not (Test-Path -LiteralPath (Join-Path $resolved 'Scam With Your Friends.exe') -PathType Leaf)) {
        throw "The selected folder is not a game installation: $resolved"
    }
    return $resolved
}

function Assert-ScamGameClosed {
    foreach ($name in @('Scam With Your Friends', 'Scam With Your Friends Playtest')) {
        if (Get-Process -Name $name -ErrorAction SilentlyContinue) {
            throw 'Close Scam With Your Friends before installing or changing its config.'
        }
    }
}

function Get-ScamChildPath {
    param([string]$Root, [string]$Relative)
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $targetPath = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (-not $targetPath.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A file operation would escape its intended folder.'
    }
    return $targetPath
}
