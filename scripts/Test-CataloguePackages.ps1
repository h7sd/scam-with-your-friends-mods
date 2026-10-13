<# Offline package/metadata checks. Never loads plugin code, runs setup scripts or installs files. #>
[CmdletBinding()]
param(
    [string]$ProjectDirectory=(Split-Path -Parent $PSScriptRoot),
    [string]$PackageDirectory,
    [string]$CataloguePath,
    [string]$CecilPath
)
$ErrorActionPreference='Stop'
$catalogueAuditRoot=[IO.Path]::GetFullPath($ProjectDirectory)
if(-not $PackageDirectory){$PackageDirectory=Join-Path $catalogueAuditRoot 'build/catalogue-packages'}
if(-not $CataloguePath){$CataloguePath=Join-Path $catalogueAuditRoot 'catalogue/mods.json'}
if(-not $CecilPath){$CecilPath=Join-Path $catalogueAuditRoot 'upstream/Launcher/vendor/Mono.Cecil.dll'}
Add-Type -Path ([IO.Path]::GetFullPath($CecilPath))
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$catalogueAuditResults=[pscustomobject]@{Passed=0;NegativeCases=0}
$catalogueAuditExpected=@(
    [pscustomobject]@{Repo='ElevenLabs-Agents';Name='ElevenLabs Agents';Version='1.0.1';Zip='ElevenLabs-Agents-1.0.2.zip';Script='setup-elevenlabs-agents.ps1';Files=@('ScamWYF.ElevenLabsAgents.dll')},
    [pscustomobject]@{Repo='Requested-Payout';Name='Requested Payout';Version='1.2.0';Zip='Requested-Payout-1.2.1.zip';Script='post-install-requested-payout.ps1';Files=@('ScamWYF.RequestedPayout.dll')}
)
$catalogueAuditDependencyExpected=@(
    [pscustomobject]@{Repo='Modding-Core';Name='Shared Mod Library';Version='1.0.4';Zip='Modding-Core-1.0.4.zip';Script='post-install-modding-core.ps1';Files=@('ScamWYF.Modding.Core.dll')},
    [pscustomobject]@{Repo='AI-Backend';Name='AI Backend';Version='1.1.0';Zip='AI-Backend-1.1.0.zip';Script='post-install-ai-backend.ps1';Files=@('ScamWYF.AiBackend.dll')}
)
$catalogueAuditDependencies=@{}
$catalogueAuditDependencyPins=@{}
$catalogueAuditPluginIdentity=@{
    'ScamWYF.ElevenLabsAgents.dll'=@('com.community.scamwyf.elevenlabsagents','ElevenLabs Agents','1.0.1')
    'ScamWYF.AiBackend.dll'=@('com.community.scamwyf.aibackend','Scam WYF AI Backend','1.1.0')
    'ScamWYF.RequestedPayout.dll'=@('com.community.scamwyf.requestedpayout','Requested Payout','1.2.0')
}
$catalogueAuditCompiledSources=@{
    'ScamWYF.ElevenLabsAgents.dll'='mod/bin/ScamWYF.ElevenLabsAgents.dll'
    'ScamWYF.AiBackend.dll'='upstream/AI-Backend/bin/ScamWYF.AiBackend.dll'
    'ScamWYF.RequestedPayout.dll'='payout-mod/bin/ScamWYF.RequestedPayout.dll'
    'ScamWYF.Modding.Core.dll'='dist/BepInEx/core/ScamWYF.Modding.Core.dll'
}
function Assert-CatalogueCondition {
    param([bool]$Condition,[string]$Reason)
    if(-not $Condition){throw $Reason}
    $catalogueAuditResults.Passed++
}
function Assert-CataloguePropertySet {
    param($Object,[string[]]$Fields,[string]$Where)
    if($null -eq $Object){throw "$Where is missing."}
    $actual=@($Object.PSObject.Properties.Name)
    if($actual.Count -ne $Fields.Count -or @($actual|Where-Object {$Fields -cnotcontains $_}).Count){throw "$Where has unexpected or missing fields."}
}
function Assert-CatalogueDocument {
    param($Document)
    Assert-CataloguePropertySet $Document @('schemaVersion','mods') 'Catalogue'
    if(($Document.schemaVersion -isnot [int] -and $Document.schemaVersion -isnot [long]) -or $Document.schemaVersion -ne 1){throw 'Catalogue schemaVersion must be integer 1.'}
    if($Document.mods -isnot [array] -or $Document.mods.Count -ne $catalogueAuditExpected.Count){throw 'Catalogue must contain exactly the two active prepared mods.'}
    for($index=0;$index -lt $catalogueAuditExpected.Count;$index++){
        $entry=$Document.mods[$index];$expected=$catalogueAuditExpected[$index]
        Assert-CataloguePropertySet $entry @('owner','repo','displayName','summary','files','postInstall') $expected.Repo
        if($entry.owner -cne 'h7sd' -or $entry.repo -cne $expected.Repo -or $entry.displayName -cne $expected.Name){throw 'Unexpected catalogue identity or sort order.'}
        if($entry.summary -isnot [string] -or [string]::IsNullOrWhiteSpace($entry.summary) -or $entry.summary.Length -gt 500 -or $entry.displayName.Length -gt 80){throw 'Invalid catalogue summary or display name.'}
        if($entry.files -isnot [array] -or $entry.files.Count -ne $expected.Files.Count){throw 'Catalogue DLL set is incomplete.'}
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach($file in $entry.files){
            Assert-CataloguePropertySet $file @('fileName','folder','expectsPluginAttribute') 'DLL declaration'
            if($file.fileName -isnot [string] -or $expected.Files -cnotcontains $file.fileName -or -not $seen.Add($file.fileName)){throw 'Unexpected or duplicate declared DLL.'}
            $core=$file.fileName -ceq 'ScamWYF.Modding.Core.dll'
            if($file.expectsPluginAttribute -isnot [bool] -or $file.expectsPluginAttribute -eq $core -or $file.folder -cne $(if($core){'core'}else{'plugins'})){throw 'Incorrect shared Core/plugin routing or loader-attribute flag.'}
        }
        if($entry.postInstall -isnot [array] -or $entry.postInstall.Count -ne 1){throw 'Each prepared mod must declare exactly its one optional setup step.'}
        $step=$entry.postInstall[0]
        Assert-CataloguePropertySet $step @('script','description') 'Setup declaration'
        if($step.script -cne $expected.Script -or $step.script -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9_.-]*\.ps1\z' -or $step.script.Length -gt 100){throw 'Invalid optional setup script name.'}
        if($step.description -isnot [string] -or [string]::IsNullOrWhiteSpace($step.description) -or $step.description.Length -gt 280){throw 'Invalid optional setup description.'}
    }
}
function Get-CatalogueZipEntries {
    param([string]$Path)
    if((Get-Item -LiteralPath $Path).Length -gt 33554432){throw 'Thin package exceeds the stock launcher 32 MiB download limit.'}
    $archive=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Path))
    try{
        $result=[Collections.Generic.List[object]]::new()
        foreach($entry in $archive.Entries){
            # Inspect shape first, so a forbidden profile/env entry is never read.
            if($entry.FullName -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9_.-]*\.(dll|ps1)\z' -or $entry.FullName.Contains('..')){throw 'Thin package contains a non-root, non-DLL/script or private/runtime entry.'}
            if($entry.Length -le 0 -or $entry.Length -gt 16777216){throw 'Unexpected empty/oversized thin-package entry.'}
            $stream=$entry.Open();$memory=[IO.MemoryStream]::new()
            try{$stream.CopyTo($memory);$result.Add([pscustomobject]@{Name=$entry.FullName;Bytes=$memory.ToArray()})}
            finally{$memory.Dispose();$stream.Dispose()}
        }
        return ,$result.ToArray()
    }finally{$archive.Dispose()}
}
function Get-CatalogueAllTypes {
    param($Types)
    foreach($type in $Types){$type;Get-CatalogueAllTypes $type.NestedTypes}
}
function Read-CatalogueDll {
    param([byte[]]$Bytes,[string]$Name)
    $memory=[IO.MemoryStream]::new($Bytes,$false)
    try{
        $assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($memory)
        try{
            $plugins=@(foreach($type in (Get-CatalogueAllTypes $assembly.MainModule.Types)){foreach($attribute in $type.CustomAttributes){if($attribute.AttributeType.FullName -ceq 'BepInEx.BepInPlugin'){
                $args=$attribute.ConstructorArguments
                if($args.Count -ne 3){throw 'Malformed BepInPlugin metadata.'}
                [pscustomobject]@{Guid=[string]$args[0].Value;Name=[string]$args[1].Value;Version=[string]$args[2].Value}
            }}})
            $dependency=$null
            if($Name -ceq 'ScamWYF.ElevenLabsAgents.dll'){
                foreach($type in (Get-CatalogueAllTypes $assembly.MainModule.Types)){foreach($attribute in $type.CustomAttributes){if($attribute.AttributeType.FullName -ceq 'BepInEx.BepInDependency'){
                    # Decode the actual primitive blob without resolving or loading BepInEx/game code.
                    $blob=$attribute.GetBlob()
                    if($attribute.Constructor.Parameters.Count -eq 2 -and $attribute.Constructor.Parameters[1].ParameterType.FullName -ceq 'BepInEx.BepInDependency/DependencyFlags' -and $blob.Length -ge 9 -and $blob[0] -eq 1 -and $blob[1] -eq 0 -and $blob[2] -lt 128){
                        $count=[int]$blob[2]
                        if($blob.Length -ge 3+$count+6){$dependency=[pscustomobject]@{Guid=[Text.Encoding]::UTF8.GetString($blob,3,$count);Flags=[BitConverter]::ToInt32($blob,3+$count)}}
                    }
                }}}
            }
            return [pscustomobject]@{Assembly=$assembly.Name.Name;Version=$assembly.Name.Version.ToString();Plugins=$plugins;Dependency=$dependency;References=@($assembly.MainModule.AssemblyReferences.Name)}
        }finally{$assembly.Dispose()}
    }finally{$memory.Dispose()}
}
function Assert-CataloguePackage {
    param([object[]]$Entries,$Expected,$Declaration)
    $allowed=@($Expected.Files)+@($Expected.Script)
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    if($Entries.Count -ne $allowed.Count){throw 'Thin package has missing or extra entries.'}
    $metadata=@{}
    foreach($entry in $Entries){
        if($allowed -cnotcontains $entry.Name -or -not $seen.Add($entry.Name)){throw 'Thin package contains unexpected/nested/duplicate entries.'}
        if($entry.Bytes.Length -eq 0){throw 'Thin package contains an empty entry.'}
        if($entry.Name.EndsWith('.dll',[StringComparison]::OrdinalIgnoreCase)){
            $dll=Read-CatalogueDll $entry.Bytes $entry.Name
            if($dll.Assembly -cne [IO.Path]::GetFileNameWithoutExtension($entry.Name)){throw 'DLL filename and actual assembly identity differ.'}
            if($entry.Name -ceq 'ScamWYF.Modding.Core.dll'){
                if($dll.Plugins.Count -ne 0 -or $dll.Version -cne '1.0.4.0'){throw 'Shared Core must be version 1.0.4 without a BepInPlugin attribute.'}
            }else{
                $identity=$catalogueAuditPluginIdentity[$entry.Name]
                if($dll.Plugins.Count -ne 1 -or $dll.Plugins[0].Guid -cne $identity[0] -or $dll.Plugins[0].Name -cne $identity[1] -or $dll.Plugins[0].Version -cne $identity[2]){throw 'Plugin loader identity/version does not match the prepared mod.'}
                $parsedVersion=$null
                if(-not [Version]::TryParse($dll.Plugins[0].Version,[ref]$parsedVersion)){throw 'BepInEx would reject the plugin version.'}
                if($dll.References -cnotcontains 'ScamWYF.Modding.Core'){throw 'Expected shared Core dependency is absent from the binary.'}
            }
            $metadata[$entry.Name]=$dll
        }else{
            if($entry.Bytes.Length -gt 65536){throw 'Optional setup script exceeds the stock launcher 64 KiB limit.'}
            $text=[Text.UTF8Encoding]::new($false,$true).GetString($entry.Bytes)
            if([string]::IsNullOrWhiteSpace($text)){throw 'Optional setup script is empty.'}
            $tokens=$null;$errors=$null
            $ast=[Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors)
            if($errors.Count -ne 0){throw 'Optional setup script contains PowerShell syntax errors.'}
            if(@($ast.ParamBlock.Parameters|Where-Object {$_.Name.VariablePath.UserPath -ceq 'GameDir'}).Count -ne 1){throw 'Setup script must accept the standard launcher -GameDir parameter.'}
            if($text -notmatch 'Permission\s+is\s+hereby\s+granted' -or $text -notmatch 'THE SOFTWARE IS PROVIDED'){throw 'DLL-only distribution setup script must carry the MIT notices.'}
            if($Expected.Repo -ceq 'ElevenLabs-Agents'){
                if($text -notmatch 'https://github\.com/h7sd/scam-with-your-friends-mods/releases/download/catalogue-runtime-v1\.0\.1/' -or $text -notmatch '\b[0-9a-fA-F]{64}\b' -or $text -match '/releases/latest/' -or $text -match '__[A-Z0-9_]+__'){throw 'Runtime setup requires a fixed companion URL and a stamped SHA-256 digest.'}
            }
            if($catalogueAuditExpected.Repo -ccontains $Expected.Repo){
                if($text -notmatch 'https://github\.com/h7sd/scam-with-your-friends-mods/releases/download/catalogue-dependencies-v1\.0\.0/' -or $text -match '__[A-Z0-9_]+__'){throw 'Active setup must use fixed, stamped separate dependency packages.'}
                # The shared embedded helper carries both definitions, including the
                # unused AI Backend definition in Requested Payout. Pin every copy so
                # a mixed-generation helper/package set cannot pass validation.
                $neededDependencies=@('ScamWYF.Modding.Core.dll','ScamWYF.AiBackend.dll')
                foreach($needed in $neededDependencies){
                    $pin=$catalogueAuditDependencyPins[$needed]
                    if($null -eq $pin){throw 'Separate dependency package pins must be validated before the active script.'}
                    $pattern="(?s)Archive\s*=\s*'"+[Regex]::Escape($pin.Archive)+"'[^}]*Relative\s*=\s*'"+[Regex]::Escape($pin.Relative)+"'[^}]*ArchiveSha256\s*=\s*'"+$pin.ArchiveSha256+"'[^}]*DllSha256\s*=\s*'"+$pin.DllSha256+"'"
                    if($text -notmatch $pattern){throw 'Embedded dependency archive/DLL hashes or install routing differ from the separate package.'}
                }
            }
        }
    }
    if($Expected.Repo -ceq 'ElevenLabs-Agents'){
        $agents=$metadata['ScamWYF.ElevenLabsAgents.dll'];$backend=$catalogueAuditDependencies['ScamWYF.AiBackend.dll']
        if($null -eq $backend -or $null -eq $catalogueAuditDependencies['ScamWYF.Modding.Core.dll']){throw 'Validate the separate Core and AI Backend packages before the active plugin.'}
        if($agents.References -cnotcontains 'ScamWYF.AiBackend' -or $null -eq $agents.Dependency -or $agents.Dependency.Guid -cne $backend.Plugins[0].Guid -or ($agents.Dependency.Flags -band 1) -ne 1){throw 'ElevenLabs Agents requires its actual hard-dependent plugin in the separately validated AI Backend package.'}
    }
    return $metadata
}
function Assert-CatalogueRejected {
    param([scriptblock]$Action,[string]$Name)
    $rejected=$false
    try{& $Action|Out-Null}catch{$rejected=$true}
    Assert-CatalogueCondition $rejected ('Negative case was accepted: '+$Name)
    $catalogueAuditResults.NegativeCases++
}
function Copy-CatalogueJson {
    param($Value)
    return ($Value|ConvertTo-Json -Depth 12|ConvertFrom-Json)
}
function Edit-CataloguePluginMetadata {
    param([byte[]]$Bytes,[bool]$RemoveAttribute)
    $metadataInput=[IO.MemoryStream]::new($Bytes,$false);$metadataOutput=[IO.MemoryStream]::new()
    try{
        $assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($metadataInput)
        try{
            foreach($type in (Get-CatalogueAllTypes $assembly.MainModule.Types)){
                foreach($attribute in @($type.CustomAttributes|Where-Object {$_.AttributeType.FullName -ceq 'BepInEx.BepInPlugin'})){
                    if($RemoveAttribute){$null=$type.CustomAttributes.Remove($attribute)}
                    else{$attribute.ConstructorArguments[2]=[Mono.Cecil.CustomAttributeArgument]::new($assembly.MainModule.TypeSystem.String,'1.2.0+invalid')}
                }
            }
            $assembly.Write($metadataOutput);return ,$metadataOutput.ToArray()
        }finally{$assembly.Dispose()}
    }finally{$metadataOutput.Dispose();$metadataInput.Dispose()}
}
function Test-CatalogueArchive {
    param($Spec,$Declaration)
    $entries=Get-CatalogueZipEntries (Join-Path $PackageDirectory $Spec.Zip)
    $metadata=Assert-CataloguePackage $entries $Spec $Declaration
    foreach($dllName in $Spec.Files){
        $dll=$metadata[$dllName]
        $packedBytes=($entries|Where-Object {$_.Name -ceq $dllName}).Bytes
        $hasher=[Security.Cryptography.SHA256]::Create()
        try{$packedHash=[BitConverter]::ToString($hasher.ComputeHash([byte[]]$packedBytes)).Replace('-','')}
        finally{$hasher.Dispose()}
        $sourceHash=(Get-FileHash -LiteralPath (Join-Path $catalogueAuditRoot $catalogueAuditCompiledSources[$dllName]) -Algorithm SHA256).Hash
        Assert-CatalogueCondition ($packedHash -ceq $sourceHash) ('Packed DLL differs from its actual compiled source: '+$dllName)
        $catalogueAuditVersions.Add([pscustomobject]@{Package=$Spec.Repo;File=$dllName;Version=$(if($dll.Plugins.Count){$dll.Plugins[0].Version}else{$dll.Version})})
    }
    Assert-CatalogueCondition $true ('Real thin ZIP layout/metadata/script checks: '+$Spec.Repo)
    return [pscustomobject]@{Entries=$entries;Metadata=$metadata}
}
$catalogueAuditDocument=[IO.File]::ReadAllText([IO.Path]::GetFullPath($CataloguePath))|ConvertFrom-Json
Assert-CatalogueDocument $catalogueAuditDocument
Assert-CatalogueCondition $true 'Prepared catalogue schema/identities/routes/scripts'
$catalogueAuditPackages=@{}
$catalogueAuditVersions=[Collections.Generic.List[object]]::new()
foreach($catalogueAuditDependency in $catalogueAuditDependencyExpected){
    $catalogueAuditDependencyResult=Test-CatalogueArchive $catalogueAuditDependency $null
    foreach($catalogueAuditDependencyDll in $catalogueAuditDependency.Files){
        $catalogueAuditDependencies[$catalogueAuditDependencyDll]=$catalogueAuditDependencyResult.Metadata[$catalogueAuditDependencyDll]
        $catalogueAuditDependencyPins[$catalogueAuditDependencyDll]=[pscustomobject]@{
            Archive=$catalogueAuditDependency.Zip
            Relative=$(if($catalogueAuditDependencyDll -ceq 'ScamWYF.Modding.Core.dll'){'BepInEx/core/'}else{'BepInEx/plugins/'})+$catalogueAuditDependencyDll
            ArchiveSha256=(Get-FileHash -LiteralPath (Join-Path $PackageDirectory $catalogueAuditDependency.Zip)).Hash.ToLowerInvariant()
            DllSha256=(Get-FileHash -LiteralPath (Join-Path $catalogueAuditRoot $catalogueAuditCompiledSources[$catalogueAuditDependencyDll])).Hash.ToLowerInvariant()
        }
    }
}
for($catalogueAuditIndex=0;$catalogueAuditIndex -lt $catalogueAuditExpected.Count;$catalogueAuditIndex++){
    $catalogueAuditSpec=$catalogueAuditExpected[$catalogueAuditIndex]
    $catalogueAuditArchiveResult=Test-CatalogueArchive $catalogueAuditSpec $catalogueAuditDocument.mods[$catalogueAuditIndex]
    $catalogueAuditPackages[$catalogueAuditSpec.Repo]=$catalogueAuditArchiveResult.Entries
}
foreach($mutate in @(
    {param($d)$d.schemaVersion='1'},
    {param($d)$d.mods[0].files[0].folder='core'},
    {param($d)$d.mods[0].files[0].expectsPluginAttribute=$false},
    {param($d)$d.mods[0].files=@($d.mods[0].files)+@([pscustomobject]@{fileName='ScamWYF.Modding.Core.dll';folder='core';expectsPluginAttribute=$false})},
    {param($d)$d.mods[1].postInstall[0].script='nested/setup.ps1'},
    {param($d)$d.mods[1].summary=('x'*501)},
    {param($d)$d|Add-Member -NotePropertyName unexpected -NotePropertyValue $true},
    {param($d)$d.mods[1].repo=$d.mods[0].repo},
    {param($d)$d.mods[1].repo='TikTok-Pocket'},
    {param($d)$inactive=Copy-CatalogueJson $d.mods[1];$inactive.repo='TikTok-Pocket';$inactive.displayName='TikTok Pocket';$d.mods=@($d.mods)+@($inactive)}
)){
    $catalogueAuditBadJson=Copy-CatalogueJson $catalogueAuditDocument
    & $mutate $catalogueAuditBadJson
    Assert-CatalogueRejected {Assert-CatalogueDocument $catalogueAuditBadJson} 'Invalid schema/route/script/identity JSON'
}
$catalogueAuditPayout=$catalogueAuditExpected[1]
$catalogueAuditValidPayout=$catalogueAuditPackages[$catalogueAuditPayout.Repo]
$catalogueAuditPayoutDeclaration=$catalogueAuditDocument.mods[1]
Assert-CatalogueRejected {Assert-CataloguePackage @($catalogueAuditValidPayout|Where-Object {$_.Name -cne 'ScamWYF.RequestedPayout.dll'}) $catalogueAuditPayout $catalogueAuditPayoutDeclaration} 'Missing plugin DLL'
Assert-CatalogueRejected {Assert-CataloguePackage @($catalogueAuditValidPayout|Where-Object {$_.Name -cne $catalogueAuditPayout.Script}) $catalogueAuditPayout $catalogueAuditPayoutDeclaration} 'Missing declared setup script'
foreach($extraName in @('bridge/.env','Profile/Cookies','README.md','nested/ScamWYF.RequestedPayout.dll','extra.dll')){
    Assert-CatalogueRejected {Assert-CataloguePackage (@($catalogueAuditValidPayout)+@([pscustomobject]@{Name=$extraName;Bytes=[byte[]]@(1)})) $catalogueAuditPayout $catalogueAuditPayoutDeclaration} 'Unexpected/private/nested entry'
}
# Archived TikTok ZIPs may remain beside the active packages. They must not enter
# either active mod's declared contents or reappear in the active catalogue.
for($catalogueAuditInactiveIndex=0;$catalogueAuditInactiveIndex -lt $catalogueAuditExpected.Count;$catalogueAuditInactiveIndex++){
    $catalogueAuditActiveSpec=$catalogueAuditExpected[$catalogueAuditInactiveIndex]
    $catalogueAuditActiveEntries=$catalogueAuditPackages[$catalogueAuditActiveSpec.Repo]
    $catalogueAuditActiveDeclaration=$catalogueAuditDocument.mods[$catalogueAuditInactiveIndex]
    $catalogueAuditOtherPlugin=if($catalogueAuditActiveSpec.Repo -ceq 'ElevenLabs-Agents'){'ScamWYF.RequestedPayout.dll'}else{'ScamWYF.ElevenLabsAgents.dll'}
    foreach($catalogueAuditInactiveName in @('ScamWYF.TikTokPocket.dll','TikTok-Pocket-1.0.1.zip','ScamWYF.Modding.Core.dll','ScamWYF.AiBackend.dll',$catalogueAuditOtherPlugin)){
        Assert-CatalogueRejected {Assert-CataloguePackage (@($catalogueAuditActiveEntries)+@([pscustomobject]@{Name=$catalogueAuditInactiveName;Bytes=[byte[]]@(1)})) $catalogueAuditActiveSpec $catalogueAuditActiveDeclaration} ('Foreign mod/dependency content inside '+$catalogueAuditActiveSpec.Repo)
    }
}
foreach($badKind in @('duplicate','unreadable','attribute','version','oversize-script','bad-parameter','wrong-dependency-pin','wrong-unused-ai-pin')){
    $catalogueAuditBadEntries=@(foreach($entry in $catalogueAuditValidPayout){[pscustomobject]@{Name=$entry.Name;Bytes=$entry.Bytes}})
    $catalogueAuditBadPlugin=($catalogueAuditBadEntries|Where-Object {$_.Name -ceq 'ScamWYF.RequestedPayout.dll'})
    switch($badKind){
        'duplicate'{$catalogueAuditBadEntries[0].Name=$catalogueAuditBadEntries[1].Name}
        'unreadable'{$catalogueAuditBadPlugin.Bytes=[byte[]]@(1,2,3)}
        'attribute'{$catalogueAuditBadPlugin.Bytes=Edit-CataloguePluginMetadata $catalogueAuditBadPlugin.Bytes $true}
        'version'{$catalogueAuditBadPlugin.Bytes=Edit-CataloguePluginMetadata $catalogueAuditBadPlugin.Bytes $false}
        'oversize-script'{($catalogueAuditBadEntries|Where-Object {$_.Name -ceq $catalogueAuditPayout.Script}).Bytes=[Text.Encoding]::UTF8.GetBytes(('x'*65537))}
        'bad-parameter'{($catalogueAuditBadEntries|Where-Object {$_.Name -ceq $catalogueAuditPayout.Script}).Bytes=[Text.Encoding]::UTF8.GetBytes('param([string]$Other)')}
        'wrong-dependency-pin'{
            $catalogueAuditBadScript=$catalogueAuditBadEntries|Where-Object {$_.Name -ceq $catalogueAuditPayout.Script}
            $catalogueAuditBadText=[Text.Encoding]::UTF8.GetString($catalogueAuditBadScript.Bytes).Replace($catalogueAuditDependencyPins['ScamWYF.Modding.Core.dll'].ArchiveSha256,('0'*64))
            $catalogueAuditBadScript.Bytes=[Text.Encoding]::UTF8.GetBytes($catalogueAuditBadText)
        }
        'wrong-unused-ai-pin'{
            $catalogueAuditBadScript=$catalogueAuditBadEntries|Where-Object {$_.Name -ceq $catalogueAuditPayout.Script}
            $catalogueAuditBadText=[Text.Encoding]::UTF8.GetString($catalogueAuditBadScript.Bytes).Replace($catalogueAuditDependencyPins['ScamWYF.AiBackend.dll'].DllSha256,('0'*64))
            $catalogueAuditBadScript.Bytes=[Text.Encoding]::UTF8.GetBytes($catalogueAuditBadText)
        }
    }
    Assert-CatalogueRejected {Assert-CataloguePackage $catalogueAuditBadEntries $catalogueAuditPayout $catalogueAuditPayoutDeclaration} ('Invalid actual metadata or script: '+$badKind)
}
$catalogueAuditVersions|Format-Table -AutoSize
Write-Output ('Catalogue package checks passed: '+$catalogueAuditResults.Passed+' checks, including '+$catalogueAuditResults.NegativeCases+' rejection cases. No plugins/setup scripts ran and no game files were read or installed.')
