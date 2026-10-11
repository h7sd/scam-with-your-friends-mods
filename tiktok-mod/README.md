# TikTok Pocket for Scam With Your Friends

TikTok Pocket adds a purchasable **TikTok** browser app to **Scamazon**, with an original phone/play icon and a portrait window. It opens the official TikTok website inside the game's embedded browser. The mod interface is in English. The stock address bar is hidden; compact **Back** and **Refresh** controls remain available.

## In the game

1. Install TikTok Pocket and start the game.
2. Open **Scamazon** and buy **TikTok Pocket** with in-game currency.
3. Open the app from the game desktop to visit the official TikTok website in its phone-shaped window. See the video compatibility limitation below before relying on it for video playback.
4. You may browse without signing in wherever TikTok allows it. To sign in, enter your credentials yourself in the official TikTok page.

The normal game purchase and ownership system controls access. Buying the app does not buy a TikTok subscription or spend real money.

## Local browser session

TikTok Pocket uses a separate browser client and local profile from the game's normal Browser app. Its profile is stored under `%LOCALAPPDATA%\ScamWYF-Mods\TikTokPocket\Profile`. The browser handles cookies and site storage; this mod does not extract or upload passwords or cookies. Browser profiles are excluded from source control and release archives. Remote browser debugging is disabled for this profile.

TikTok still needs an internet connection and handles its own login, videos and account data. There is no additional mod server or account. Saved browser data can preserve a login, but TikTok may expire it or request a new login. Session-only cookies may not survive browser shutdown.

## Compatibility

The mod uses the browser engine already shipped with the game and does not redistribute game or browser-engine assemblies. Native browser video codecs, TikTok's current site behavior and your region determine whether a particular video or guest feed plays. A displayed page alone does not prove video playback. The current live test loads TikTok's guest feed, but the tested videos show a playback error; video playback is not yet working in the tested game runtime.

Under **F1 → TikTok Pocket**, **Check video compatibility** checks H.264, AAC, WebM and AV1 support and the browser's numeric media error. It collects no video addresses, browsing history, cookies or account information. Its bounded result appears in the mod menu and the game mod log.

The normal Browser app keeps its own page and profile. Multiplayer purchase/ownership uses the game's existing system; browsing sessions stay local to the PC. Content displayed on a game screen remains subject to the game's own screen-sharing behavior.

## Installation

Close the game and install **TikTok Pocket** under **Get mods** in the customized launcher. For a manual installation, extract the standalone ZIP and run:

```powershell
.\scripts\Install-TikTokMod.ps1 -GameDirectory 'C:\Path\To\Game' -PackageDirectory .
```

The game must already have BepInEx set up. Existing compatible shared mod libraries are retained. Replaced plugin/icon files are backed up under `BepInEx/tiktok-pocket-backups`. Browser profiles and the other mods' configuration are not modified by the installer.

## Development

```powershell
.\scripts\Create-TikTokIcon.ps1
.\tiktok-mod\build.ps1
```

The original icon source is `assets/tiktok-pocket.svg`; the game uses the PNG rendered by the included script. The hooks target the installed **SWYF v82-playtest**, with **Unity 6000.3.10f1** and its bundled UnityWebBrowser/CEF runtime. A game update may require rebuilding or adapting the hooks.

Unofficial community mod, unaffiliated with TikTok or the game's developer.
