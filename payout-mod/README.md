# Requested Payout for Scam With Your Friends

This standalone BepInEx mod replaces the fixed reward for the **credit-card scam** or **gift-card scam** with the price the caller agreed to during the conversation. Version **1.1.0** supports both scams. The mod is shown as **Wunschsumme** in the launcher and the F1 menu.

## In the game

1. Start a new call and use the credit-card or gift-card scam.
2. State an unambiguous whole-number price, such as **"This costs 5,000 euros"** or **"This costs five thousand euros."**
3. Convince the caller to agree to that price. The game must confirm the price objective. For gift cards, the caller must first accept the offer of help and the solution; the mod adds an optional price objective for each call.
4. Complete the scam successfully in the appropriate app. Gift cards still require the **correct fictional gift-card code**. Only then is the agreed amount credited instead of the normal 400 game currency for a credit card or 200 for a gift card.

Prices are tracked separately for each call and scam. Credit cards and gift cards can have different prices within the same call. The first confirmed amount stays fixed. The game's existing success checks and single-payout rule remain active. Personal earnings, spendable money and team money are updated through the normal server path. The F1 menu shows price confirmation and payout status.

Without a confirmed, unambiguously recognized price, the normal reward remains in effect. Decimal amounts are not rounded. Card numbers and other numbers are not automatically treated as prices. There is no currency conversion: the stated number becomes the amount in game currency.

Very large amounts that would exceed the game's native integer limit when combined with other possible rewards fall back to the original reward. The shared game catalog is not changed; the optional gift-card price objective exists only in a private copy for that call.

## Installation

Close the game, then install the mod in the customized launcher under **Get mods → Wunschsumme → Install…**. It will then appear under **Mods** and in the F1 menu.

Alternatively, extract the package and run:

```powershell
.\scripts\Install-PayoutMod.ps1 -GameDirectory 'C:\Path\To\Game' -PackageDirectory .
```

The game must already be prepared for BepInEx mods. The package includes the shared mod library, version 1.0.4 or later. A compatible library that is already installed is kept. The installer backs up replaced files under `BepInEx/requested-payout-backups`.

In multiplayer, the **host** must load this mod. It requires neither ElevenLabs nor a separate AI backend provider.

## Development

```powershell
.\payout-mod\build.ps1
```

For version **1.1.0**, all **80 source assertions** and **72 native/Harmony assertions** pass. These tests cover amounts and call state, both scams, incorrect codes, repeated submissions, separate prices, call disposal and integer overflow limits. They also exercise the optional gift-card price objective through the game's actual dialogue evaluation and payout methods. Installation and backups are tested separately in isolated folders.

The tests require a .NET 8 SDK. Native tests also require the installed game and run in a separate test process without changing game files or the running game. The native test script uses the bundled SDK when available, otherwise an installed `dotnet` from `PATH`. Use `-DotnetPath` to select another `dotnet.exe`.

```powershell
dotnet run --project .\payout-mod\test\Payout.Tests.csproj
.\payout-mod\test\Run-NativeTests.ps1
.\scripts\Test-PayoutInstaller.ps1
```

The hooks target the game version installed on this PC. A game update may require a rebuild. The mod does not store dialogue or card numbers in its logs.

Unofficial community mod for the fictional game.
