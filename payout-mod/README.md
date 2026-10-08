# Requested Payout for Scam With Your Friends

This standalone BepInEx mod replaces the fixed reward for the **credit-card scam** or **gift-card scam** with your clearly stated whole-number service price. Version **1.1.1** fixes amounts such as **20,000** falling back to the normal reward. The mod is shown as **Wunschsumme** in the launcher and the F1 menu.

## In the game

1. Start a new call and use the credit-card or gift-card scam.
2. State an unambiguous whole-number price, such as **"This costs 20,000 euros"**, **"This costs twenty thousand euros"** or **"Das kostet zwanzigtausend Euro."**
3. Check the **Wunschsumme** F1 page: the requested amount should be remembered. You can state a new clear price before completing the scam. The game no longer needs to confirm its optional AI price objective first.
4. Complete the scam successfully in the appropriate app. Gift cards still require the **correct fictional gift-card code**. Only then is the requested amount credited instead of the normal 400 game currency for a credit card or 200 for a gift card.

Prices are tracked separately for each call and scam. An offer mentioning **credit card/Kreditkarte** targets the credit-card scam; one mentioning **gift card/Geschenkkarte/Gutschein** targets the gift-card scam. A generic service price applies to the supported card scams available in that call. The latest clear player offer wins over older offers and delayed AI results. The game's existing card verification and single-payout rule remain active. Personal earnings, spendable money and team money are updated through the normal server path. The F1 menu shows the requested amount and payout status.

Without an unambiguously recognized price, the normal reward remains in effect. Use explicit price or currency wording. A standalone number is only accepted as the immediate answer to a caller asking for a price, and not to a card/PIN/code question. Decimal amounts are not rounded. Card numbers and other numbers are not automatically treated as prices. There is no currency conversion: the stated number becomes the amount in game currency.

Very large amounts that would exceed the game's native integer limit when combined with other possible rewards fall back to the original reward. The shared game catalog is not changed; the optional gift-card price objective exists only in a private copy for that call. Optional AI price agreement remains available, but it is no longer required to remember a clearly stated player price.

### Why 1.1.1 was needed

The native game can award a successful card submission before its optional service/price objectives have completed. Earlier versions depended on those objectives and could therefore pay only the original reward despite a clear 20,000-price offer. The native AI detector also limits its metadata `amount` to 10,000. This version remembers player offers directly and normalizes only eligible non-final price metadata to zero; the real payout amount comes from the dialogue, while other objectives keep their native validation.

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

Regression tests cover clear 20,000 offers followed by native card submissions with no optional price goals completed, both card scams, incorrect codes, repeated submissions, scoped and generic offers, delayed AI results, call disposal and integer overflow limits. Optional price evaluation also runs through the game's actual methods. Installation and backups are tested separately in isolated folders.

Version **1.1.1** passes **117 source assertions** and **147 native/Harmony assertions**, including the actual 400/200 rewards and `scam-*` product IDs from the installed game catalog. Payout and recorded earnings remain identical if a price changes or the mod is disabled during the native completion event.

The tests require a .NET 8 SDK. Native tests also require the installed game and run in a separate test process without changing game files or the running game. The native test script uses the bundled SDK when available, otherwise an installed `dotnet` from `PATH`. Use `-DotnetPath` to select another `dotnet.exe`.

```powershell
dotnet run --project .\payout-mod\test\Payout.Tests.csproj
.\payout-mod\test\Run-NativeTests.ps1
.\scripts\Test-PayoutInstaller.ps1
```

The hooks target **SWYF v82-playtest (Unity 6000.3.10f1)**, the game version used for validation. A game update may require a rebuild. The mod does not store dialogue or card numbers in its logs.

Unofficial community mod for the fictional game.
