using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StandbyAndTimer;
using StandbyAndTimer.Core.Interfaces;
using StandbyAndTimer.Core.Models;
using StandbyAndTimer.ViewModels;
using WinForms = System.Windows.Forms;

// No-op stand-in for every service interface: void → nothing, value types →
// default, Task → completed, Task<T> → completed default. Nothing touches
// the registry, the system timer, the standby list or processes.
public class NullProxy : DispatchProxy
{
    protected override object? Invoke(MethodInfo? m, object?[]? args)
    {
        var rt = m!.ReturnType;
        if (rt == typeof(void)) return null;
        if (rt == typeof(Task)) return Task.CompletedTask;
        if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var arg = rt.GetGenericArguments()[0];
            var def = arg.IsValueType ? Activator.CreateInstance(arg) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(arg).Invoke(null, [def]);
        }
        return rt.IsValueType ? Activator.CreateInstance(rt) : null;
    }
}

// Timer stand-in: records Activate/Deactivate, never calls NtSetTimerResolution.
public sealed class FakeTimer : ITimerResolutionService
{
    public int Activations, Deactivations;
    public bool IsActive { get; private set; }
    public event EventHandler<double>? ResolutionMeasured { add { } remove { } }
    public double Activate() { Activations++; IsActive = true; return 0.5; }
    public Task<double> ActivateAsync(CancellationToken ct = default) => Task.FromResult(Activate());
    public void Deactivate() { Deactivations++; IsActive = false; }
    public void Dispose() { }
}

// Settings stand-in that returns a given snapshot from Load.
public sealed class FixedSettings(AppSettings toLoad, List<AppSettings> saved) : ISettingsService
{
    public AppSettings Load() => toLoad;
    public void Save(AppSettings settings) => saved.Add(settings);
}

// Records every Save (in memory only — never touches the registry).
public sealed class RecordingSettings(List<AppSettings> saved) : ISettingsService
{
    public AppSettings Load() => new();
    public void Save(AppSettings settings) => saved.Add(settings);
}

public sealed class FakeLoc : ILocalizationService
{
    public Language CurrentLanguage { get; set; } = Language.English;
    public void SetLanguage(Language lang) => CurrentLanguage = lang;
    public string GetString(string key) => Application.Current.Resources[key] as string ?? key;
    public event EventHandler? LanguageChanged { add { } remove { } }
}

// Windows 11 that already has the switch on, changed after the last boot:
// the System settings panel renders its "restart pending" state. Writes
// are accepted and go nowhere.
public sealed class FakeGlobalTimer : IGlobalTimerSetting
{
    public bool IsSupported => true;
    public bool IsEnabled { get; private set; } = true;
    public bool TrySetEnabled(bool enabled) { IsEnabled = enabled; return true; }
    public DateTime LastBootUtc => DateTime.UtcNow.AddHours(-1);
}

public static class Program
{
    static T Null<T>() where T : class => DispatchProxy.Create<T, NullProxy>();
    static Uri Pack(string rel) => new($"pack://application:,,,/StandbyAndTimer;component/{rel}");
    static readonly List<string> Report = [];
    static void Log(string s) { Report.Add(s); Console.WriteLine(s); }

    static void SetField(object o, string name, object? value)
    {
        var f = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(o.GetType().Name, name);
        f.SetValue(o, value);
    }

    static void Pump()
    {
        for (int i = 0; i < 3; i++)
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, () => { });
    }

    static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var d in Walk(VisualTreeHelper.GetChild(root, i))) yield return d;
    }

    static string Describe(FrameworkElement fe)
    {
        var parts = new List<string>();
        for (DependencyObject? d = fe; d is not null && parts.Count < 4; d = VisualTreeHelper.GetParent(d))
            if (d is UserControl uc) { parts.Add(uc.GetType().Name); break; }
        return $"{fe.GetType().Name}{(string.IsNullOrEmpty(fe.Name) ? "" : "#" + fe.Name)} in {string.Join("/", parts)}";
    }

    // Text that does not fit its box (NoWrap → clipped; "…"-trimming is set
    // on purpose and not counted), panels whose content wants more height
    // than they got, and anything drawn outside its card. Returns the count,
    // which goes into the exit code.
    static int CheckLayout(FrameworkElement root, string tag)
    {
        int issues = 0;
        double dpi = VisualTreeHelper.GetDpi(root).PixelsPerDip;
        foreach (var d in Walk(root))
        {
            if (d is TextBlock tb && tb.IsVisible && !string.IsNullOrEmpty(tb.Text)
                && tb.TextWrapping == TextWrapping.NoWrap && tb.TextTrimming == TextTrimming.None
                && tb.ActualWidth > 0)
            {
                var ft = new FormattedText(tb.Text, CultureInfo.CurrentUICulture, tb.FlowDirection,
                    new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
                    tb.FontSize, Brushes.Black, dpi);
                double need = ft.WidthIncludingTrailingWhitespace + tb.Padding.Left + tb.Padding.Right;
                if (need > tb.ActualWidth + 0.5)
                {
                    issues++;
                    Log($"  FAIL [{tag}] TEXT DOES NOT FIT ({need:F0} > {tb.ActualWidth:F0} px): \"{tb.Text}\" — {Describe(tb)}");
                }
            }
            // Element rendered wider/taller than the slot its parent gave it:
            // WPF keeps the desired size and clips (or overdraws siblings),
            // so ActualWidth alone never shows the overflow.
            if (d is FrameworkElement fe && fe.IsVisible && fe.ActualWidth > 0
                && VisualTreeHelper.GetParent(fe) is UIElement parent
                && !IsInsideScrollContent(fe))
            {
                var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(fe);
                if (!slot.IsEmpty && slot.Width > 0)
                {
                    var m = fe.Margin;
                    double availW = slot.Width - m.Left - m.Right;
                    double availH = slot.Height - m.Top - m.Bottom;
                    if (fe.ActualWidth > availW + 0.5 || fe.ActualHeight > availH + 0.5)
                    {
                        issues++;
                        string what = fe is TextBlock t ? $" \"{t.Text}\"" : "";
                        Log($"  FAIL [{tag}] EXCEEDS SLOT ({fe.ActualWidth:F0}x{fe.ActualHeight:F0} in {availW:F0}x{availH:F0}){what} — {Describe(fe)}");
                    }
                }
            }
            if (d is Panel p && p.IsVisible && p.ActualHeight > 0)
            {
                double want = p.DesiredSize.Height - p.Margin.Top - p.Margin.Bottom;
                if (want > p.ActualHeight + 0.5 && p is not VirtualizingPanel)
                {
                    issues++;
                    Log($"  FAIL [{tag}] PANEL CLIPPED VERTICALLY (wants {want:F0}, has {p.ActualHeight:F0} px) — {Describe(p)}");
                }
            }
        }
        issues += CheckInsideCards(root, tag);
        if (issues == 0) Log($"  [{tag}] layout OK: no clipped text or panels, nothing outside its card");
        return issues;
    }

    // A panel squeezed by its row keeps arranging children at their full
    // size, and WPF clips DesiredSize to the space offered, so the checks
    // above cannot see it (the Hotkeys card cut off at the window bottom
    // passed them). Instead: every visible element must lie within the
    // nearest bordered ancestor (a card / dialog) on screen.
    static int CheckInsideCards(FrameworkElement root, string tag)
    {
        int issues = 0;
        foreach (var fe in Walk(root).OfType<FrameworkElement>())
        {
            if (!fe.IsVisible || fe.ActualHeight <= 0 || fe.ActualWidth <= 0 || IsInsideScrollContent(fe)) continue;
            Border? card = null;
            for (var a = VisualTreeHelper.GetParent(fe); a is not null; a = VisualTreeHelper.GetParent(a))
                if (a is Border { BorderThickness.Top: > 0 } b && b.ActualHeight > 0) { card = b; break; }
            if (card is null) continue;

            var tl = fe.TranslatePoint(new Point(0, 0), card);
            bool outside = tl.Y + fe.ActualHeight > card.ActualHeight + 0.5 || tl.X + fe.ActualWidth > card.ActualWidth + 0.5
                        || tl.Y < -0.5 || tl.X < -0.5;
            if (!outside) continue;
            issues++;
            Log($"  FAIL [{tag}] OUTSIDE CARD ({tl.X:F0},{tl.Y:F0} {fe.ActualWidth:F0}x{fe.ActualHeight:F0} in card {card.ActualWidth:F0}x{card.ActualHeight:F0}) — {Describe(fe)}");
            break;   // the first one names the problem; its children would repeat it
        }
        return issues;
    }

    // Vertical centre of the visible ink of an element, in relativeTo's
    // coordinates; null when blank. relativeTo is rendered at 4x on its
    // background (AppBg) and the element's rectangle is scanned for pixels
    // that differ from it, so a quarter-pixel offset shows.
    static double? InkCentreY(FrameworkElement el, FrameworkElement relativeTo)
    {
        const double k = 4;
        int w = (int)Math.Ceiling(relativeTo.ActualWidth * k), h = (int)Math.Ceiling(relativeTo.ActualHeight * k);
        var bgBrush = (SolidColorBrush)Application.Current.Resources["AppBg"];
        var area = new Rect(0, 0, relativeTo.ActualWidth, relativeTo.ActualHeight);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(bgBrush, null, area);
            dc.DrawRectangle(new VisualBrush(relativeTo) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, area);
        }
        var bmp = new RenderTargetBitmap(w, h, 96 * k, 96 * k, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var px = new byte[w * h * 4];
        bmp.CopyPixels(px, w * 4, 0);

        var o = el.TranslatePoint(new Point(0, 0), relativeTo);
        int x0 = (int)(o.X * k), x1 = Math.Min(w, (int)((o.X + el.ActualWidth) * k));
        int y0 = (int)(o.Y * k), y1 = Math.Min(h, (int)((o.Y + el.ActualHeight) * k));
        var bg = bgBrush.Color;
        int top = -1, bottom = -1;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * w + x) * 4;   // BGRA
                int d = Math.Abs(px[i] - bg.B) + Math.Abs(px[i + 1] - bg.G) + Math.Abs(px[i + 2] - bg.R);
                if (d > 60) { if (top < 0) top = y; bottom = y; break; }
            }
        return top < 0 ? null : (top + bottom + 1) / 2.0 / k;
    }

    // Content of a ScrollViewer is allowed to be larger than its viewport.
    static bool IsInsideScrollContent(DependencyObject d)
    {
        for (var p = VisualTreeHelper.GetParent(d); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is ScrollContentPresenter) return true;
        return false;
    }

    // 4x render (384 dpi) of one element, for pixel-level inspection.
    static void SaveZoomed(FrameworkElement fe, string path, double k = 4)
    {
        // RenderTargetBitmap draws a nested element at its offset inside the
        // parent; go through a VisualBrush so it lands at (0,0). Paint the
        // page background underneath, as the real window does.
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var r = new Rect(0, 0, fe.ActualWidth, fe.ActualHeight);
            dc.DrawRectangle((Brush)Application.Current.Resources["AppBg"], null, r);
            dc.DrawRectangle(new VisualBrush(fe) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, r);
        }
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(fe.ActualWidth * k), (int)Math.Ceiling(fe.ActualHeight * k), 96 * k, 96 * k, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    static void Save(FrameworkElement fe, string path)
    {
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(fe.ActualWidth), (int)Math.Ceiling(fe.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(fe);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
        Log($"  saved {Path.GetFileName(path)} ({bmp.PixelWidth}x{bmp.PixelHeight})");
    }

    [STAThread]
    public static int Main(string[] args)
    {
        string outDir = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "ui-render-out";
        Directory.CreateDirectory(outDir);
        // The views drive real view models: their log lines (hotkeys rebound,
        // games added...) go next to the renders, not into the user's app log.
        Environment.SetEnvironmentVariable("STANDBYANDTIMER_LOG_DIR", Path.GetFullPath(Path.Combine(outDir, "log")));
        int failures = 0;

        // Root-relative pack URIs ("/app_icon.ico") resolve against the
        // resource assembly, which would otherwise be this renderer.
        // The public setter is one-shot and already locked to the entry
        // assembly, so swap the backing field (dev tool only).
        var raField = typeof(Application).GetField("_resourceAssembly", BindingFlags.Static | BindingFlags.NonPublic)
                      ?? throw new MissingFieldException("Application", "_resourceAssembly");
        raField.SetValue(null, typeof(MainWindow).Assembly);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var md = app.Resources.MergedDictionaries;
        md.Add(new ResourceDictionary { Source = Pack("Views/Styles/Theme.xaml") });
        md.Add(new ResourceDictionary { Source = Pack("Views/Styles/Palette.Dark.xaml") });
        md.Add(new ResourceDictionary { Source = Pack("Views/Strings/Strings.en-US.xaml") });

        var loc = new FakeLoc();
        var settingsVm = new SettingsViewModel(Null<IAutoStartService>(), loc, Null<ISettingsService>(),
                                               Null<IUpdateService>(), Null<IThemeService>(), Null<ILogTailReader>());
        var systemVm = new SystemViewModel(new FakeGlobalTimer());
        systemVm.Initialize(promptAnswered: true, setByApp: true, changedUtc: DateTime.UtcNow, bootValue: false);
        var vm = new MainViewModel(Null<ITimerResolutionService>(), Null<IStandbyPurgeService>(),
                                   Null<IMemoryMonitorService>(), Null<ISettingsService>(), loc,
                                   Null<IGameDetectionService>(), settingsVm, systemVm);

        // Sample data written straight to the backing fields: no OnXChanged
        // partials run, so nothing tries to persist.
        SetField(vm, "_totalRamMb", 16087L);
        SetField(vm, "_freeRamMb", 753L);
        SetField(vm, "_standbyRamMb", 2867L);
        // Purge history sample (oldest first; RecordPurge puts each on top),
        // spread over days so the totals and the date column show.
        typeof(MainViewModel).GetProperty("Now", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, (Func<DateTime>)(() => new DateTime(2026, 10, 14, 22, 0, 0)));
        foreach (var (at, kind, trigger, mb) in new[] {
            (new DateTime(2026, 10, 2, 18, 30, 0),  PurgeKind.Normal, PurgeTrigger.Auto,      2210L),
            (new DateTime(2026, 10, 11, 23, 4, 12), PurgeKind.Deep,   PurgeTrigger.Manual,    3550L),
            (new DateTime(2026, 10, 14, 19, 2, 31), PurgeKind.Light,  PurgeTrigger.Manual,      12L),
            (new DateTime(2026, 10, 14, 20, 15, 48), PurgeKind.Normal, PurgeTrigger.Auto,     1843L),
            (new DateTime(2026, 10, 14, 21, 42, 5), PurgeKind.Deep,   PurgeTrigger.GameStart, 3124L) })
            typeof(MainViewModel).GetMethod("RecordPurge", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, [new PurgeResult(at, kind, trigger, mb)]);
        SetField(vm, "_purgeCount", 3);
        SetField(vm, "_clearableMb", 2867L);
        SetField(vm, "_standbyLimitMb", 60);
        SetField(vm, "_freeLimitMb", 60);
        SetField(vm, "_autoPurgeEnabled", false);
        SetField(vm, "_actualTimerMs", 0.5);
        SetField(vm, "_timerActive", true);
        SetField(vm, "_gameModeEnabled", true);
        vm.Games.Add(new GameEntry { DisplayName = "cs2.exe",      ExecutablePath = @"C:\Windows\System32\notepad.exe" });
        vm.Games.Add(new GameEntry { DisplayName = "VALORANT", ExecutablePath = @"C:\Windows\explorer.exe", AutoAdded = true, BoostPriority = false });
        vm.Games.Add(new GameEntry { DisplayName = "EldenRingNightreignWithAVeryLongName", ExecutablePath = @"C:\Windows\regedit.exe", AutoAdded = true });
        SetField(settingsVm, "_autoStartEnabled", true);

        // Multi-line strings must keep their line breaks after XAML loading.
        foreach (var lang in new[] { "en-US", "tr-TR" })
        {
            md[2] = new ResourceDictionary { Source = Pack($"Views/Strings/Strings.{lang}.xaml") };
            vm.GetType().GetMethod("RelabelPurgeHistory", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);   // history rows in this language
            foreach (var key in new[] { "Str_Wizard_Body", "Str_Tray_TooltipFormat" })
            {
                string v = loc.GetString(key);
                bool ok = v.Contains('\n');
                if (!ok) failures++;
                Log($"  {(ok ? "OK  " : "FAIL")} [{lang}] {key} keeps line breaks ({v.Count(ch => ch == '\n')} LF)");
            }
        }
        md[2] = new ResourceDictionary { Source = Pack("Views/Strings/Strings.en-US.xaml") };

        // ── Hotkey rebinding: logic checks with fake register/unregister ──
        // No real RegisterHotKey: the delegates only record calls. A combo in
        // `taken` is refused, like one owned by another application.
        var regLog = new List<string>();
        var taken  = new HashSet<string> { "Ctrl+Alt+K" };
        void SetInternal(string prop, object value) =>
            typeof(SettingsViewModel).GetProperty(prop, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(settingsVm, value);
        SetInternal("RegisterHotkey", new Func<string, string, bool>((id, text) => { regLog.Add($"reg {id}={text}"); return !taken.Contains(text); }));
        SetInternal("UnregisterHotkey", new Action<string>(id => regLog.Add($"unreg {id}")));
        int settingsChanged = 0;
        settingsVm.SettingsChanged += (_, _) => settingsChanged++;
        void Check(bool ok, string what) { if (!ok) failures++; Log($"  {(ok ? "OK  " : "FAIL")} hotkey: {what}"); }
        const uint CTRL = 2, ALT = 1, SHIFT = 4;
        {
            Log("== hotkey logic");
            // Format / Parse round trip
            foreach (var s in new[] { "Ctrl+Alt+P", "Shift+F9", "Ctrl+Shift+Win+0", "Alt+F24" })
                Check(HotkeyBinding.Parse(s)!.Format() == s, $"round trip {s} -> {HotkeyBinding.Parse(s)!.Format()}");
            Check(new HotkeyBinding(0, 'P').Format() is null, "no modifier formats to null");
            Check(new HotkeyBinding(CTRL, 0x09).Format() is null, "Tab (0x09) formats to null");

            settingsVm.StartHotkeyRecordingCommand.Execute("purge");
            Check(settingsVm.IsRecordingPurge && regLog.SequenceEqual(new[] { "unreg purge", "unreg timer", "unreg autopurge" }), $"start suspends all three [{string.Join(", ", regLog)}]");
            regLog.Clear();

            settingsVm.CompleteHotkeyRecording(0, 'P');
            Check(settingsVm.IsRecordingPurge && settingsVm.HotkeyError == loc.GetString("Str_Settings_HotkeyNeedsModifier"), "no modifier -> error, still recording");
            settingsVm.CompleteHotkeyRecording(CTRL | ALT, 0x09);
            Check(settingsVm.IsRecordingPurge && settingsVm.HotkeyError == loc.GetString("Str_Settings_HotkeyUnsupported"), "unsupported key -> error, still recording");
            settingsVm.CompleteHotkeyRecording(CTRL | ALT, 'T');
            Check(settingsVm.IsRecordingPurge && settingsVm.HotkeyError == loc.GetString("Str_Settings_HotkeyDuplicate"), "duplicate of timer -> error, still recording");
            Check(regLog.Count == 0 && settingsChanged == 0, "rejected combos register/persist nothing");

            settingsVm.CompleteHotkeyRecording(CTRL | SHIFT, 0x74); // Ctrl+Shift+F5
            Check(!settingsVm.IsRecordingPurge && settingsVm.PurgeHotkeyText == "Ctrl+Shift+F5" && settingsVm.HotkeyError == "", $"rebind purge -> {settingsVm.PurgeHotkeyText}");
            Check(settingsChanged == 1, $"rebind raises SettingsChanged once (got {settingsChanged})");
            Check(regLog.Contains("reg purge=Ctrl+Shift+F5") && regLog.Contains("reg timer=Ctrl+Alt+T"), $"both re-registered [{string.Join(", ", regLog)}]");
            regLog.Clear(); settingsChanged = 0;

            settingsVm.StartHotkeyRecordingCommand.Execute("timer");
            settingsVm.CompleteHotkeyRecording(CTRL | ALT, 'K');   // refused by "another app"
            Check(settingsVm.TimerHotkeyText == "Ctrl+Alt+T" && settingsVm.HotkeyError == loc.GetString("Str_Settings_HotkeyTaken") && settingsChanged == 0,
                  $"refused combo keeps Ctrl+Alt+T, shows error, no persist");
            Check(regLog.IndexOf("reg timer=Ctrl+Alt+T") > regLog.IndexOf("reg timer=Ctrl+Alt+K"), $"previous timer binding restored after the refusal [{string.Join(", ", regLog)}]");
            regLog.Clear();

            settingsVm.IsOpen = true;
            settingsVm.StartHotkeyRecordingCommand.Execute("timer");
            settingsVm.IsOpen = false;                              // leave the Settings tab
            Check(settingsVm.RecordingHotkeyId is null && regLog.Contains("reg purge=Ctrl+Shift+F5") && regLog.Contains("reg timer=Ctrl+Alt+T"),
                  $"leaving tab cancels + re-registers [{string.Join(", ", regLog)}]");

            // Third hotkey: duplicate detection covers it, and rebinding it works.
            settingsVm.StartHotkeyRecordingCommand.Execute("autopurge");
            Check(settingsVm.IsRecordingAutoPurge, "autopurge recording starts");
            settingsVm.CompleteHotkeyRecording(CTRL | SHIFT, 0x74);   // = purge's Ctrl+Shift+F5
            Check(settingsVm.IsRecordingAutoPurge && settingsVm.HotkeyError == loc.GetString("Str_Settings_HotkeyDuplicate"), "autopurge: purge's combo rejected as duplicate");
            regLog.Clear();
            settingsVm.CompleteHotkeyRecording(CTRL | ALT, 'U');
            Check(settingsVm.AutoPurgeHotkeyText == "Ctrl+Alt+U" && regLog.Contains("reg autopurge=Ctrl+Alt+U"), $"autopurge rebound -> {settingsVm.AutoPurgeHotkeyText}");
            SetField(settingsVm, "_autoPurgeHotkeyText", "Ctrl+Alt+O");

            // The hotkey's command really flips AUTO PURGE (and back).
            bool before = vm.AutoPurgeEnabled;
            vm.ToggleAutoPurgeCommand.Execute(null);
            Check(vm.AutoPurgeEnabled == !before && vm.StatusMessage == loc.GetString(before ? "Str_Status_AutoPurgeOff" : "Str_Status_AutoPurgeOn"),
                  $"ToggleAutoPurge flips {before} -> {vm.AutoPurgeEnabled}, status \"{vm.StatusMessage}\"");
            vm.ToggleAutoPurgeCommand.Execute(null);
            Check(vm.AutoPurgeEnabled == before, "ToggleAutoPurge flips back");

            // Import re-registers hotkeys (separate VM pair: ApplySettings
            // would otherwise wipe the screenshot sample data).
            {
                var sv2 = new SettingsViewModel(Null<IAutoStartService>(), loc, Null<ISettingsService>(),
                                                Null<IUpdateService>(), Null<IThemeService>(), Null<ILogTailReader>());
                _ = new MainViewModel(Null<ITimerResolutionService>(), Null<IStandbyPurgeService>(),
                                      Null<IMemoryMonitorService>(), Null<ISettingsService>(), loc,
                                      Null<IGameDetectionService>(), sv2);
                var log2 = new List<string>();
                typeof(SettingsViewModel).GetProperty("RegisterHotkey", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(sv2, new Func<string, string, bool>((id, text) => { log2.Add($"reg {id}={text}"); return !taken.Contains(text); }));
                typeof(SettingsViewModel).GetProperty("UnregisterHotkey", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(sv2, new Action<string>(id => log2.Add($"unreg {id}")));
                var apply = typeof(SettingsViewModel).GetMethod("ApplyImportedSettings", BindingFlags.Instance | BindingFlags.NonPublic)!;

                apply.Invoke(sv2, [new AppSettings { PurgeHotkey = "Ctrl+Alt+F9", TimerHotkey = "Shift+F8" }]);
                Check(sv2.PurgeHotkeyText == "Ctrl+Alt+F9" && log2.Contains("reg purge=Ctrl+Alt+F9") && log2.Contains("reg timer=Shift+F8") && log2.Contains("reg autopurge=Ctrl+Alt+O") && sv2.HotkeyError == "",
                      $"import re-registers both [{string.Join(", ", log2)}]");
                log2.Clear();
                apply.Invoke(sv2, [new AppSettings { PurgeHotkey = "Ctrl+Alt+P", TimerHotkey = "Ctrl+Alt+K" }]);   // K is "taken"
                Check(sv2.HotkeyError == loc.GetString("Str_Settings_HotkeyTaken") && log2.Contains("reg timer=Ctrl+Alt+K"),
                      $"import with a taken combo reports it [{string.Join(", ", log2)}]");
            }

            // ── Game Mode auto-add ──
            {
                Log("== game mode auto-add");
                var detType = typeof(MainWindow).Assembly.GetType("StandbyAndTimer.Services.GameDetectionService")!;
                var det = Activator.CreateInstance(detType, nonPublic: true)!;
                var should = detType.GetMethod("ShouldReport", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var notFs  = detType.GetMethod("OnNotFullscreen", BindingFlags.Instance | BindingFlags.NonPublic)!;
                bool S(string path, string proc) => (bool)should.Invoke(det, [path, proc])!;
                string game = @"C:\Games\Hades\Hades.exe";
                Check(!S(game, "Hades"), "detect: first fullscreen poll waits");
                Check(S(game, "Hades"), "detect: second consecutive poll reports");
                Check(!S(game, "Hades") && !S(game, "Hades"), "detect: same exe never reported twice per session");
                string g2 = @"C:\Games\Other\Other.exe";
                Check(!S(g2, "Other"), "detect: g2 first poll");
                notFs.Invoke(det, null);
                Check(!S(g2, "Other"), "detect: a non-fullscreen poll in between resets the count");
                Check(!S(@"C:\Users\x\Discord.exe", "Discord") && !S(@"C:\Users\x\Discord.exe", "Discord"), "detect: Discord ignored");
                Check(!S(@"C:\W\SnippingTool.exe", "SnippingTool") && !S(@"C:\W\SnippingTool.exe", "SnippingTool"), "detect: SnippingTool ignored");
                Check(!S(@"C:\P\chrome.exe", "chrome") && !S(@"C:\P\chrome.exe", "chrome"), "detect: browser ignored");

                // ViewModel side, on a separate VM pair (keeps the screenshot data)
                var savedSettings = new List<AppSettings>();
                var svc = DispatchProxy.Create<ISettingsService, NullProxy>();
                var sv3 = new SettingsViewModel(Null<IAutoStartService>(), loc, svc, Null<IUpdateService>(), Null<IThemeService>(), Null<ILogTailReader>());
                var detFake = Null<IGameDetectionService>();
                var vm3 = new MainViewModel(Null<ITimerResolutionService>(), Null<IStandbyPurgeService>(), Null<IMemoryMonitorService>(),
                                            new RecordingSettings(savedSettings), loc, detFake, sv3);
                var auto = typeof(MainViewModel).GetMethod("AutoAddDetectedGame", BindingFlags.Instance | BindingFlags.NonPublic)!;
                string realExe = @"C:\Windows\System32\notepad.exe";   // must exist: AddGameFromPath checks File.Exists
                auto.Invoke(vm3, [realExe]);
                Check(vm3.Games.Count == 1 && vm3.Games[0].AutoAdded && vm3.Games[0].DisplayName == "notepad", "vm: detected exe added with AutoAdded=true");
                Check(savedSettings.LastOrDefault()?.Games.Any(g => g.AutoAdded && g.ExecutablePath == realExe) == true, "vm: auto-add persisted (AutoAdded kept in the saved snapshot)");
                auto.Invoke(vm3, [realExe]);
                Check(vm3.Games.Count == 1, "vm: re-detection does not duplicate");

                vm3.SelectedGame = vm3.Games[0];
                vm3.RemoveGameCommand.Execute(null);
                Check(vm3.Games.Count == 0 && savedSettings.Last().IgnoredGamePaths.Contains(realExe), "vm: removing an auto-added game dismisses it (persisted)");
                auto.Invoke(vm3, [realExe]);
                Check(vm3.Games.Count == 0, "vm: dismissed exe is not auto-added again");

                vm3.AddGameFromPath(realExe);
                Check(vm3.Games.Count == 1 && !vm3.Games[0].AutoAdded && !savedSettings.Last().IgnoredGamePaths.Contains(realExe),
                      "vm: manual add works and clears the dismissal");
                vm3.SelectedGame = vm3.Games[0];
                vm3.RemoveGameCommand.Execute(null);
                Check(!savedSettings.Last().IgnoredGamePaths.Contains(realExe), "vm: removing a user-added game does not dismiss it");
            }

            // ── Timer start-up + lock ──
            {
                Log("== timer start-up + lock");
                (MainViewModel vm, FakeTimer timer, List<AppSettings> saved) Make(AppSettings s)
                {
                    var saved = new List<AppSettings>();
                    var store = new FixedSettings(s, saved);
                    var sv = new SettingsViewModel(Null<IAutoStartService>(), loc, store, Null<IUpdateService>(), Null<IThemeService>(), Null<ILogTailReader>());
                    var timer = new FakeTimer();
                    var m = new MainViewModel(timer, Null<IStandbyPurgeService>(), Null<IMemoryMonitorService>(), store, loc, Null<IGameDetectionService>(), sv);
                    return (m, timer, saved);
                }
                string ready = loc.GetString("Str_Status_Ready");
                string activePrefix = loc.GetString("Str_Status_TimerActive").Split('{')[0];

                // Old behaviour restored the last session's state: left ON once -> ON every launch.
                var (a, ta, _) = Make(new AppSettings { TimerResolutionActive = true, TimerLocked = false, FirstRunCompleted = true });
                a.InitializeAsync().GetAwaiter().GetResult();
                Check(!a.TimerActive && ta.Activations == 0 && a.StatusMessage == ready,
                      $"unlocked start: timer stays off even if last session left it on (active={a.TimerActive}, activations={ta.Activations}, status=\"{a.StatusMessage}\")");

                var (b, tb, savedB) = Make(new AppSettings { TimerLocked = true, FirstRunCompleted = true });
                b.InitializeAsync().GetAwaiter().GetResult();
                Check(b.TimerActive && tb.Activations == 1 && b.StatusMessage.StartsWith(activePrefix),
                      $"locked start: timer on, status shows the timer, not Ready (\"{b.StatusMessage}\")");

                // While locked, the TIMER button still turns it off for this session; the lock stays.
                b.ToggleTimerResolutionCommand.Execute(null);
                Check(!b.TimerActive && (b.TimerLock != TimerLockMode.Unlocked) && tb.Deactivations == 1 && savedB.Last().TimerLocked,
                      "locked: TIMER button turns the timer off, lock stays on (persisted)");

                // Unlock while running: keeps running, next start is off.
                b.ToggleTimerResolutionCommand.Execute(null);   // on again
                b.ToggleTimerLockCommand.Execute(null);   // -> IN GAME
                b.ToggleTimerLockCommand.Execute(null);   // -> UNLOCKED
                Check(b.TimerActive && !(b.TimerLock != TimerLockMode.Unlocked) && !savedB.Last().TimerLocked && b.StatusMessage == loc.GetString("Str_Status_TimerLockedOff"),
                      "unlock while running: timer keeps running, lock off persisted");

                // Lock while off: switches the timer on now.
                var (c, tc, savedC) = Make(new AppSettings { FirstRunCompleted = true });
                c.InitializeAsync().GetAwaiter().GetResult();
                c.ToggleTimerLockCommand.Execute(null);
                Check((c.TimerLock != TimerLockMode.Unlocked) && c.TimerActive && tc.Activations == 1 && savedC.Last().TimerLocked && c.StatusMessage == loc.GetString("Str_Status_TimerLockedOn"),
                      "lock while off: timer turns on now, lock persisted");

                // Import must not mark the timer active without activating it.
                var (d, td, _) = Make(new AppSettings { FirstRunCompleted = true });
                d.InitializeAsync().GetAwaiter().GetResult();
                typeof(SettingsViewModel).GetMethod("ApplyImportedSettings", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(d.Settings, [new AppSettings { TimerResolutionActive = true, TimerLocked = true }]);
                Check(!d.TimerActive && td.Activations == 0 && d.TimerLock == TimerLockMode.Locked,
                      "import: TimerLocked applied, timer not shown active without activation");
            }

            // Restore defaults for the screenshots below (fields: no persist).
            SetField(settingsVm, "_purgeHotkeyText", "Ctrl+Alt+P");
            SetField(settingsVm, "_hotkeyError", string.Empty);
        }

        foreach (var (theme, themeEnum) in new[] { ("Dark", Theme.Dark), ("Black", Theme.Black), ("Light", Theme.Light), ("White", Theme.White) })
        foreach (var (lang, langEnum) in new[] { ("en-US", Language.English), ("tr-TR", Language.Turkish) })
        {
            string tag = $"{theme}-{lang}";
            Log($"== {tag}");
            vm.SelectedGame = theme is "Dark" or "White" ? vm.Games[1] : null;   // both strip states
            md[1] = new ResourceDictionary { Source = Pack($"Views/Styles/Palette.{theme}.xaml") };
            md[2] = new ResourceDictionary { Source = Pack($"Views/Strings/Strings.{lang}.xaml") };
            loc.SetLanguage(langEnum);
            vm.GetType().GetMethod("RelabelPurgeHistory", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);   // rows + dates in this language
            SetField(settingsVm, "_selectedTheme", themeEnum);
            SetField(settingsVm, "_selectedLanguage", langEnum);
            // Idle-off picker: Custom (minutes box shown) in two themes, Off in the others.
            SetField(settingsVm, "_selectedIdleOffOption", theme is "Black" or "Light" ? SettingsViewModel.IdleOffCustom : 0);
            SetField(settingsVm, "_timerIdleOffMinutes", 25);
            SetField(settingsVm, "_isOpen", false);
            SetField(vm, "_statusMessage", loc.GetString("Str_Status_Ready"));
            SetField(vm, "_timerLock", theme is "Light" or "Black" ? TimerLockMode.InGame : TimerLockMode.Unlocked);   // show two lock states

            var w = new MainWindow(vm)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -30000, Top = -30000, ShowInTaskbar = false, ShowActivated = false,
            };
            w.Show();
            Pump();
            var root = (FrameworkElement)w.Content;
            var tabs = Walk(root).OfType<TabControl>().Single();

            // Real on-screen capture (hardware render path, through DWM): the
            // offscreen RenderTargetBitmap path is software-only and cannot
            // show GPU edge/opacity-layer artifacts.
            // --docs: the same capture in Dark / English with tidy sample
            // data, written to docs/ for the README.
            bool docs = args.Contains("--docs") && tag == "Dark-en-US";
            if ((args.Contains("--screen") && tag == "Dark-tr-TR") || docs)
            {
                var keepGame = vm.Games[2];
                var keepLock = vm.TimerLock;
                if (docs)
                {
                    vm.Games[2] = new GameEntry { DisplayName = "eldenring", ExecutablePath = keepGame.ExecutablePath, AutoAdded = true };
                    SetField(vm, "_timerLock", TimerLockMode.Locked);
                    vm.GetType().GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string)])!
                        .Invoke(vm, [nameof(MainViewModel.TimerLock)]);
                }
                string Shot(string page) => docs
                    ? Path.Combine("docs", page == "window" ? "screenshot.png" : "screenshot-settings.png")
                    : Path.Combine(outDir, $"screen-{tag}-{page}.png");
                vm.SelectedGame = vm.Games[0];
                w.Topmost = true; w.Left = 40; w.Top = 40; w.Activate();
                // Wait with a running dispatcher (DispatcherFrame), so layout,
                // render and DWM composition really happen before capturing.
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);
                var tl = root.PointToScreen(new Point(0, 0));
                var br = root.PointToScreen(new Point(root.ActualWidth, root.ActualHeight));
                int cw = (int)(br.X - tl.X), ch = (int)(br.Y - tl.Y);
                using (var shot = new System.Drawing.Bitmap(cw, ch))
                {
                    using (var g = System.Drawing.Graphics.FromImage(shot))
                        g.CopyFromScreen((int)tl.X, (int)tl.Y, 0, 0, new System.Drawing.Size(cw, ch));
                    shot.Save(Shot("window"), System.Drawing.Imaging.ImageFormat.Png);
                }
                // Same capture for the Settings tab (log viewer, combos).
                settingsVm.IsOpen = true;
                var frame2 = new DispatcherFrame();
                var timer2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                timer2.Tick += (_, _) => { timer2.Stop(); frame2.Continue = false; };
                timer2.Start(); Dispatcher.PushFrame(frame2);
                using (var shot2 = new System.Drawing.Bitmap(cw, ch))
                {
                    using (var g2 = System.Drawing.Graphics.FromImage(shot2))
                        g2.CopyFromScreen((int)tl.X, (int)tl.Y, 0, 0, new System.Drawing.Size(cw, ch));
                    shot2.Save(Shot("settings"), System.Drawing.Imaging.ImageFormat.Png);
                }
                settingsVm.IsOpen = false; Pump();
                if (docs)
                {
                    vm.Games[2] = keepGame;
                    SetField(vm, "_timerLock", keepLock);
                    Log($"  saved docs/screenshot.png and docs/screenshot-settings.png");
                }

                w.Left = -30000; w.Top = -30000; w.Topmost = false;
                Pump();
            }

            Save(root, Path.Combine(outDir, $"{tag}-1-status.png"));

            // Lime (DarkButtonStyle) buttons must draw their label in
            // AccentOnText, not the app-level AppFg (white on lime in dark).
            void CheckAccentButtons(string where)
            {
                var dark = (Style)Application.Current.Resources["DarkButtonStyle"];
                var want = ((SolidColorBrush)Application.Current.Resources["AccentOnText"]).Color;
                var bad = Walk(root).OfType<Button>().Where(b => b.IsVisible && b.Style == dark)
                    .SelectMany(b => Walk(b).OfType<TextBlock>().Select(t => (b, t)))
                    .Where(x => x.t.Foreground is not SolidColorBrush s || s.Color != want).ToList();
                int n = Walk(root).OfType<Button>().Count(b => b.IsVisible && b.Style == dark);
                if (bad.Count > 0) failures++;
                Log($"  {(bad.Count == 0 ? "OK  " : "FAIL")} [{tag} {where}] {n} lime button(s) use AccentOnText" +
                    (bad.Count == 0 ? "" : ": " + string.Join(", ", bad.Select(x => $"\"{x.t.Text}\"={(x.t.Foreground as SolidColorBrush)?.Color}"))));
            }
            CheckAccentButtons("status");

            // Close confirmation ("Run in background") over the status page.
            {
                var close = Walk(root).OfType<StandbyAndTimer.Views.Overlays.CloseConfirmationOverlay>().Single();
                close.GetType().GetMethod("Show")!.Invoke(close, null);
                close.BeginAnimation(UIElement.OpacityProperty, null); close.Opacity = 1;   // skip the 160 ms fade-in
                Pump();
                Save(root, Path.Combine(outDir, $"{tag}-5-close.png"));
                CheckAccentButtons("close dialog");
                close.Visibility = Visibility.Collapsed;
                Pump();
            }

            // About dialog and first-run wizard over the status page.
            foreach (var (flag, prop, name) in new[] { ("_isAboutVisible", "IsAboutVisible", "about"), ("_isWizardVisible", "IsWizardVisible", "wizard") })
            {
                var raise = vm.GetType().GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string)])!;
                SetField(vm, flag, true);  raise.Invoke(vm, [prop]); Pump();
                Save(root, Path.Combine(outDir, $"{tag}-4-{name}.png"));
                // The guide scrolls: also render its end (thresholds table, last sections).
                if (name == "about")
                {
                    var sv = Walk(Walk(root).OfType<StandbyAndTimer.Views.Overlays.AboutOverlay>().Single()).OfType<ScrollViewer>().Single();
                    bool scrolls = sv.ScrollableHeight > 0;
                    if (!scrolls) { failures++; Log($"  FAIL [{tag}] about: guide does not scroll (ScrollableHeight=0)"); }
                    sv.ScrollToEnd(); Pump();
                    Save(root, Path.Combine(outDir, $"{tag}-4-about-end.png"));
                    sv.ScrollToHome(); Pump();
                }
                failures += CheckLayout(root, $"{tag} {name}");
                SetField(vm, flag, false); raise.Invoke(vm, [prop]); Pump();
            }
            // System settings panel (restart pending) and the start-up question.
            foreach (var (show, name) in new (Action<bool>, string)[] { (v => systemVm.IsOpen = v, "system"), (v => systemVm.IsPromptVisible = v, "prompt"), (v => vm.IsPurgeHistoryVisible = v, "history") })
            {
                show(true); Pump();
                Save(root, Path.Combine(outDir, $"{tag}-4-{name}.png"));
                failures += CheckLayout(root, $"{tag} {name}");
                show(false); Pump();
            }
            {
                var purge = Walk(root).OfType<StandbyAndTimer.Views.Cards.StandbyPurgeCard>().Single();
                var cb = Walk(purge).OfType<CheckBox>().First(c => c.Style == (Style)Application.Current.Resources["DarkCheckBoxStyle"]);
                var texts = Walk(cb).OfType<TextBlock>().ToList();
                string Hex(Brush b) => b is SolidColorBrush s ? $"#{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}" : b?.ToString() ?? "null";
                var muted = (SolidColorBrush)Application.Current.Resources["AppFgMuted"];
                Log($"  idle checkbox: IsEnabled={cb.IsEnabled} Foreground={Hex(cb.Foreground)} Opacity={cb.Opacity} textBlocks={texts.Count}");
                foreach (var tbk in texts)
                {
                    var src = DependencyPropertyHelper.GetValueSource(tbk, TextBlock.ForegroundProperty);
                    Log($"    TextBlock \"{tbk.Text}\" Foreground={Hex(tbk.Foreground)} source={src.BaseValueSource} parent={VisualTreeHelper.GetParent(tbk)?.GetType().Name}");
                }
                bool dimmed = !cb.IsEnabled && texts.All(x => x.Foreground is SolidColorBrush s && s.Color == muted.Color);
                if (!cb.IsEnabled && !dimmed) { failures++; Log("  FAIL disabled idle checkbox label is not AppFgMuted"); }
            }
            SaveZoomed(Walk(root).OfType<StandbyAndTimer.Views.Cards.TitleBar>().Single(), Path.Combine(outDir, $"{tag}-titlebar-x4.png"));
            SaveZoomed(Walk(root).OfType<StandbyAndTimer.Views.Cards.StandbyPurgeCard>().Single(),
                       Path.Combine(outDir, $"{tag}-purge-x4.png"));
            SaveZoomed(Walk(root).OfType<StandbyAndTimer.Views.Cards.StandbyPurgeCard>().Single(),
                       Path.Combine(outDir, $"{tag}-purge-x1.png"), 1);
            failures += CheckLayout(root, tag + " status");
            {
                // Title bar: the admin badge text and the ?, — and ✕ glyphs
                // must sit on one line, the bar's centre (within 1 px).
                // Measured on the ink: layout centres a glyph's em box, not
                // its visible strokes (they sat ~2 px low next to the badge).
                var bar = Walk(root).OfType<StandbyAndTimer.Views.Cards.TitleBar>().Single();
                var centres = new List<(string what, double y)>();
                var badgeText = Walk(bar).OfType<TextBlock>().Single(t => t.Text == loc.GetString("Str_TitleBar_Admin"));
                centres.Add(("badge", InkCentreY(badgeText, bar) ?? double.NaN));
                foreach (var b in Walk(bar).OfType<Button>().Where(b => b.Width == 36))
                    centres.Add(($"\"{b.Content}\"", InkCentreY(b, bar) ?? double.NaN));
                double barMid = bar.ActualHeight / 2;
                bool ok = centres.Count == 4 && centres.All(c => Math.Abs(c.y - barMid) <= 1.0);
                if (!ok) failures++;
                Log($"  {(ok ? "OK  " : "FAIL")} [{tag}] title bar on one line (bar centre {barMid:F1}): " +
                    string.Join(", ", centres.Select(c => $"{c.what}={c.y:F1}")));
            }
            {
                // Left column: the Timer and Standby memory cards are the same height.
                double th = Walk(root).OfType<StandbyAndTimer.Views.Cards.TimerCard>().Single().ActualHeight;
                double mh = Walk(root).OfType<StandbyAndTimer.Views.Cards.MemoryStatsCard>().Single().ActualHeight;
                bool ok = Math.Abs(th - mh) < 0.5;
                if (!ok) failures++;
                Log($"  {(ok ? "OK  " : "FAIL")} [{tag}] timer / memory cards same height: {th:F0} / {mh:F0}");
            }

            // VM → View: IsOpen=true must select the Settings tab.
            settingsVm.IsOpen = true;
            Pump();
            if (tabs.SelectedIndex != 1) { failures++; Log($"  FAIL IsOpen=true → SelectedIndex={tabs.SelectedIndex}, expected 1"); }
            else Log("  OK   IsOpen=true selects Settings tab");
            Save(root, Path.Combine(outDir, $"{tag}-2-settings.png"));
            failures += CheckLayout(root, tag + " settings");
            {
                // Cards sharing a grid row must share their top and bottom edges.
                var page = Walk(root).OfType<StandbyAndTimer.Views.SettingsPanelView>().Single();
                var grid = (Grid)page.Content;
                var cards = grid.Children.OfType<FrameworkElement>()
                    .Where(e => e is Border || e is StandbyAndTimer.Views.Cards.LogsViewerCard).ToList();
                foreach (var row in cards.GroupBy(Grid.GetRow))
                {
                    var r = row.Select(e => { var o = e.TranslatePoint(new Point(0, 0), page); return (top: o.Y, bottom: o.Y + e.ActualHeight); }).ToList();
                    bool same = r.Count == 2 && Math.Abs(r[0].top - r[1].top) < 0.5 && Math.Abs(r[0].bottom - r[1].bottom) < 0.5;
                    if (!same) failures++;
                    Log($"  {(same ? "OK  " : "FAIL")} settings row {row.Key}: " + string.Join(" | ", r.Select(x => $"{x.top:F0}-{x.bottom:F0}")));
                }
            }

            // Recording state + the longest error message, for layout.
            settingsVm.StartHotkeyRecordingCommand.Execute("purge");
            // Worst case at once: longest hotkey error, a long status line and
            // the Download & Install button visible.
            SetField(settingsVm, "_hotkeyError", loc.GetString("Str_Settings_HotkeyUnsupported"));
            SetField(settingsVm, "_updateStatus", loc.GetString("Str_Settings_AutoStartFailedOn"));
            SetField(settingsVm, "_updateAvailable", true);
            foreach (var prop in new[] { nameof(SettingsViewModel.HotkeyError), nameof(SettingsViewModel.UpdateStatus), nameof(SettingsViewModel.UpdateAvailable) })
                settingsVm.GetType().GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string)])!.Invoke(settingsVm, [prop]);
            Pump();
            {
                var pills = Walk(root).OfType<Button>().Where(b => b.Style == (Style)Walk(root).OfType<StandbyAndTimer.Views.SettingsPanelView>().Single().Resources["HotkeyButtonStyle"]).ToList();
                bool PromptShown(Button b) => b.Template.FindName("prompt", b) is TextBlock p && p.Visibility == Visibility.Visible;
                bool ok = pills.Count == 3 && PromptShown(pills[0]) && !PromptShown(pills[1]) && !PromptShown(pills[2]);
                if (!ok) failures++;
                Log($"  {(ok ? "OK  " : "FAIL")} recording pill shows prompt (purge={(pills.Count > 0 && PromptShown(pills[0]))}, timer={(pills.Count > 1 && PromptShown(pills[1]))}, pills={pills.Count})");
                if (!ok)
                    Log($"       vm.RecordingHotkeyId={settingsVm.RecordingHotkeyId ?? "null"} IsRecordingPurge={settingsVm.IsRecordingPurge} " +
                        $"tags=[{string.Join(", ", pills.Select(b => $"{b.Tag?.GetType().Name}:{b.Tag}"))}]");
            }
            Save(root, Path.Combine(outDir, $"{tag}-3-hotkey-recording.png"));
            failures += CheckLayout(root, tag + " hotkey-recording");

            // Code-behind wiring: synthetic key events into the Settings page.
            if (tag == "Dark-en-US" && Keyboard.Modifiers != ModifierKeys.None)
                Log($"  SKIP view key checks: a physical modifier is held ({Keyboard.Modifiers}); Keyboard.Modifiers reads the real keyboard");
            else if (tag == "Dark-en-US")
            {
                var page = Walk(root).OfType<StandbyAndTimer.Views.SettingsPanelView>().Single();
                var src = PresentationSource.FromVisual(page)!;
                void Press(Key k) => page.RaiseEvent(new System.Windows.Input.KeyEventArgs(Keyboard.PrimaryDevice, src, 0, k) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                SetField(settingsVm, "_hotkeyError", string.Empty);
                Log($"  debug: recording={settingsVm.RecordingHotkeyId ?? "null"} focusWithin={page.IsKeyboardFocusWithin} focused={Keyboard.FocusedElement?.GetType().Name ?? "null"} modifiers={Keyboard.Modifiers}");
                Press(Key.F5);   // no physical modifier held → rejected, still recording
                Check(settingsVm.IsRecordingPurge && settingsVm.HotkeyError == loc.GetString("Str_Settings_HotkeyNeedsModifier"), "view: F5 without modifier -> NeedsModifier");
                Press(Key.LeftCtrl); // bare modifier → ignored
                Check(settingsVm.IsRecordingPurge, "view: bare Ctrl keeps recording");
                Press(Key.Escape);
                Check(settingsVm.RecordingHotkeyId is null, "view: Esc cancels recording");
                settingsVm.StartHotkeyRecordingCommand.Execute("purge"); // re-enter for the cleanup below
            }
            settingsVm.CancelHotkeyRecording();
            SetField(settingsVm, "_updateStatus", string.Empty);
            SetField(settingsVm, "_updateAvailable", false);
            foreach (var prop in new[] { nameof(SettingsViewModel.UpdateStatus), nameof(SettingsViewModel.UpdateAvailable) })
                settingsVm.GetType().GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string)])!.Invoke(settingsVm, [prop]);
            SetField(settingsVm, "_hotkeyError", string.Empty);
            settingsVm.GetType().GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(string)])!
                      .Invoke(settingsVm, [nameof(SettingsViewModel.HotkeyError)]);
            Pump();

            // View → VM: clicking the Status tab must clear IsOpen.
            tabs.SelectedIndex = 0;
            Pump();
            if (settingsVm.IsOpen) { failures++; Log("  FAIL SelectedIndex=0 left IsOpen=true"); }
            else Log("  OK   selecting Status tab sets IsOpen=false");

            // Theme/language must not have been written back by any binding.
            if (settingsVm.SelectedTheme != themeEnum || settingsVm.SelectedLanguage != langEnum)
            { failures++; Log("  FAIL a binding wrote back SelectedTheme/SelectedLanguage"); }

            SetField(w, "_forceClose", true);
            w.Close();
        }

        // Tray context menu in every theme, with the "Settings" row in hover state.
        foreach (var lang in new[] { "en-US", "tr-TR" })
        {
            md[2] = new ResourceDictionary { Source = Pack($"Views/Strings/Strings.{lang}.xaml") };

            foreach (var key in new[] { "Str_Tray_NotifyUpdateTitle", "Str_Tray_NotifyUpdateBody", "Str_Tray_Settings" })
                if (loc.GetString(key) == key) { failures++; Log($"  FAIL [{lang}] missing string {key}"); }
            try { Log($"  [{lang}] update balloon: \"{loc.GetString("Str_Tray_NotifyUpdateTitle")}\" / \"{string.Format(CultureInfo.CurrentCulture, loc.GetString("Str_Tray_NotifyUpdateBody"), "2.2.0")}\""); }
            catch (FormatException ex) { failures++; Log($"  FAIL [{lang}] update body format: {ex.Message}"); }

            foreach (var themeEnum in new[] { Theme.Dark, Theme.Black, Theme.Light, Theme.White })
            {
                var trayType = typeof(MainWindow).Assembly.GetType("StandbyAndTimer.Services.Tray.TrayIconService")!;
                var tray = Activator.CreateInstance(trayType, [loc])!;
                var menu = (WinForms.ContextMenuStrip)trayType.GetMethod("BuildMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tray, null)!;
                ((ITrayIconService)tray).Refresh(new TrayState(true, 3, DeepPurge: true, GameMode: true, TimerLock: TimerLockMode.Locked));
                // Same call path as App: public interface, after the menu exists.
                ((ITrayIconService)tray).ApplyTheme(themeEnum);
                _ = menu.Handle;
                menu.Size = menu.GetPreferredSize(System.Drawing.Size.Empty);
                menu.PerformLayout();
                menu.Items[1].Select();   // hover on the "Settings" item
                using var bmp = new System.Drawing.Bitmap(menu.Width, menu.Height);
                menu.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, menu.Width, menu.Height));
                string theme = themeEnum.ToString();
                string p = Path.Combine(outDir, $"tray-{theme}-{lang}.png");
                bmp.Save(p, System.Drawing.Imaging.ImageFormat.Png);

                // Sample the plain background and the hovered row: they must
                // be the palette colours, not the system accent.
                var bg = bmp.GetPixel(menu.Width / 2, menu.Height - 4);
                var hover = bmp.GetPixel(menu.Width - 12, menu.Items[1].Bounds.Top + menu.Items[1].Height / 2);
                // Expected = the WPF palette of the same theme (TrayMenuPalette duplicates it).
                var pal = new ResourceDictionary { Source = Pack($"Views/Styles/Palette.{theme}.xaml") };
                string Hex(string key) { var c = ((SolidColorBrush)pal[key]).Color; return $"{c.R:X2}{c.G:X2}{c.B:X2}"; }
                string expectBg = Hex("SurfaceBg"), expectHover = Hex("HoverBg");
                string gotBg = $"{bg.R:X2}{bg.G:X2}{bg.B:X2}", gotHover = $"{hover.R:X2}{hover.G:X2}{hover.B:X2}";
                bool ok = gotBg == expectBg && gotHover == expectHover;
                if (!ok) failures++;
                Log($"== tray {theme} {lang}: saved {Path.GetFileName(p)} ({menu.Width}x{menu.Height}) bg=#{gotBg} hover=#{gotHover} {(ok ? "OK" : $"FAIL expected bg=#{expectBg} hover=#{expectHover}")}");
                menu.Dispose();
            }
        }

        File.WriteAllLines(Path.Combine(outDir, "report.txt"), Report);
        Log(failures == 0 ? "RESULT: all binding checks passed" : $"RESULT: {failures} binding check(s) FAILED");
        return failures == 0 ? 0 : 1;
    }
}
