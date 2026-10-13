# TikTok Pocket for Scam With Your Friends

TikTok Pocket **1.0.1** adds a purchasable **TikTok** browser app to **Scamazon**, with an original phone/play icon and a portrait window. It displays the official TikTok website inside the game using a separate Microsoft Edge WebView2 browser worker. The mod interface is in English. The address bar is hidden; compact **Back** and **Refresh** controls remain available.

## In the game

1. Install TikTok Pocket and start the game.
2. Open **Scamazon** and buy **TikTok Pocket** with in-game currency.
3. Open the app from the game desktop to visit the official TikTok website in its phone-shaped window.
4. You may browse without signing in wherever TikTok allows it. To sign in, enter your credentials yourself in the official TikTok page.

The normal game purchase and ownership system controls access. Buying the app does not buy a TikTok subscription or spend real money.

## Local browser session

TikTok Pocket uses a separate browser client and local profile from the game's normal Browser app. Its profile is stored under `%LOCALAPPDATA%\ScamWYF-Mods\TikTokPocket\WebView2Profile`. The browser handles cookies and site storage; this mod does not extract or upload passwords or cookies. Browser profiles are excluded from source control and release archives. No remote browser-debugging port is opened.

The previous CEF profile used by version 1.0.0 is left untouched. Its login is not imported into WebView2, so you may need to sign in again after this update. With local session persistence disabled, the app uses a separate temporary guest profile.

TikTok still needs an internet connection and handles its own login, videos and account data. There is no additional mod server or account. Saved browser data can preserve a login, but TikTok may expire it or request a new login. Session-only cookies may not survive browser shutdown.

## Compatibility

Windows 10 version 2004 or later (x64), BepInEx and the **Microsoft Edge WebView2 Evergreen Runtime** are required. The self-contained browser worker is included; players do not need the .NET SDK. The game's normal Browser app continues using its original engine. Game assemblies and the WebView2 runtime are not bundled with this mod.

Version 1.0.0 loaded the site but showed video errors in the game's original browser engine. The new WebView2 renderer passed an isolated test of the actual TikTok guest feed: playback time advanced, captured frames changed and the video reported no media error. A separate local test verified click, scroll, text, Backspace and Enter input without moving Windows focus. TikTok's site behavior, region and account restrictions can still affect which videos or guest feeds are available.

Under **F1 → TikTok Pocket**, **Check video compatibility** checks H.264, AAC, WebM and AV1 support and the browser's numeric media error. It collects no video addresses, browsing history, cookies or account information. Its bounded result appears in the mod menu and the game mod log. Browser frames are transported locally in memory and are not saved to disk by the mod.

The normal Browser app keeps its own page and profile. Multiplayer purchase/ownership uses the game's existing system; browsing sessions stay local to the PC. Content displayed on a game screen remains subject to the game's own screen-sharing behavior.

## Installation

Close the game and install [TikTok-Pocket-1.0.1.zip](https://github.com/h7sd/TikTok-Pocket/releases/latest) through the standard launcher. The thin ZIP contains only the plugin, shared library and `setup-tiktok-pocket.ps1`, all at its root. The optional catalogue setup step downloads the icon and browser worker from a fixed release and verifies its SHA-256. Skipping it leaves the DLL installed but the app unavailable until those files are installed.

For installation through the direct-URL field, the stock launcher does not offer catalogue setup steps. Extract the same ZIP and run its root script yourself after the DLLs are installed:

```powershell
.\setup-tiktok-pocket.ps1 -GameDir 'C:\Path\To\Game'
```

The game must already have BepInEx set up. The catalogue routes the shared DLL to `core`. The optional script backs up replaced icon and worker files and preserves browser profiles and other mods' configuration. The worker payload is checked against its SHA-256 manifest before installation and startup. It does not install the Microsoft Edge WebView2 Runtime automatically. Source builds with the staged `dist` layout can still use `scripts/Install-TikTokMod.ps1 -PackageDirectory dist`.

## Development

```powershell
.\scripts\Create-TikTokIcon.ps1
.\webview-worker\build.ps1
.\tiktok-mod\build.ps1
```

The original icon source is `assets/tiktok-pocket.svg`; the game uses the PNG rendered by the included script. The hooks target the installed **SWYF v82-playtest**, with **Unity 6000.3.10f1**. The worker uses pinned NuGet dependencies and publishes its exact file inventory. A game update may require rebuilding or adapting the hooks.

Unofficial community mod, unaffiliated with TikTok or the game's developer.
