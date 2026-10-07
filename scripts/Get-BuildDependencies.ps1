[CmdletBinding()]
param([string]$ProjectDirectory = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$gitCommand = Get-Command git -ErrorAction SilentlyContinue
if (-not $gitCommand) { throw 'Git is required. Install Git for Windows and reopen PowerShell.' }
$root = [IO.Path]::GetFullPath($ProjectDirectory)
$dependencies = @(
    @{ Name='AI-Backend'; Url='https://github.com/swyf-modding/AI-Backend.git'; Revision='ce48428edf19cec41bc87f049dd54988cdb1c433' },
    @{ Name='Launcher'; Url='https://github.com/swyf-modding/Launcher.git'; Revision='7d67b0d4a4d0025a5590845605ecbff29af5ae38' },
    @{ Name='Setup'; Url='https://github.com/swyf-modding/Setup.git'; Revision='7a352f1b5d7f2098eac70d8e9b48d1dd1002bc2a' }
)
foreach ($dependency in $dependencies) {
    $checkout = Join-Path $root ('upstream\' + $dependency.Name)
    if (Test-Path -LiteralPath $checkout) {
        $current = & $gitCommand.Source -C $checkout rev-parse HEAD
        if ($LASTEXITCODE -ne 0 -or $current -ne $dependency.Revision) {
            throw "Existing $checkout does not match the required revision. Keep your changes and choose a fresh project directory."
        }
    }
    else {
        & $gitCommand.Source clone --no-checkout $dependency.Url $checkout
        if ($LASTEXITCODE -ne 0) { throw "Could not clone $($dependency.Name)." }
        & $gitCommand.Source -C $checkout checkout --detach $dependency.Revision
        if ($LASTEXITCODE -ne 0) { throw "Could not select $($dependency.Name) revision." }
    }
    if ($dependency.Name -eq 'AI-Backend') {
        & $gitCommand.Source -C $checkout submodule update --init --recursive
        if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the shared mod library.' }
    }
    Write-Output "$($dependency.Name): $($dependency.Revision)"
}
