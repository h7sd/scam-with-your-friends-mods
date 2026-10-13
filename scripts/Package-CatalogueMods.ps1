<# Creates only flat DLL + optional setup-script ZIPs for the stock launcher. #>
[CmdletBinding()]
param(
    [string]$ProjectDirectory=(Split-Path -Parent $PSScriptRoot),
    [ValidateSet('All','ElevenLabs','RequestedPayout','TikTok')][string]$Mod='All',
    [switch]$UseExistingRuntime
)
$ErrorActionPreference='Stop'
$catalogueProject=[IO.Path]::GetFullPath($ProjectDirectory)
$cataloguePackageRoot=Join-Path $catalogueProject 'build/catalogue-packages'
[IO.Directory]::CreateDirectory($cataloguePackageRoot)|Out-Null
if($Mod -ne 'RequestedPayout'){
    if(-not $UseExistingRuntime){& (Join-Path $PSScriptRoot 'Package-CatalogueRuntime.ps1') -ProjectDirectory $catalogueProject}
    $digests=Get-Content -LiteralPath (Join-Path $cataloguePackageRoot 'runtime-digests.json') -Raw|ConvertFrom-Json
}
$commonCore='dist/BepInEx/core/ScamWYF.Modding.Core.dll'
$definitions=@(
    @{Key='ElevenLabs';Repo='ElevenLabs-Agents';Version='1.0.1';Script='setup-elevenlabs-agents.ps1';Dlls=[ordered]@{
        'ScamWYF.ElevenLabsAgents.dll'='mod/bin/ScamWYF.ElevenLabsAgents.dll';'ScamWYF.AiBackend.dll'='upstream/AI-Backend/bin/ScamWYF.AiBackend.dll';'ScamWYF.Modding.Core.dll'=$commonCore}},
    @{Key='RequestedPayout';Repo='Requested-Payout';Version='1.2.0';Script='post-install-requested-payout.ps1';Dlls=[ordered]@{
        'ScamWYF.RequestedPayout.dll'='payout-mod/bin/ScamWYF.RequestedPayout.dll';'ScamWYF.Modding.Core.dll'=$commonCore}},
    @{Key='TikTok';Repo='TikTok-Pocket';Version='1.0.1';Script='setup-tiktok-pocket.ps1';Dlls=[ordered]@{
        'ScamWYF.TikTokPocket.dll'='tiktok-mod/bin/ScamWYF.TikTokPocket.dll';'ScamWYF.Modding.Core.dll'=$commonCore}}
)
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$catalogueBuilt=[Collections.Generic.List[object]]::new()
foreach($definition in $definitions){
    if($Mod -ne 'All' -and $definition.Key -ne $Mod){continue}
    $scriptPath=Join-Path $catalogueProject ('scripts/post-install/'+$definition.Script)
    $scriptBody=[IO.File]::ReadAllText($scriptPath)
    if($definition.Key -eq 'ElevenLabs'){
        $scriptBody=$scriptBody.Replace('__ELEVENLABS_BRIDGE_SHA256__',$digests.bridgeArchiveSha256).Replace('__ELEVENLABS_BRIDGE_MANIFEST_SHA256__',$digests.bridgeManifestSha256)
    } elseif($definition.Key -eq 'TikTok'){
        $scriptBody=$scriptBody.Replace('__TIKTOK_RUNTIME_SHA256__',$digests.tikTokArchiveSha256).Replace('__TIKTOK_WORKER_MANIFEST_SHA256__',$digests.tikTokManifestSha256).Replace('__TIKTOK_ICON_SHA256__',$digests.tikTokIconSha256)
        $scriptBody=$scriptBody.Replace('__TIKTOK_LICENSE_SHA256__',(Get-FileHash -LiteralPath (Join-Path $catalogueProject 'LICENSE')).Hash.ToLowerInvariant())
    }
    if($scriptBody -match '__[A-Z0-9_]+__'){throw 'A setup-script release pin was not resolved.'}
    $licensePaths=@('LICENSE','upstream/AI-Backend/vendor/ScamWYF.Modding.Core/LICENSE')
    if($definition.Key -eq 'ElevenLabs'){$licensePaths+='upstream/AI-Backend/LICENSE'}
    $licenseHeader="<#`nIncluded DLL licence notices:`n"+(@($licensePaths|ForEach-Object {[IO.File]::ReadAllText((Join-Path $catalogueProject $_))}) -join "`n`n")+"`n#>`n"
    $scriptBytes=[Text.UTF8Encoding]::new($false).GetBytes($licenseHeader+$scriptBody)
    if($scriptBytes.Length -gt 65536){throw 'Post-install script exceeds the stock launcher 64 KiB limit.'}
    $tokens=$null;$errors=$null
    [Management.Automation.Language.Parser]::ParseInput(([Text.Encoding]::UTF8.GetString($scriptBytes)),[ref]$tokens,[ref]$errors)|Out-Null
    if($errors){throw ('Generated setup script does not parse: '+($errors.Message -join ' | '))}
    $zipPath=Join-Path $cataloguePackageRoot ($definition.Repo+'-'+$definition.Version+'.zip')
    $stream=[IO.File]::Open($zipPath,[IO.FileMode]::Create)
    $archive=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach($dll in $definition.Dlls.GetEnumerator()){
            $source=Join-Path $catalogueProject $dll.Value
            if(-not (Test-Path -LiteralPath $source -PathType Leaf)){throw ('Required mod DLL missing: '+$dll.Key)}
            $identity=[Reflection.AssemblyName]::GetAssemblyName($source)
            if($identity.Name+'.dll' -cne $dll.Key){throw 'Mod DLL assembly identity does not match filename.'}
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$source,$dll.Key,[IO.Compression.CompressionLevel]::Optimal)|Out-Null
        }
        $entry=$archive.CreateEntry($definition.Script,[IO.Compression.CompressionLevel]::Optimal)
        $write=$entry.Open();try {$write.Write($scriptBytes,0,$scriptBytes.Length)}finally{$write.Dispose()}
    } finally {$archive.Dispose();$stream.Dispose()}
    if((Get-Item -LiteralPath $zipPath).Length -gt 33554432){throw 'Mod ZIP exceeds the stock launcher 32 MiB limit.'}
    $read=[IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $expected=@($definition.Dlls.Keys)+@($definition.Script)
        if($read.Entries.Count -ne $expected.Count -or @($read.Entries|Where-Object {$expected -cnotcontains $_.FullName}).Count){throw 'Thin mod ZIP contains unexpected entries.'}
    } finally {$read.Dispose()}
    $catalogueBuilt.Add([pscustomobject]@{Name=[IO.Path]::GetFileName($zipPath);Length=(Get-Item -LiteralPath $zipPath).Length;Sha256=(Get-FileHash -LiteralPath $zipPath).Hash.ToLowerInvariant()})
}
$catalogueBuilt.ToArray()|Format-Table Name,Length,Sha256 -AutoSize
