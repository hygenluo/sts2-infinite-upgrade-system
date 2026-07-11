# Infinite Upgrade System (Wu Xian Sheng Ji Xi Tong)

An STS2 mod that adds an infinite upgrade system with currency points.

## Features (Iteration 1)

- Press **P** to open the upgrade UI (outside combat only)
- Displays current available upgrade points
- Select a card from your deck and spend **5 points** to upgrade it
- Infinitely repeatable upgrades — upgrades reset the card's upgrade counter
- Debug: add 999 points via the UI button

## Dependencies

- [Slay the Spire 2](https://store.steampowered.com/app/2862050)
- [BaseLib](https://github.com/Alchyr/BaseLib-StS2) (auto-adapts to latest version, minimum v3.3.2)

## Build

```powershell
dotnet build -c Debug
```

The mod is automatically deployed to `%Sts2Dir%/mods/InfiniteUpgradeSystem/`.

## Game Test

1. Make sure BaseLib is enabled in the mod manager
2. Enable InfiniteUpgradeSystem
3. Start a run
4. Press **P** to open the upgrade UI
5. Click "Select a card to upgrade" to test

## Points System

- Start each run with **5** points
- Spend **5** points to upgrade a card (infinitely repeatable)
- Points persist across combats within the same run
