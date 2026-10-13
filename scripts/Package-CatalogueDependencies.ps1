<# Packages each shared dependency separately, without combining plugin DLLs. #>
[CmdletBinding()]
param([string]$ProjectDirectory=(Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference='Stop'
$dependencyProject=[IO.Path]::GetFullPath($ProjectDirectory)
$dependencyOutput=Join-Path $dependencyProject 'build/catalogue-packages'
[IO.Directory]::CreateDirectory($dependencyOutput)|Out-Null
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$dependencyDefinitions=@(
    @{Key='core';Name='ScamWYF.Modding.Core';Version='1.0.4';Archive='Modding-Core-1.0.4.zip';Script='post-install-modding-core.ps1';Source='dist/BepInEx/core/ScamWYF.Modding.Core.dll';Folder='core';Licence='upstream/AI-Backend/vendor/ScamWYF.Modding.Core/LICENSE'},
    @{Key='aiBackend';Name='ScamWYF.AiBackend';Version='1.1.0';Archive='AI-Backend-1.1.0.zip';Script='post-install-ai-backend.ps1';Source='upstream/AI-Backend/bin/ScamWYF.AiBackend.dll';Folder='plugins';Licence='upstream/AI-Backend/LICENSE'}
)
$dependencyDigests=[ordered]@{}
foreach($definition in $dependencyDefinitions){
    $source=Join-Path $dependencyProject $definition.Source
    $identity=[Reflection.AssemblyName]::GetAssemblyName($source)
    if($identity.Name -cne $definition.Name -or $identity.Version -ne [version]($definition.Version+'.0')){throw 'Unexpected dependency assembly identity.'}
    $notice="<#`n"+[IO.File]::ReadAllText((Join-Path $dependencyProject $definition.Licence))+"`n#>`n"
    $notice+="[CmdletBinding()]`nparam([Parameter(Mandatory=`$true)][string]`$GameDir)`n"
    $notice+="`$ErrorActionPreference='Stop'`n`$installed=Join-Path ([IO.Path]::GetFullPath(`$GameDir)) 'BepInEx/"+$definition.Folder+'/'+$definition.Name+".dll'`n"
    $notice+="if(-not (Test-Path -LiteralPath `$installed -PathType Leaf)){throw 'Install this dependency DLL in its declared BepInEx folder first.'}`n"
    $notice+="Write-Output '"+$definition.Name+' '+$definition.Version+" dependency is installed. Configure its consuming mod separately.'`n"
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes($notice)
    $tokens=$null;$errors=$null
    [Management.Automation.Language.Parser]::ParseInput($notice,[ref]$tokens,[ref]$errors)|Out-Null
    if($errors -or $bytes.Length -gt 65536){throw 'Invalid dependency notice script.'}
    $zip=Join-Path $dependencyOutput $definition.Archive
    $stream=[IO.File]::Open($zip,[IO.FileMode]::Create)
    $archive=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create)
    try{
        $dllEntry=$archive.CreateEntry(($definition.Name+'.dll'),[IO.Compression.CompressionLevel]::Optimal)
        $dllEntry.LastWriteTime=[DateTimeOffset]'2000-01-01T00:00:00+00:00'
        $input=[IO.File]::OpenRead($source);$write=$dllEntry.Open()
        try{$input.CopyTo($write)}finally{$write.Dispose();$input.Dispose()}
        $entry=$archive.CreateEntry($definition.Script,[IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime=[DateTimeOffset]'2000-01-01T00:00:00+00:00'
        $write=$entry.Open();try{$write.Write($bytes,0,$bytes.Length)}finally{$write.Dispose()}
    }finally{$archive.Dispose();$stream.Dispose()}
    $dependencyDigests[$definition.Key+'ArchiveSha256']=(Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant()
    $dependencyDigests[$definition.Key+'DllSha256']=(Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant()
    Write-Output ($definition.Archive+': one dependency DLL and one licence/verification script.')
}
[IO.File]::WriteAllText((Join-Path $dependencyOutput 'dependency-digests.json'),($dependencyDigests|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
