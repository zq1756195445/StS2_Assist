# SpireGuide

SpireGuide is now structured as a Rust + Tauri desktop app for a Slay the Spire 2 guidance overlay.

Current scope:

- Rust backend for mock game-state generation and rule-based recommendations
- Tauri transparent overlay window
- Vite frontend for HUD rendering
- local JSON database for cards, relics, and archetypes
- macOS game-window tracking via AppleScript fallback logic

## Run

1. `npm install`
2. `npm start`

## Test

- `npm test`

## Current assumptions

- Real game-state reading is still mocked.
- Overlay attachment to the game window depends on macOS accessibility permissions.
- Recommendations are deterministic heuristics meant to validate the architecture.

## Main paths

- frontend entry: [index.html](/Users/cheemtain/StS2_Assist/index.html)
- frontend logic: [src/main.js](/Users/cheemtain/StS2_Assist/src/main.js)
- frontend styles: [src/styles.css](/Users/cheemtain/StS2_Assist/src/styles.css)
- Rust backend: [src-tauri/src/main.rs](/Users/cheemtain/StS2_Assist/src-tauri/src/main.rs)
