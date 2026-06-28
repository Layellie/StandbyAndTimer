## Summary

<!-- 1–3 bullets on what changed and why. Skip if the title is self-explanatory. -->

## Files

<!-- One line per touched area, e.g.
- `Services/TimerResolutionService.cs` — restart watchdog when MMCSS thread name changes
-->

## Test plan

- [ ] `dotnet build -c Release` — 0 warnings, 0 errors
- [ ] Smoke-tested the affected feature manually (UAC accepted, app running)
- [ ] Touched a `Views/Cards/*.xaml`? Resized window, confirmed no layout shift
- [ ] Touched a service that owns a `PerformanceCounter`, `Process`, or native handle? Verified `OnExit` still releases it
- [ ] Touched a string in `Strings.en-US.xaml`? Mirrored in `Strings.tr-TR.xaml`

## Release note

<!-- One sentence the next GitHub release page should include, or "internal — skip changelog". -->
