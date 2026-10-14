# UiRender

Developer tool, not shipped. It loads the app's real XAML views with no-op fake services, so nothing touches the registry, the system timer, the standby list or other processes. It renders every screen for Dark/Light × English/Türkçe and checks the result.

```powershell
dotnet run --project tools/UiRender -c Release -- [outDir] [--screen]
```

- `outDir` defaults to `ui-render-out` (git-ignored).
- `--screen` also captures the real window from the screen for Dark/Türkçe. The offscreen path renders in software, so only this capture shows GPU artifacts such as stray edge lines. It briefly shows the window at the top left.

## What it checks

- **Layout:** text that does not fit, and panels that overflow their card, in every theme and language.
- **View model behaviour through the real views:** hotkey rebinding and recording, timer lock states, Game Mode auto-detect, settings import.
- **Theme:** lime buttons draw their label in `AccentOnText`; tray menu colours match the palette.
- **Strings:** multi-line strings keep their line breaks after XAML loading.

Each failed check prints `FAIL` and the exit code is the failure count. Checks that press keys read the real keyboard state, so they are skipped (`SKIP`) while a modifier key is physically held.

Run it after any change to XAML, styles or strings, then look at the PNGs: the checks catch overflow, not ugliness.
