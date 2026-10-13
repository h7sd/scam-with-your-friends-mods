// Publish audited, flat per-mod ZIPs using the owner's existing GitHub credential.
// Credentials stay in memory; existing release assets are verified, never replaced.
const fs = require('node:fs');
const path = require('node:path');
const cp = require('node:child_process');
const crypto = require('node:crypto');
const zlib = require('node:zlib');
const root = path.resolve(__dirname, '..');
const out = path.join(root, 'build/catalogue-packages');
const git = process.env.SCAMWYF_GIT || 'git';
const owner = 'h7sd';
const sourceRepo = 'scam-with-your-friends-mods';
const digest = bytes => 'sha256:' + crypto.createHash('sha256').update(bytes).digest('hex');
const specs = [
  {repo:'ElevenLabs-Agents', version:'1.0.2', dllVersion:'1.0.1', title:'ElevenLabs Agents', folder:'mod', dll:'ScamWYF.ElevenLabsAgents.dll', script:'setup-elevenlabs-agents.ps1', note:'Setup installs the required shared Core, AI Backend 1.1.0 and the local ElevenLabs bridge. Node.js 22.13 or newer and your own ElevenLabs configuration are required. Skipping setup requires these dependencies and a running bridge to be installed manually.'},
  {repo:'Requested-Payout', version:'1.2.1', dllVersion:'1.2.0', title:'Requested Payout', folder:'payout-mod', dll:'ScamWYF.RequestedPayout.dll', script:'post-install-requested-payout.ps1', note:'Setup installs the required shared Core 1.0.4 when missing. Skipping setup requires Core 1.0.4 or newer already installed in BepInEx/core. Credit-card payouts add your stated price to the original reward; gift-card payouts use your stated price after correct fictional-code verification. Install on the multiplayer host.'}
];
const dependencyFiles = ['Modding-Core-1.0.4.zip', 'AI-Backend-1.1.0.zip'];
function assertZip(filename, expected) {
  const bytes = fs.readFileSync(path.join(out, filename));
  if (bytes.length > 33554432) throw new Error('Oversized stock package: ' + filename);
  let eocd = bytes.length - 22;
  while (eocd >= Math.max(0, bytes.length - 65557) && bytes.readUInt32LE(eocd) !== 0x06054b50) eocd--;
  if (eocd < 0) throw new Error('Invalid ZIP: ' + filename);
  const count = bytes.readUInt16LE(eocd + 10);
  let offset = bytes.readUInt32LE(eocd + 16);
  const names = [];
  const scripts = {};
  for (let i = 0; i < count; i++) {
    if (bytes.readUInt32LE(offset) !== 0x02014b50) throw new Error('Invalid ZIP inventory.');
    const length = bytes.readUInt16LE(offset + 28);
    const name = bytes.subarray(offset + 46, offset + 46 + length).toString('utf8');
    names.push(name);
    if (name.endsWith('.ps1')) {
      const size = bytes.readUInt32LE(offset + 24);
      const compressed = bytes.readUInt32LE(offset + 20);
      const local = bytes.readUInt32LE(offset + 42);
      if (size > 65536 || compressed > 65536 || bytes.readUInt32LE(local) !== 0x04034b50) throw new Error('Invalid setup entry.');
      const start = local + 30 + bytes.readUInt16LE(local + 26) + bytes.readUInt16LE(local + 28);
      const method = bytes.readUInt16LE(offset + 10);
      const body = method === 8 ? zlib.inflateRawSync(bytes.subarray(start,start+compressed),{maxOutputLength:65536}) : method === 0 ? bytes.subarray(start,start+compressed) : null;
      if (!body || body.length !== size) throw new Error('Invalid setup contents.');
      scripts[name] = body.toString('utf8');
    }
    offset += 46 + length + bytes.readUInt16LE(offset + 30) + bytes.readUInt16LE(offset + 32);
  }
  if (names.length !== expected.length || new Set(names).size !== expected.length || names.some(name => !expected.includes(name))) throw new Error('Unexpected ZIP inventory: ' + filename);
  return scripts;
}
async function main() {
  const revision = cp.spawnSync(git, ['rev-parse','HEAD'], {cwd:root, encoding:'utf8', windowsHide:true});
  const clean = cp.spawnSync(git, ['status','--porcelain'], {cwd:root, encoding:'utf8', windowsHide:true});
  if (revision.status !== 0 || clean.status !== 0 || clean.stdout.trim()) throw new Error('Commit audited source before publication.');
  const source = revision.stdout.trim();
  const catalogue = JSON.parse(fs.readFileSync(path.join(root,'catalogue/mods.json'), 'utf8'));
  const dependencyDigests = JSON.parse(fs.readFileSync(path.join(out,'dependency-digests.json'),'utf8'));
  for (const [filename,key] of [[dependencyFiles[0],'core'],[dependencyFiles[1],'aiBackend']]) {
    if (digest(fs.readFileSync(path.join(out,filename))).slice(7) !== dependencyDigests[key+'ArchiveSha256']) throw new Error('Dependency digest file is stale.');
  }
  for (const spec of specs) {
    spec.archive = spec.repo + '-' + spec.version + '.zip';
    const scripts = assertZip(spec.archive, [spec.dll, spec.script]);
    for (const [name,key] of [['Core','core'],['AiBackend','aiBackend']]) {
      const definition = scripts[spec.script].match(new RegExp(name+'=@\\{[^\\r\\n]+\\}'));
      if (!definition || !definition[0].includes("ArchiveSha256='"+dependencyDigests[key+'ArchiveSha256']+"'") || !definition[0].includes("DllSha256='"+dependencyDigests[key+'DllSha256']+"'")) throw new Error('Setup pins differ from dependency packages: '+spec.archive);
    }
    const entry = catalogue.mods.find(item => item.repo === spec.repo);
    if (!entry || entry.files.length !== 1 || entry.files[0].fileName !== spec.dll || entry.postInstall[0].script !== spec.script) throw new Error('Catalogue and package disagree.');
  }
  assertZip(dependencyFiles[0], ['ScamWYF.Modding.Core.dll','post-install-modding-core.ps1']);
  assertZip(dependencyFiles[1], ['ScamWYF.AiBackend.dll','post-install-ai-backend.ps1']);
  const auth = cp.spawnSync(git, ['credential','fill'], {cwd:root, encoding:'utf8', windowsHide:true, input:'protocol=https\nhost=github.com\npath='+owner+'/'+sourceRepo+'.git\n\n', env:{...process.env,GIT_TERMINAL_PROMPT:'0',GCM_INTERACTIVE:'never'}});
  if (auth.status !== 0) throw new Error('Existing GitHub authentication is unavailable.');
  const credential = Object.fromEntries(auth.stdout.trim().split(/\r?\n/).map(line => {const i=line.indexOf('=');return [line.slice(0,i),line.slice(i+1)];}));
  if (!credential.password) throw new Error('No existing GitHub credential.');
  const headers = {Authorization:'Bearer '+credential.password, Accept:'application/vnd.github+json', 'X-GitHub-Api-Version':'2026-03-10', 'User-Agent':'SWYF-Individual-Mod-Packages'};
  async function request(url, options={}, optional=false) {
    const target = new URL(url);
    if (target.protocol !== 'https:' || !['api.github.com','uploads.github.com'].includes(target.hostname)) throw new Error('Unexpected API host.');
    const response = await fetch(url, {...options, headers:{...headers,...options.headers}, signal:AbortSignal.timeout(120000)});
    if (optional && response.status === 404) return null;
    if (!response.ok) throw new Error('GitHub HTTP '+response.status+' '+target.pathname);
    return response.json();
  }
  const account = await request('https://api.github.com/user');
  if (account.login.toLowerCase() !== owner) throw new Error('Signed-in account differs from the authorized owner.');
  const results = [];
  async function release(repo, tag, title, body, files, prerelease, targetCommit) {
    const api = 'https://api.github.com/repos/'+owner+'/'+repo;
    let item = await request(api+'/releases/tags/'+tag, {}, true);
    if (!item) item = await request(api+'/releases', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({tag_name:tag,target_commitish:targetCommit,name:title,body,draft:true,prerelease})});
    if (item.prerelease !== prerelease) throw new Error('Existing release has a different publication status.');
    const existing = await request(item.assets_url);
    for (const filename of files) {
      const bytes = fs.readFileSync(path.join(out,filename));
      let asset = existing.find(value => value.name === filename);
      if (!asset) {
        const upload = new URL(item.upload_url.replace(/\{.*$/,''));upload.searchParams.set('name',filename);
        asset = await request(upload.toString(), {method:'POST',headers:{'Content-Type':filename.endsWith('.zip')?'application/zip':'text/plain','Content-Length':String(bytes.length)},body:bytes});
      }
      if (asset.state !== 'uploaded' || asset.size !== bytes.length || asset.digest !== digest(bytes)) throw new Error('Remote asset differs: '+filename);
      console.log('Verified:', repo, filename, bytes.length);
    }
    if (existing.some(asset => !files.includes(asset.name))) throw new Error('Release has an unexpected asset.');
    if (item.draft) await request(api+'/releases/'+item.id, {method:'PATCH',headers:{'Content-Type':'application/json'},body:JSON.stringify({draft:false,make_latest:prerelease?'false':'true'})});
    const verified = await request(api+'/releases/tags/'+tag);
    const actualZips = verified.assets.filter(asset => asset.name.endsWith('.zip'));
    if (verified.draft || actualZips.length !== files.filter(file=>file.endsWith('.zip')).length) throw new Error('Published release inventory differs.');
    results.push({repo,tag,url:verified.html_url,assets:verified.assets.map(asset=>({name:asset.name,size:asset.size,digest:asset.digest,url:asset.browser_download_url}))});
  }
  for (const spec of specs) fs.writeFileSync(path.join(out,spec.repo+'-SHA256SUMS.txt'), digest(fs.readFileSync(path.join(out,spec.archive))).slice(7)+'  '+spec.archive+'\n');
  fs.writeFileSync(path.join(out,'DEPENDENCIES-SHA256SUMS.txt'), dependencyFiles.map(file=>digest(fs.readFileSync(path.join(out,file))).slice(7)+'  '+file).join('\n')+'\n');
  await release(sourceRepo,'catalogue-dependencies-v1.0.0','Separate shared dependencies for individual mod packages','Pinned dependencies downloaded by the active mods\' approved post-install scripts. Each ZIP contains exactly one dependency DLL and one licence/verification script. Core installs to BepInEx/core; AI Backend installs to BepInEx/plugins. These companion assets are separate from releases/latest. Source: '+source,[...dependencyFiles,'DEPENDENCIES-SHA256SUMS.txt'],true,source);
  for (const spec of specs) {
    const api = 'https://api.github.com/repos/'+owner+'/'+spec.repo;
    const repository = await request(api);
    const marker = 'Stock-launcher distribution for '+spec.title+'; source: '+owner+'/'+sourceRepo;
    if (repository.private || repository.owner.login.toLowerCase() !== owner || repository.description !== marker) throw new Error('Unexpected distribution repository.');
    const entry = catalogue.mods.find(item=>item.repo===spec.repo);
    const readme = '# '+spec.title+' for Scam With Your Friends\n\n'+spec.note+'\n\nDownload **'+spec.archive+'** from [Releases](https://github.com/'+owner+'/'+spec.repo+'/releases/latest). The ZIP has exactly two root files:\n\n- `'+spec.dll+'`\n- `'+spec.script+'`\n\nPackage '+spec.version+' changes distribution only; the DLL version remains '+spec.dllVersion+'. There is no launcher, dependency DLL or runtime inside this mod ZIP. Required dependencies are downloaded separately by setup using fixed HTTPS URLs and SHA-256 checks. Licence notices are included in the script.\n\nThe stock launcher offers setup only for catalogue installations. If using a direct ZIP URL, extract it and run `'+spec.script+' -GameDir "C:\\Path\\To\\Game"` with the game closed.\n\n[Source](https://github.com/'+owner+'/'+sourceRepo+'/tree/'+source+'/'+spec.folder+') · [Package and dependency instructions](https://github.com/'+owner+'/'+sourceRepo+'/blob/'+source+'/catalogue/README.md)\n\nCatalogue entry:\n\n```json\n'+JSON.stringify(entry,null,2)+'\n```\n';
    const current = await request(api+'/contents/README.md');
    let branchCommit;
    const desired = Buffer.from(readme,'utf8');
    if (Buffer.from(current.content.replace(/\s/g,''),'base64').equals(desired)) {
      branchCommit = (await request(api+'/branches/'+repository.default_branch)).commit.sha;
    } else {
      const update = await request(api+'/contents/README.md',{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({message:'Document individual DLL package '+spec.version,content:desired.toString('base64'),sha:current.sha})});
      branchCommit = update.commit.sha;
    }
    const body = '**Each mod is now separate.** This ZIP contains exactly `'+spec.dll+'` and `'+spec.script+'` at the root. No other DLLs, folders or launcher.\n\n'+spec.note+'\n\nPackage '+spec.version+' retains DLL version '+spec.dllVersion+'. Dependencies are separate pinned downloads. [Source and setup instructions](https://github.com/'+owner+'/'+sourceRepo+'/blob/'+source+'/catalogue/README.md). Package inventories, unchanged binary hashes and fake-game setup were verified.';
    await release(spec.repo,'v'+spec.version,spec.title+' '+spec.version+' – individual DLL package',body,[spec.archive,spec.repo+'-SHA256SUMS.txt'],false,branchCommit);
    if ((await request(api+'/releases/latest')).tag_name !== 'v'+spec.version) throw new Error('Unexpected latest release.');
  }
  fs.writeFileSync(path.join(out,'individual-publication-result.json'),JSON.stringify({source,results},null,2));
  for (const item of results) console.log('Published:',item.url);
}
main().catch(error=>{console.error(error.message);process.exitCode=1;});
