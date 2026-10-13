# Third-party components

The source in this repository is licensed under MIT. Separately downloaded dependencies and bundled release components retain their own licenses.

- AI Backend and ScamWYF.Modding.Core: [swyf-modding/AI-Backend](https://github.com/swyf-modding/AI-Backend) and [swyf-modding/mod-lib](https://github.com/swyf-modding/mod-lib), MIT. Their license notices are included in the release packages.
- The customized launcher is built from [swyf-modding/Launcher](https://github.com/swyf-modding/Launcher), MIT. Its license is included beside the launcher.
- The setup payload comes from [swyf-modding/Setup](https://github.com/swyf-modding/Setup), with its own license and included Mono and Unity Doorstop notices.
- The bridge uses ajv and ws, MIT. Their package license files remain in the bundled node_modules directory.

Game assemblies are referenced only from the user's local installation and are not distributed. ElevenLabs is an external service; its terms and usage charges apply to the user's own account.

TikTok Pocket 1.0.1 includes a self-contained Windows browser worker. Its third-party license and notice files are included in `BepInEx/plugins/TikTokPocket/BrowserWorker/Licenses` and in the source under `webview-worker/Licenses`. They cover the Microsoft WebView2 SDK, .NET runtime components, CsWinRT, System.Text.Json and Vortice graphics dependencies. These components retain their respective licences. The Microsoft Edge WebView2 Evergreen Runtime is installed separately and is not redistributed here.

The game's normal Browser app retains the UnityWebBrowser/CEF runtime already included in the game; that runtime is not redistributed by this mod. TikTok is an external website and handles its own videos, login and account data. The original TikTok Pocket phone/play icon is included under this repository's MIT license. This project is unaffiliated with TikTok.
