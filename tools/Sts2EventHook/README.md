# Sts2EventHook

Minimal in-proc Harmony hook bridge for the HUD.

Current behavior:

- When this assembly is loaded through `DOTNET_STARTUP_HOOKS`, `StartupHook.Initialize()` runs automatically.
- It patches a small set of combat- and hand-related candidate methods by name.
- Every matched method sends a line-delimited JSON `refresh` event to `127.0.0.1:43125`.
- The Tauri app listens on that port and refreshes its cached live state immediately.

Event shape:

```json
{"kind":"refresh","source":null,"trigger":{"typeName":"...","methodName":"..."}}
```

Notes:

- This project is the event bridge.
- The default loader path in this repo is `.NET startup hooks`, using `DOTNET_STARTUP_HOOKS`.
- The hook manifest is intentionally string-based so we can iterate without compile-time references to game assemblies.

Quick start:

1. Build the hook project.
2. Start the Tauri HUD.
3. Launch the game through `tools/launch_sts2_with_hook.ps1`.

Example:

```powershell
npm run hook:build
powershell -ExecutionPolicy Bypass -File .\tools\launch_sts2_with_hook.ps1
```
