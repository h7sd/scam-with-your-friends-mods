# Community Mods for Scam With Your Friends

This repository includes **ElevenLabs Agents 1.0.1**, **Wunschsumme 1.1.0** for credit card and gift card scams, and a customized Windows launcher with entries for both mods. Ready-to-use packages are available under [Releases](https://github.com/h7sd/scam-with-your-friends-mods/releases).

For the complete setup, download and extract `SWYF-Mods-1.1.0.zip`. The smaller `Wunschsumme-SWYF-1.1.0.zip` package is also available for an existing game installation that already has BepInEx set up.

## ElevenLabs Agents

A local BepInEx mod with a customized launcher. Speech recognition, conversation responses, and voice output use **ElevenLabs Agents**. Cursor is not used during gameplay.

## Getting started

The local setup page currently uses German labels; the corresponding labels are included below.

1. Open `Start-ElevenLabs.cmd` at the root of the extracted package. When building from source, the file is under `dist`. The local setup page opens at `http://127.0.0.1:8765`.
2. Enter your ElevenLabs API key and preferred voice ID, then choose **Save locally** (**Lokal speichern**).
3. Choose **Create new game agent** (**Neuen Spiel-Agenten anlegen**). This creates an agent and the `submit_game_turn` client tool in your ElevenLabs account. Alternatively, connect an existing agent configured for the game and its tool ID.
4. Choose **Check agent** (**Agent prüfen**), then **Test response and voice** (**Antwort und Stimme testen**). The conversation test consumes ElevenLabs Agents usage.
5. The customized launcher lists **ElevenLabs Agents** under **Get mods**. Once installed, the mod also appears under **Mods** and in the game's F1 menu.

Version 1.0.1 uses the **character's language** (`Sprache des jeweiligen Charakters`) and `eleven_v4_turbo` by default. **Your microphone language** (`Deine Mikrofon-Sprache`) is independent of this setting and can, for example, remain German. For existing agents, **Enable character voices** (`Charakter-Stimmen aktivieren`) enables the required voice overrides.

To have every caller respond in German, select **Caller language → Everyone in my microphone language** (`Anrufer-Sprache → Alle in meiner Mikrofon-Sprache`) and set **Your microphone language → German** (`Deine Mikrofon-Sprache → Deutsch`). Character-specific voices remain enabled.

On the first call, the backend selects a suitable voice from the available ElevenLabs voices using the character's recorded language, gender, and age. This assignment is retained for each character. Under **Callers and voices** (**Anrufer und Stimmen**), you can load detected callers, preview voices, and change each caller's language or voice. Previewing does not save changes. **Automatic selection** (**Automatische Auswahl**) resets manual overrides. If a character has no explicit language, the game's English default is used; language is not inferred from nationality or name.

The original launcher's mod list is maintained by the `swyf-modding` project. A separate launcher version is therefore included to provide the additional local entries.

## What the mod does

The mod extends the existing AI Backend interface. It forwards dialogue, objective checks, and reviews to the local backend. For dialogue, the agent reports trust and emotion through a restricted client tool, then speaks its response. The mod returns the JSON expected by the game; technical status values are not spoken aloud.

Audio arrives as PCM16 at 24 kHz and is fed into the game's existing 20 ms audio and multiplayer pipeline. Call IDs, subtitles, and completion notifications remain connected through the game's existing events.

For speech recognition, the mod uses the game's existing microphone capture, recognition pauses, voice activity detection, and resampling. Each completed utterance is sent as PCM16 at 16 kHz to an ElevenLabs Agents session. That session closes after receiving the transcript. The response is then generated in a separate Agents session using the game context available at that point. This requires additional session starts. Continuous microphone streaming within a single conversation session is not implemented in this version.

## Requirements and limitations

- Windows x64, Node.js 22.13 or later, and a configured BepInEx installation.
- The mod was built against the game assemblies installed on the development PC. Other game updates may require changes to the hooks.
- An ElevenLabs account with permissions to use Agents and create agents and tools, a usable voice ID, and access to the voice library. The agent must allow voice overrides.
- Actual latency, recognition quality, and multiplayer audio must be tested in the game with your own account. Local protocol tests do not replace that live test.
- Voice selection depends on the voices available to your account and their metadata. If no suitable voice is available or the library cannot be reached, the configured default voice remains available as a labeled fallback. The original game voices are not automatically cloned into ElevenLabs.

API keys are stored locally as plain text in `bridge/.env`. They are not stored in the game configuration or returned in status responses. The local service binds only to `127.0.0.1` and rejects other origins and hosts.

Local voice assignments are stored in `bridge/caller-profiles.json`. Credentials, voice assignments, and runtime logs are excluded from the ZIP packages. When running from `dist`, the corresponding paths are `dist/bridge/.env` and `dist/bridge/caller-profiles.json`.

If the service is unavailable or ElevenLabs rejects a request, the enabled mod reports an error. Disabling it in the F1 menu or mod configuration restores the original providers.

## Development

```powershell
.\scripts\Get-BuildDependencies.ps1
cd bridge
npm ci
npm test
cd ..
.\mod\build.ps1 -NoCopy
.\payout-mod\build.ps1
.\scripts\Stage-Release.ps1
.\scripts\Build-Launcher.ps1
.\scripts\Package-Mod.ps1 -FileName SWYF-Mods-1.1.0.zip
.\scripts\Package-PayoutMod.ps1
```

`scripts/Get-BuildDependencies.ps1` downloads the source dependencies listed below at pinned Git revisions into `upstream/`. Git, Node.js 22.13 or later, and the .NET 8 SDK are required. An optional portable compiler can be placed under `build/tools/dotnet`. Building the C# mods requires locally installed game assemblies and a configured BepInEx installation; game assemblies are not included. The launcher includes the official setup files and their license notices.

Verified checks include local Node tests covering independent character and player languages, stable voice assignments, concurrent callers, and identical greetings, as well as C# context tests, PCM buffer tests, and hook signatures for the installed game version. Real agent responses with language and voice overrides, along with a cleanly completed PCM audio stream, were tested using the configured account. The backend continues sending silent audio packets during voice output and after microphone recordings so that Agents completes its output and short utterances. Full conversation and multiplayer tests are still needed.

For manual installation from the extracted package, run `scripts/Install-Mod.ps1 -PackageDirectory .` (use `-PackageDirectory dist` when building from source). Existing files are backed up under `BepInEx/elevenlabs-agents-backups`. Installation is refused while the game is running.

## Sources

- [AI Backend](https://github.com/swyf-modding/AI-Backend), [shared mod library](https://github.com/swyf-modding/mod-lib), and [launcher](https://github.com/swyf-modding/Launcher), each with its included license notices.
- [ElevenLabs Agents WebSocket](https://elevenlabs.io/docs/eleven-agents/api-reference/eleven-agents/websocket), [configuration overrides](https://elevenlabs.io/docs/eleven-agents/customization/personalization/overrides), and [client events](https://elevenlabs.io/docs/eleven-agents/customization/events/client-events).
- [Speech models](https://elevenlabs.io/docs/overview/models), [agent voices](https://elevenlabs.io/docs/eleven-agents/customization/voice), and [voice library](https://elevenlabs.io/docs/api-reference/voices/search).

This is an unofficial community mod, unaffiliated with the game's developer.

## Additional mod: Wunschsumme

The separate **Wunschsumme** mod replaces the fixed reward for a successful credit card or gift card scam with the whole-number amount agreed during the conversation. For example, "That costs five thousand euros" results in 5,000 units of in-game money after the caller accepts the price and the scam succeeds. For gift cards, the correct fictional gift code must still be submitted. The game's normal success checks remain active; merely mentioning an amount does not award money.

The mod works independently of the speech backend. In multiplayer, the host must load it. The customized launcher provides a separate **Get mods** entry. Instructions and limitations are in `payout-mod/README.md`; the manual installer is `scripts/Install-PayoutMod.ps1`.
