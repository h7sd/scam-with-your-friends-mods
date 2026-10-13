# Stock launcher packages

Each mod has its own public release repository and exactly one mod ZIP in its latest release:

- [ElevenLabs Agents](https://github.com/h7sd/ElevenLabs-Agents/releases/latest): `ElevenLabs-Agents-1.0.1.zip`
- [Requested Payout](https://github.com/h7sd/Requested-Payout/releases/latest): `Requested-Payout-1.2.0.zip`
- [TikTok Pocket](https://github.com/h7sd/TikTok-Pocket/releases/latest): `TikTok-Pocket-1.0.1.zip`

These ZIPs contain only root-level DLLs and their optional root-level post-install PowerShell script. The shared `ScamWYF.Modding.Core.dll` is declared as `core`, without a plugin attribute. ElevenLabs Agents also includes its required AI Backend plugin DLL. Licence notices for the included DLLs are in the script comments.

[`mods.json`](mods.json) contains the three entries in the format used by [swyf-modding/ModCatalogue](https://github.com/swyf-modding/ModCatalogue). The entries are ready to be added to that catalogue; this file does not itself change the upstream catalogue.

The stock launcher chooses the first ZIP from each repository's latest release and caps the download at 32 MiB. Separate repositories prevent it from choosing a different mod or the larger TikTok runtime. The bridge and browser runtime ZIPs are hosted in the source repository's separate `catalogue-runtime-v1.0.1` prerelease, and are downloaded only by the optional setup scripts using fixed URLs and SHA-256 checks.

Post-install scripts accept `-GameDir` and are individually offered by the stock launcher. Skipping Requested Payout's usage check has no effect. ElevenLabs Agents needs its local bridge, Node.js 22.13 or newer, and the user's own account configuration. TikTok Pocket needs its icon/browser files and an installed Microsoft Edge WebView2 Runtime. The scripts preserve local configuration and browser profiles.

Installing a ZIP through the launcher's direct-URL field does not offer catalogue post-install steps. After such an installation, extract the same ZIP and run its setup script yourself, with the game closed:

```powershell
.\setup-elevenlabs-agents.ps1 -GameDir 'C:\Path\To\Game'
# Or, for TikTok Pocket:
.\setup-tiktok-pocket.ps1 -GameDir 'C:\Path\To\Game'
```

Build all three stock packages from this source checkout:

```powershell
.\scripts\Package-CatalogueMods.ps1
.\scripts\Test-CataloguePackages.ps1
```

Outputs are under `build/catalogue-packages`. Build the mod DLLs and browser worker, and install the bridge's npm dependencies before packaging. Rebuilding a companion ZIP requires publishing that exact ZIP together with the newly stamped setup script; the scripts deliberately do not download an unpinned `latest` runtime.
