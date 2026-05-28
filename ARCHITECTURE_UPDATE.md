# Sprint 2 — Architecture Update Plan

## Overview

Four features + UI overhaul. All changes stay within the existing MVVM / DI / Clean Architecture
boundaries. No business logic moves between layers; no existing tests or hooks break.

---

## Feature 1 — True Background Service (No Screen Hacks)

### Problem
The current `PeriodicTimer` loop in `MemoryMonitorService` is already alive when the window is
hidden — WPF desktop apps are never suspended by Windows the way UWP/Store apps are.
However, Windows 10+ **Power Throttling** can silently de-prioritize background process CPU
time, which may delay the 1-second tick by hundreds of milliseconds under battery/efficiency
mode.

### Solution (two-layer hardening)

**Layer A — Process-level: Disable EcoQoS / Power Throttling**  
`NativeMethods.cs` already declares `SetProcessInformation` (class 34 = ProcessPowerThrottling).
Call it once in `App.OnStartup` after building the DI container, with `StateMask = 0` / `State = 0`
to opt the entire process out of Power Throttling.

```csharp
// App.xaml.cs — after _services = AppBootstrapper.Build()
DisablePowerThrottling();

private static void DisablePowerThrottling()
{
    // PROCESS_POWER_THROTTLING_STATE { Version=1, ControlMask=0, StateMask=0 }
    // ControlMask=0 → clear all throttling; StateMask=0 → don't enable any.
    Span<uint> state = stackalloc uint[3] { 1, 0, 0 };
    NativeMethods.SetProcessInformation(
        System.Diagnostics.Process.GetCurrentProcess().Handle,
        34, ref state[0], 12);
}
```

**Layer B — Thread-level: AVRT "Games" characteristic**  
`NativeMethods.cs` already declares `AvSetMmThreadCharacteristics`.  
In `MemoryMonitorService.RunLoopAsync`, register the worker task's thread as an AVRT "Games"
task before the tick loop starts. Store the returned handle; close it on cancellation.

```csharp
private async Task RunLoopAsync(CancellationToken ct)
{
    uint taskIndex = 0;
    IntPtr avrtHandle = NativeMethods.AvSetMmThreadCharacteristics("Games", ref taskIndex);
    try { /* existing PeriodicTimer loop */ }
    finally { if (avrtHandle != IntPtr.Zero) NativeMethods.AvRevertMmThreadCharacteristics(avrtHandle); }
}
```

Add `AvRevertMmThreadCharacteristics` declaration to `NativeMethods.cs`.

### Files changed
| File | Change |
|------|--------|
| `App.xaml.cs` | Call `DisablePowerThrottling()` once on startup |
| `Services/Native/NativeMethods.cs` | Add `AvRevertMmThreadCharacteristics` |
| `Services/MemoryMonitorService.cs` | Register AVRT task in `RunLoopAsync` |

---

## Feature 2 — Single Instance Guard (Mutex)

### Solution
Named global `Mutex` checked at the very top of `App.OnStartup`, before any DI or window work.
If another instance owns the mutex the new instance shows a WPF `MessageBox` and calls
`Current.Shutdown()`.

```csharp
private static Mutex? _singleInstanceMutex;

protected override async void OnStartup(StartupEventArgs e)
{
    _singleInstanceMutex = new Mutex(true, @"Global\StandbyAndTimer_v1", out bool isNewInstance);
    if (!isNewInstance)
    {
        MessageBox.Show(
            "Application is already running in the background.\nLook for the icon in the system tray.",
            "StandbyAndTimer",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        Current.Shutdown();
        return;
    }
    // ... rest of startup
}

protected override void OnExit(ExitEventArgs e)
{
    _singleInstanceMutex?.ReleaseMutex();
    _singleInstanceMutex?.Dispose();
    // ... existing cleanup
}
```

### Files changed
| File | Change |
|------|--------|
| `App.xaml.cs` | Add `_singleInstanceMutex` field; guard at top of `OnStartup`; release in `OnExit` |

---

## Feature 3 — Settings Panel + Localization

### 3A — Localization Architecture

**Approach: Swappable ResourceDictionary at runtime**  
WPF's `DynamicResource` automatically re-evaluates when a `ResourceDictionary` entry changes.
We maintain a *language slot* in `Application.Resources.MergedDictionaries`; swapping the
dictionary at that slot triggers a live UI update with zero restarts.

```
App.Resources
  └─ MergedDictionaries[0]  ← Theme.xaml         (static)
  └─ MergedDictionaries[1]  ← Strings.en-US.xaml  ← swapped at runtime
```

**String key convention:** `Str_SectionName_KeyName` (all uppercase section)
e.g. `Str_Memory_Total`, `Str_Settings_LaunchOnStartup`, `Str_Tray_Show`, `Str_Tray_Exit`

**New files:**
- `Views/Strings/Strings.en-US.xaml` — English strings
- `Views/Strings/Strings.tr-TR.xaml` — Turkish strings

**New interface / service:**
```csharp
// Core/Interfaces/ILocalizationService.cs
public interface ILocalizationService
{
    Language CurrentLanguage { get; }
    void SetLanguage(Language lang);
}

// Core/Models/Language.cs (simple enum)
public enum Language { English, Turkish }
```

```csharp
// Services/LocalizationService.cs
// Swaps MergedDictionaries[1] in Application.Current.Resources
// Runs on UI thread via Application.Current.Dispatcher
```

**`AppSettings` change:** add `Language Language { get; set; } = Language.English;`  
`SettingsService` persists/reads it from the same registry key.

### 3B — Settings Panel Architecture

**Choice: Slide-in overlay panel (not a separate Window)**  
A `Grid` overlay at `Panel.ZIndex=10` inside `MainWindow`, initially `Visibility=Collapsed`.
Slides in from the right using a `ThicknessAnimation` on `Margin`. The rest of the window
is dimmed with a semi-transparent overlay behind the panel.

**`SettingsViewModel`** (new, registered as singleton in DI):
```
[ObservableProperty] bool   _isOpen
[ObservableProperty] bool   _autoStartEnabled
[ObservableProperty] Language _selectedLanguage
[RelayCommand] void Close()
[RelayCommand] void ToggleAutoStart()
```
`SettingsViewModel` is owned by `MainViewModel` (constructor injection) — not the view.
`MainViewModel` exposes `OpenSettingsCommand` that delegates to `SettingsViewModel.IsOpen = true`.

**`SettingsPanel.xaml`** — `UserControl` (not Window), placed directly in `MainWindow.xaml`.
Contains:
- Section header "SETTINGS"
- Toggle: "Launch on Startup (Minimized)" → moves from bottom row of main window
- Toggle: Language selector (EN / TR segmented control)
- Close button (×)

**`MainWindow.xaml` changes:**
- Remove `CheckBox` "Launch on Startup (Minimized)" from bottom row
- Replace with a gear-icon `Button` (`⚙`) that fires `OpenSettingsCommand`
- Add dim overlay `Border` + `SettingsPanel` `UserControl` in a containing `Grid`
- All existing layout wraps inside the inner `Grid` child; overlay sits on top via `ZIndex`

### Files changed / created
| File | Action |
|------|--------|
| `Core/Models/Language.cs` | **New** — `Language` enum |
| `Core/Models/AppSettings.cs` | Add `Language` property |
| `Core/Interfaces/ILocalizationService.cs` | **New** |
| `Services/LocalizationService.cs` | **New** |
| `Services/SettingsService.cs` | Persist/load `Language` |
| `Views/Strings/Strings.en-US.xaml` | **New** — all English strings |
| `Views/Strings/Strings.tr-TR.xaml` | **New** — all Turkish strings |
| `Views/SettingsPanel.xaml` | **New** — settings `UserControl` |
| `Views/SettingsPanel.xaml.cs` | **New** — codebehind (animation only) |
| `ViewModels/SettingsViewModel.cs` | **New** |
| `ViewModels/MainViewModel.cs` | Inject `SettingsViewModel`; add `OpenSettingsCommand` |
| `App.xaml` | Merge `Strings.en-US.xaml` as second dictionary |
| `App.xaml.cs` | Apply saved language on startup before showing window |
| `MainWindow.xaml` | Restructure layout; add panel + gear button; all labels → `DynamicResource` |
| `Infrastructure/AppBootstrapper.cs` | Register `ILocalizationService`, `SettingsViewModel` |

---

## Feature 4 — UI Modernization ("Dark Premium/Sleek")

### New Palette — inspired by modern tooling (VS Code Dark+, Raycast, Linear)

| Token | Old value | New value | Purpose |
|-------|-----------|-----------|---------|
| `AppBg` | `#000000` | `#0D0D12` | Main window background |
| `SurfaceBg` | *(new)* | `#13131A` | Card / section background |
| `Surface2Bg` | *(new)* | `#1C1C27` | Hover state surfaces |
| `AppFg` | `#FFFF00` | `#E2E2F0` | Primary text |
| `AppFgDim` | `#80FFFF00` | `#7070A0` | Secondary / hint text |
| `AccentPrimary` | *(new)* | `#7C6AF7` | Purple accent (buttons, active) |
| `AccentHover` | *(new)* | `#9585F9` | Accent hover |
| `AccentPressed` | *(new)* | `#6455E0` | Accent pressed |
| `InputBg` | `#FFFF00` | `#1C1C27` | TextBox background |
| `InputFg` | `#000000` | `#C8C8E0` | TextBox text |
| `BorderColor` | `#FFFF00` | `#2A2A3D` | Section borders |
| `SeparatorColor`| `#40FFFF00`| `#1E1E2E` | Separator lines |
| `SuccessColor` | *(new)* | `#4ADE80` | Purge success, timer active |
| `SelectionBg` | `#CCCC00` | `#3D3670` | ListBox selection |

### Control changes
- **Font**: `Segoe UI` (13px regular weight for body, `SemiBold` for labels)
- **Button**: `CornerRadius="6"`, `BorderThickness="0"` (flat with accent fill),
  smooth `ColorAnimation` 150ms on hover/press instead of hard `Trigger` swap
- **Section Border**: `CornerRadius="10"`, `Background="{DynamicResource SurfaceBg}"`,
  no border line (depth via background contrast)
- **ListBox / ListBoxItem**: `CornerRadius="6"` on items, no yellow; accent selection
- **CheckBox**: custom template with accent-colored checkmark, smooth 120ms transition
- **TextBox**: `CornerRadius="6"`, subtle left accent bar on focus (`BorderThickness="0,0,0,2"`)
- **Status bar**: subdued separator, `AppFgDim` colour for hint text

### Files changed
| File | Change |
|------|--------|
| `Views/Styles/Theme.xaml` | Full rewrite of palette + all control styles |
| `MainWindow.xaml` | New `FontFamily`, remove hardcoded colours, adjust widths for new font metrics |

---

## Complete File Change Summary

### New files (7)
```
Core/Models/Language.cs
Core/Interfaces/ILocalizationService.cs
Services/LocalizationService.cs
Views/Strings/Strings.en-US.xaml
Views/Strings/Strings.tr-TR.xaml
Views/SettingsPanel.xaml
Views/SettingsPanel.xaml.cs
ViewModels/SettingsViewModel.cs
```

### Modified files (11)
```
App.xaml                              ← merge Strings dict
App.xaml.cs                           ← Mutex + power throttling + lang init
Core/Models/AppSettings.cs            ← add Language property
Services/Native/NativeMethods.cs      ← add AvRevertMmThreadCharacteristics
Services/MemoryMonitorService.cs      ← AVRT registration in loop
Services/SettingsService.cs           ← persist Language
ViewModels/MainViewModel.cs           ← inject SettingsViewModel, OpenSettingsCommand
Infrastructure/AppBootstrapper.cs     ← register new services
MainWindow.xaml                       ← layout restructure, DynamicResource strings
MainWindow.xaml.cs                    ← no logic change, just remove autostart binding
Views/Styles/Theme.xaml               ← full palette + style rewrite
```

**Total: 8 new files, 11 modified files — zero deleted files.**

---

## Implementation Order (dependency-safe)

1. `Language.cs` + `ILocalizationService.cs` (leaf, no deps)
2. `AppSettings.cs` + `SettingsService.cs` (Language field)
3. `LocalizationService.cs`
4. `Strings.en-US.xaml` + `Strings.tr-TR.xaml`
5. `Theme.xaml` (independent visual rewrite)
6. `NativeMethods.cs` + `MemoryMonitorService.cs` (AVRT)
7. `SettingsViewModel.cs`
8. `SettingsPanel.xaml` + `SettingsPanel.xaml.cs`
9. `MainViewModel.cs` (inject SettingsViewModel)
10. `AppBootstrapper.cs` (register all new services)
11. `MainWindow.xaml` + `MainWindow.xaml.cs`
12. `App.xaml` + `App.xaml.cs` (Mutex + power throttling + lang init)
