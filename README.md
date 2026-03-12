# SpireGuide

SpireGuide is a Tauri + Vite HUD overlay for Slay the Spire 2.

Current status:

- Battle HUD is event-driven
- Battle refresh is triggered from `godot.log`, not snapshot polling
- Live battle state is read from CLR memory via `tools/Sts2ClrProbe`
- Battle, shop, event, and treasure probe paths are now scene-gated
- Battle, shop, treasure, and event probe reads are now root-based or model-based
- Monster name, intent, gold, player current HP, player max HP, and current energy are wired through the live pipeline
- Replay/history is intentionally de-emphasized right now so battle data stays clean

## Run

Install dependencies:

```powershell
npm install
```

Start the HUD:

```powershell
npm start
```

Start the game with the log-event bridge in another terminal:

```powershell
cd D:\StS2_Assist\repo
powershell -ExecutionPolicy Bypass -File .\tools\launch_sts2_with_log_bridge.ps1
```

There is also an npm shortcut:

```powershell
npm run game:launch-log
```

Notes:

- Keep the HUD running in one terminal and launch the game from a second terminal.
- The launcher now auto-discovers `SlayTheSpire2.exe` from the Steam registry path and `libraryfolders.vdf`.
- If auto-discovery still fails on a custom setup, you can still pass `-GameExe "..."` manually.
- If Steam requires it, keep `steam_appid.txt` with app id `2868840` in the game folder when launching outside Steam.
- `tools/launch_sts2_with_hook.ps1` is now only a compatibility wrapper and forwards to the log bridge launcher.

## How It Works

Battle refresh chain:

1. Slay the Spire 2 appends combat lines to `%APPDATA%\SlayTheSpire2\logs\godot.log`
2. `tools/launch_sts2_with_log_bridge.ps1` tails that file
3. Matching lines send a local TCP `refresh` event to `127.0.0.1:43125`
4. Tauri refreshes live memory cache
5. Tauri emits `snapshot-updated`
6. Frontend re-renders without polling

Current battle refresh triggers include:

- `Player 1 playing card ...`
- `Player 1 chose cards ...`
- `Monster ... performing move ...`
- combat room entry

## Validation

When the bridge is working, the launcher terminal should show lines like:

```text
[INFO] Player 1 playing card ARMAMENTS (no target)
HUD refresh -> play-card
```

Expected behavior:

- HUD updates immediately after playing a card
- HUD updates after enemy moves
- Battle enemy names and intents match the game
- Player HP and energy update from live memory

## Probe Status

`tools/Sts2ClrProbe` currently exposes these scene-aware live reads:

- `battle`
  - `hand`
  - `enemies`
  - `player`
- `reward`
  - `rewardCards`
- `event`
  - `eventOptions`
  - `eventPage`
- `shop`
  - `shopOffers`
- `treasure`
  - `treasureRelics`
- `rest`
  - scene placeholder only, content reading not implemented yet

Current root-based / model-based status:

- `battle`: root-based
- `shop`: root-based
- `treasure`: root-based
- `event`: reads current `NEventOptionButton -> EventOption` directly
- `reward`: stable and scene-gated, but still less clean than shop/treasure

This means non-battle scene reads are no longer all mixed together and then filtered afterward. The probe now detects the active scene first and only reads the relevant scene payload.

## Probe Dev Workflow

Validation should use the current `bin\Debug` build:

```powershell
cd /d D:\StS2_Assist\repo
dotnet build .\tools\Sts2ClrProbe\Sts2ClrProbe.csproj
dotnet exec .\tools\Sts2ClrProbe\bin\Debug\net8.0\Sts2ClrProbe.dll --json SlayTheSpire2
```

Notes:

- Prefer `bin\Debug` over ad-hoc output folders so manual validation and the HUD read the same probe build.
- When debugging a specific scene, stop the game on that screen first, then run the probe.

Useful probe modes:

```powershell
dotnet exec .\tools\Sts2ClrProbe\bin\Debug\net8.0\Sts2ClrProbe.dll --json SlayTheSpire2
```

```powershell
dotnet exec .\tools\Sts2ClrProbe\bin\Debug\net8.0\Sts2ClrProbe.dll --json --with-ui-candidates SlayTheSpire2
```

```powershell
dotnet exec .\tools\Sts2ClrProbe\bin\Debug\net8.0\Sts2ClrProbe.dll --dump-type "MegaCrit.Sts2.Core.Nodes.Events.NEventOptionButton" --dump-limit 3 SlayTheSpire2
```

```powershell
dotnet exec .\tools\Sts2ClrProbe\bin\Debug\net8.0\Sts2ClrProbe.dll --find-types "Treasure,Chest,Relic,Reward" SlayTheSpire2
```

## Test

```powershell
npm test
```

Optional frontend build:

```powershell
npm run build
```

## Known Limits

- Overlay attachment is still a preview-style fullscreen overlay, not the final stable attachment mode.
- Log-driven refresh can still receive duplicate game lines in some cases; this is mostly cosmetic right now.
- `rest` / campfire content is not implemented yet; only the scene placeholder exists.
- `reward` is reliable in practice, but it is not yet as cleanly root-based as `shop` or `treasure`.
- CLR type and field names are version-sensitive, so future game updates may require probe adjustments.

## Machine And Version Notes

This setup is not strongly tied to one Windows machine, but it does depend on:

- a valid `SlayTheSpire2.exe` path
- Windows `%APPDATA%` log/save layout
- .NET being installed
- the current game version still using the same CLR type/field names and log formats

In practice, machine portability is much better than version portability.

## Main Paths

- frontend entry: [index.html](/D:/StS2_Assist/repo/index.html)
- frontend logic: [src/main.js](/D:/StS2_Assist/repo/src/main.js)
- frontend styles: [src/styles.css](/D:/StS2_Assist/repo/src/styles.css)
- Rust backend: [src-tauri/src/main.rs](/D:/StS2_Assist/repo/src-tauri/src/main.rs)
- log bridge launcher: [tools/launch_sts2_with_log_bridge.ps1](/D:/StS2_Assist/repo/tools/launch_sts2_with_log_bridge.ps1)
- CLR probe: [tools/Sts2ClrProbe/Program.cs](/D:/StS2_Assist/repo/tools/Sts2ClrProbe/Program.cs)
- battle helpers: [tools/Sts2ClrProbe/ProbeCommon.cs](/D:/StS2_Assist/repo/tools/Sts2ClrProbe/ProbeCommon.cs)
- reward helpers: [tools/Sts2ClrProbe/ProbeReward.cs](/D:/StS2_Assist/repo/tools/Sts2ClrProbe/ProbeReward.cs)
- event helpers: [tools/Sts2ClrProbe/ProbeEvent.cs](/D:/StS2_Assist/repo/tools/Sts2ClrProbe/ProbeEvent.cs)
- shop helpers: [tools/Sts2ClrProbe/ProbeShop.cs](/D:/StS2_Assist/repo/tools/Sts2ClrProbe/ProbeShop.cs)
- treasure helpers: [tools/Sts2ClrProbe/ProbeTreasure.cs](/D:/StS2_Assist/repo/tools/Sts2ClrProbe/ProbeTreasure.cs)
