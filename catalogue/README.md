# Stock launcher packages

The compiled files are also committed directly in [ElevenLabs Agents](../mods/ElevenLabs-Agents), [Requested Payout](../mods/Requested-Payout), and [dependency folders](../mods/Dependencies). Their DLLs and stamped setup scripts match the published release ZIPs byte for byte.

Each mod has its own public release repository and exactly one mod ZIP in its latest release:

- [ElevenLabs Agents](https://github.com/h7sd/ElevenLabs-Agents/releases/latest): `ElevenLabs-Agents-1.0.2.zip`
- [Requested Payout](https://github.com/h7sd/Requested-Payout/releases/latest): `Requested-Payout-1.2.1.zip`

Each ZIP contains exactly its own root-level mod DLL and one root-level post-install PowerShell script:

- ElevenLabs: `ScamWYF.ElevenLabsAgents.dll` and `setup-elevenlabs-agents.ps1`.
- Requested Payout: `ScamWYF.RequestedPayout.dll` and `post-install-requested-payout.ps1`.

No other mod DLLs, folders, launcher or runtime files are included. Package releases 1.0.2 and 1.2.1 change distribution only; the mod DLL versions remain 1.0.1 and 1.2.0. Licence notices are in the script comments.

The shared Core and AI Backend are separate packages in the source repository's [`catalogue-dependencies-v1.0.0` prerelease](https://github.com/h7sd/scam-with-your-friends-mods/releases/tag/catalogue-dependencies-v1.0.0): `Modding-Core-1.0.4.zip` and `AI-Backend-1.1.0.zip`. Each also contains only its own DLL and a licence/verification script. Active mod setup downloads these packages using fixed URLs and SHA-256 checks, installing Core under `BepInEx/core` and AI Backend under `BepInEx/plugins`. It retains compatible installed dependencies and does not change their configuration.

[`mods.json`](mods.json) contains the two entries in the format used by [swyf-modding/ModCatalogue](https://github.com/swyf-modding/ModCatalogue). The entries are ready to be added to that catalogue; this file does not itself change the upstream catalogue.

The stock launcher chooses the first ZIP from each repository's latest release and caps the download at 32 MiB. Separate repositories prevent it from choosing a different mod. The bridge runtime ZIP is hosted in the source repository's separate `catalogue-runtime-v1.0.1` prerelease and is downloaded only by the optional setup script using a fixed URL and SHA-256 checks.

Post-install scripts accept `-GameDir` and are individually offered by the stock launcher. **Accept setup on a fresh installation:** Requested Payout requires Core 1.0.4 or newer; ElevenLabs Agents requires Core, AI Backend 1.1.0, its local bridge, Node.js 22.13 or newer and the user's own account configuration. Skipping setup requires these dependencies to be installed manually first. Setup preserves local configuration.

Installing a ZIP through the launcher's direct-URL field does not offer catalogue post-install steps. After such an installation, extract the same ZIP and run its setup script yourself, with the game closed:

```powershell
.\setup-elevenlabs-agents.ps1 -GameDir 'C:\Path\To\Game'
# Or for Requested Payout:
.\post-install-requested-payout.ps1 -GameDir 'C:\Path\To\Game'
```

Build both stock packages from this source checkout:

```powershell
.\scripts\Package-CatalogueMods.ps1
.\scripts\Test-CataloguePackages.ps1
```

Outputs are under `build/catalogue-packages`. Build the two mod DLLs and install the bridge's npm dependencies before packaging. `Package-CatalogueDependencies.ps1` builds the separate dependency archives and the mod packager embeds their exact hashes in setup. Rebuilding a companion ZIP requires publishing that exact ZIP together with the newly stamped setup script; setup deliberately does not download an unpinned `latest` runtime. Use `-UseExistingRuntime` when reusing the already published bridge archive and digest file. For offline setup, pass `-DependencyDirectory` pointing to the two published dependency ZIPs; ElevenLabs also accepts `-RuntimeArchive` for its bridge ZIP and `-NoStart`.
