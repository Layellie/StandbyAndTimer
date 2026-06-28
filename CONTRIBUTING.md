# Contributing

Thanks for considering a contribution. StandbyAndTimer is a one-person
project; the bar here isn't formal, but a few conventions keep the codebase
coherent.

## Filing issues

Use the [issue templates](https://github.com/Layellie/StandbyAndTimer/issues/new/choose).
- **Bug:** include app version, Windows build (`winver`), and ~30 lines from
  `log.txt` (Settings → Open Log Folder).
- **Feature:** lead with the user-visible problem before proposing a
  solution. Mockups welcome.
- **Security:** see [`SECURITY.md`](SECURITY.md) — please don't open a public
  issue.

## Sending pull requests

### Setup

```powershell
git clone https://github.com/Layellie/StandbyAndTimer.git
cd StandbyAndTimer
dotnet build StandbyAndTimer/StandbyAndTimer.csproj -c Release
```

You need the .NET 10 SDK. UAC will prompt when you run the app — the P/Invoke
calls require Administrator.

### Conventions

- **Code style** matches what's already in the file. Don't reformat unrelated
  code in a feature PR.
- **All P/Invoke** must live in `Services/Native/NativeMethods.cs`. Nothing
  else should import `System.Runtime.InteropServices` directly.
- **MVVM** strictly: views own no logic beyond what's in `MainWindow.xaml.cs`
  today (DataContext wiring + drag/drop event forwarding).
- **Localization:** every user-visible string ships in both
  `Strings.en-US.xaml` and `Strings.tr-TR.xaml`. If you add an English string
  and don't speak Turkish, mark the TR entry `<!-- TODO translate -->` and
  I'll fill it in on review.
- **No new top-level files** without a reason — the root is curated
  (`ARCHITECTURE.md`, `CLAUDE.md`, `LICENSE`, `README.md`, `SECURITY.md`,
  `CONTRIBUTING.md`).
- **One PR = one logical change.** Bundle small Settings-panel polish fixes
  if they're all the same theme; don't bundle "polish + new feature."
- **CI must pass** (`Build (Release)` with `-warnaserror`). Don't bypass.

### Review

I'll usually respond within a few days. For non-trivial PRs I might ask for
a follow-up commit rather than push directly — please don't squash mid-review
unless asked.

## Architecture pointer

Before changing service wiring or adding a new layer, skim
[`ARCHITECTURE.md`](ARCHITECTURE.md). The Core / Services / Infrastructure /
ViewModels split is enforced by convention, not the compiler — keeping it
clean is on us.
