## STS2 localization dump

This folder contains targeted localization files extracted from the game's
`SlayTheSpire2.pck` package.

Current scope:
- `localization/eng/*.json`
- `localization/zhs/*.json`

Refresh command:

```powershell
node .\tools\extract_sts2_localization.mjs
```

Environment overrides:
- `STS2_PCK_PATH`: alternate `.pck` file path
- `GODOT_PCK_TOOL`: alternate `godotpcktool.exe` path

The extractor also writes `report.json` with key counts and missing-key coverage
between English and Simplified Chinese for the main HUD/compendium datasets.
