# ARCHITECTURE.md — StandbyAndTimer

Mevcut Windows Forms monolitini, `Services` katmanı izole edilmiş, kaynak sızıntısı olmayan, modern bir WPF/MVVM uygulamasına dönüştürme planı.

---

## 1. Mevcut Yapının Sorunları

| Sorun | Detay |
|---|---|
| Monolitik `Form1` | Tüm iş mantığı (~17 KB), UI kodu, P/Invoke tanımları tek sınıfta |
| Ham `Thread` döngüsü | `CancellationToken` yok → uygulama kapanırken thread temizlenemiyor |
| `PerformanceCounter` sızıntısı | Asla `Dispose()` edilmiyor |
| `Process` sızıntısı | `optimizedProcesses` sözlüğü, handle'ları serbest bırakmadan Process nesnelerini tutuyor |
| `Environment.Exit(0)` | Dispose zinciri atlanıyor; registry/thread temizliği yapılmıyor |
| Registry çağrısı her tuş vuruşunda | `TextChanged` her değiştiğinde registry'e yazıyor |
| UI iş parçacığı güvensizliği | Timer callback'i `PerformanceCounter.NextValue()` çağrısı UI thread'inde yapılıyor |
| WinForms'dan WPF'e geçiş yok | Mevcut hedef `net10.0-windows` + `UseWindowsForms` |

---

## 2. Hedef Çözüm Yapısı

```
StandbyAndTimer.slnx
└── StandbyAndTimer/
    ├── StandbyAndTimer.csproj
    ├── app.manifest                         ← requireAdministrator + DPI aware
    ├── App.xaml / App.xaml.cs               ← DI container kurulumu, uygulama ömrü
    │
    ├── Core/                                ← Saf kontratlar; hiçbir platforma bağımlılık yok
    │   ├── Interfaces/
    │   │   ├── ITimerResolutionService.cs
    │   │   ├── IStandbyPurgeService.cs
    │   │   ├── IMemoryMonitorService.cs
    │   │   ├── IProcessOptimizationService.cs
    │   │   ├── IAutoStartService.cs
    │   │   └── ISettingsService.cs
    │   └── Models/
    │       ├── MemorySnapshot.cs            ← TotalMb, FreeMb, StandbyMb
    │       ├── AppSettings.cs               ← StandbyLimitMb, FreeLimitMb, GamePaths, AutoStart
    │       └── GameEntry.cs                 ← DisplayName, ExecutablePath
    │
    ├── Services/                            ← Tüm sistem çağrıları buraya izole edildi
    │   ├── Native/
    │   │   └── NativeMethods.cs             ← Tüm DllImport tanımları TEK dosyada
    │   ├── TimerResolutionService.cs
    │   ├── StandbyPurgeService.cs
    │   ├── MemoryMonitorService.cs
    │   ├── ProcessOptimizationService.cs
    │   ├── AutoStartService.cs
    │   └── SettingsService.cs
    │
    ├── ViewModels/
    │   └── MainViewModel.cs                 ← [ObservableProperty] + [RelayCommand]
    │
    ├── Views/
    │   ├── MainWindow.xaml / .xaml.cs       ← Code-behind sadece DataContext ataması
    │   └── Styles/
    │       └── Theme.xaml                   ← Sarı/siyah renk paleti, global stiller
    │
    └── Infrastructure/
        └── AppBootstrapper.cs               ← ServiceCollection yapılandırması
```

---

## 3. Katman Sorumlulukları

### 3.1 Core (Kontratlar)

- **Sıfır** platform bağımlılığı; teorik olarak test projesinde referans alınabilir.
- `IMemoryMonitorService`: `event EventHandler<MemorySnapshot> SnapshotUpdated` + `Task StartAsync(CancellationToken)` + `Task StopAsync()`.
- `ISettingsService`: `AppSettings Load()` + `void Save(AppSettings)` — registry'i soyutlar.
- `ITimerResolutionService`: `void Activate()` + `void Deactivate()` + `IDisposable` — uygulama kapandığında otomatik geri yükleme.

### 3.2 Services (Uygulama Detayları)

**`Native/NativeMethods.cs`** — Tüm P/Invoke buraya taşınır:
```
ntdll.dll     → NtSetTimerResolution, NtSetSystemInformation
kernel32.dll  → GlobalMemoryStatusEx, SetProcessInformation, SetThreadExecutionState
advapi32.dll  → OpenProcessToken, LookupPrivilegeValue, AdjustTokenPrivileges
avrt.dll      → AvSetMmThreadCharacteristics
```

**`MemoryMonitorService`** — `PeriodicTimer` (iptal edilebilir, .NET 6+) ile 1 saniyelik döngü. `PerformanceCounter` bu sınıfa aittir ve `Dispose()` ile temizlenir. Her döngüde `SnapshotUpdated` event'i yayınlar; `StandbyPurgeService` ve `ProcessOptimizationService`'i koordine eder.

**`TimerResolutionService`** — `IDisposable`. `Activate()` → 5000 (0.5 ms) hedef. `Dispose()` → `NtSetTimerResolution(false)` ile sistem varsayılanını geri yükler.

**`StandbyPurgeService`** — `SeProfileSingleProcessPrivilege` ayrıcalık yükseltmeyi kapsüller. `PurgeAsync()` → `NtSetSystemInformation(80, ...)` → `Task<bool>` döner.

**`ProcessOptimizationService`** — `Dictionary<int, ProcessHandle>` yerine `HashSet<int>` kullanır (sadece PID'leri takip eder); her kontrol sonrasında `Process.GetProcessById(pid)` ile doğrular ve `process.Dispose()` çağırır. Çıkmış process'ler temizlenir.

**`SettingsService`** — Registry okuma/yazma. `Save()` yalnızca `MainViewModel` kaydetme komutu tetiklendiğinde çağrılır (her tuş vuruşunda **değil**).

**`AutoStartService`** — `schtasks.exe` sarmalayıcısı; `ProcessStartInfo` ile çağrılır, `async/await` destekli.

### 3.3 ViewModels

`MainViewModel` tek ViewModel'dir. `CommunityToolkit.Mvvm` source generator kullanılır:

```csharp
// Örnek — kaynak üretici attribute'ları
[ObservableProperty] private long _totalRamMb;
[ObservableProperty] private long _freeRamMb;
[ObservableProperty] private long _standbyRamMb;
[ObservableProperty] private int _purgeCount;
[ObservableProperty] private int _standbyLimitMb;
[ObservableProperty] private int _freeLimitMb;
[ObservableProperty] private bool _gameModeEnabled;
[ObservableProperty] private bool _autoStartEnabled;
[ObservableProperty] private string? _selectedGame;

public ObservableCollection<GameEntry> Games { get; } = [];

[RelayCommand] private Task ManualPurgeAsync()   { ... }
[RelayCommand] private Task AddGameAsync()        { ... }  // OpenFileDialog
[RelayCommand] private void RemoveSelectedGame()  { ... }
[RelayCommand] private void ActivateTimer()       { ... }
[RelayCommand] private async Task SaveSettingsAsync() { ... }
```

`MemoryMonitorService.SnapshotUpdated` event'i, `Dispatcher.InvokeAsync` aracılığıyla observable property'leri günceller.

### 3.4 Views

`MainWindow.xaml.cs` yalnızca şunları içerir:
```csharp
public MainWindow(MainViewModel vm) {
    InitializeComponent();
    DataContext = vm;
}
```

Tüm stil ve renk (`#FFFF00` sarı, `#000000` siyah arka plan, `Times New Roman` yazı tipi) `Theme.xaml` içinde `Style` olarak tanımlanır — hardcoded değerler kaldırılır.

### 3.5 Infrastructure / DI

`App.xaml.cs` içinde `Microsoft.Extensions.DependencyInjection` ile:

```csharp
var services = new ServiceCollection()
    .AddSingleton<ISettingsService, SettingsService>()
    .AddSingleton<ITimerResolutionService, TimerResolutionService>()
    .AddSingleton<IStandbyPurgeService, StandbyPurgeService>()
    .AddSingleton<IMemoryMonitorService, MemoryMonitorService>()
    .AddSingleton<IProcessOptimizationService, ProcessOptimizationService>()
    .AddSingleton<IAutoStartService, AutoStartService>()
    .AddSingleton<MainViewModel>()
    .AddSingleton<MainWindow>();
```

`App.OnStartup` → `host.Start()` → `MainWindow.Show()`.  
`App.OnExit` → `host.StopAsync()` → tüm CancellationToken'lar iptal, `IDisposable` servisler temizlenir.

---

## 4. Eşzamanlılık Modeli

```
UI Thread (WPF Dispatcher)
    ↑ Dispatcher.InvokeAsync (snapshot güncellemeleri)
    │
Background Task (PeriodicTimer, CancellationToken)
    ├── MemoryMonitorService.RunLoopAsync()
    │       ├── GlobalMemoryStatusEx()           ← kernel32
    │       ├── PerformanceCounter.NextValue()   ← PerfCounter (kendi thread'inde)
    │       ├── → StandbyPurgeService (koşullu)
    │       └── → ProcessOptimizationService (koşullu)
    └── TimerResolutionService (başlangıçta bir kez)
            └── NtSetTimerResolution()           ← ntdll (her döngüde yenilenmez)
```

`NtSetTimerResolution` mevcut uygulamada her saniye çağrılıyor — bu gereksiz. Yeni yapıda yalnızca `Activate()` / `Deactivate()` ile bir kez çağrılır.

---

## 5. Kaynak Sızıntısı Önlemleri

| Mevcut Sorun | Çözüm |
|---|---|
| `PerformanceCounter` dispose edilmiyor | `MemoryMonitorService : IDisposable` — `Dispose()` counter'ı kapatır |
| `Thread` durdurulamıyor | `PeriodicTimer` + `CancellationTokenSource` — `StopAsync()` ile temiz iptal |
| `Process` handle sızıntısı | Her erişimde `using var p = Process.GetProcessById(pid)` |
| `Environment.Exit(0)` dispose atlatıyor | `Application.Current.Shutdown()` → `OnExit` → `host.StopAsync()` zinciri |
| Registry her tuş vuruşunda | `SettingsService.Save()` yalnızca explicit komutla veya `OnExit`'te çağrılır |

---

## 6. NuGet Bağımlılıkları

```xml
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.*" />
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.*" />
<PackageReference Include="H.NotifyIcon.Wpf" Version="2.*" />
```

- **CommunityToolkit.Mvvm** — Source-generated `[ObservableProperty]` / `[RelayCommand]`; reflection yok, sıfır ek yük.
- **Microsoft.Extensions.DependencyInjection** — .NET 10 SDK ile uyumlu; hosting altyapısı olmadan sadece DI container.
- **H.NotifyIcon.Wpf** — WinForms `NotifyIcon` olmadan, saf WPF sistem tepsisi desteği.

---

## 7. app.manifest Düzeltmesi

Mevcut manifest'te **3 çelişen** `requestedExecutionLevel` satırı var (asInvoker + requireAdministrator + highestAvailable + tekrar requireAdministrator). Yalnızca bir satır kalmalı:

```xml
<requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
```

---

## 8. Veri Akışı (Uçtan Uca)

```
Başlangıç:
  App.OnStartup
    → ServiceProvider.Build()
    → ISettingsService.Load()          → Registry → AppSettings
    → MainViewModel.InitializeAsync()
        → ITimerResolutionService.Activate()
        → IMemoryMonitorService.StartAsync(ct)
        → ApplySettings(AppSettings)
    → MainWindow.Show()

Çalışma döngüsü (her 1 saniye):
  MemoryMonitorService.RunLoopAsync()
    → GlobalMemoryStatusEx()           → MemorySnapshot
    → PerformanceCounter.NextValue()   → StandbyMb
    → SnapshotUpdated event yayınla
        → MainViewModel handler
            → Dispatcher.InvokeAsync(() => TotalRamMb = ...) ← WPF binding

    [Koşullu] standby >= limit && free <= limit
    → IStandbyPurgeService.PurgeAsync()
        → AdjustTokenPrivileges()
        → NtSetSystemInformation(80)
        → PurgeCount++

    [Koşullu] GameMode == true
    → IProcessOptimizationService.CheckAsync(GamePaths)
        → Process.GetProcessesByName()
        → p.PriorityClass = High
        → p.ProcessorAffinity = all cores
        → p.Dispose()

Kapanış:
  MainWindow.OnClosing → minimize-to-tray (Visibility=Hidden) veya Shutdown()
  App.OnExit
    → cts.Cancel()                     ← PeriodicTimer döngüsü durur
    → ITimerResolutionService.Dispose() ← timer resolution geri yüklenir
    → IMemoryMonitorService.Dispose()   ← PerformanceCounter kapatılır
    → ISettingsService.Save(current)    ← son ayarlar yazılır
```

---

## 9. Uygulama Sırası

Mimari plan onaylandıktan sonra aşağıdaki sırayla uygulanacak:

1. **`.csproj` güncelle** — `UseWindowsForms` → `UseWPF`, NuGet paketleri ekle
2. **`app.manifest` düzelt** — tek `requireAdministrator`
3. **`Core/` katmanı** — interface'ler ve modeller (bağımlılıksız)
4. **`Services/Native/NativeMethods.cs`** — tüm P/Invoke tanımları
5. **Servisler** — her biri bağımsız, sırayla: Settings → Timer → Purge → Memory → Process → AutoStart
6. **`Infrastructure/AppBootstrapper.cs`** — DI kaydı
7. **`MainViewModel`** — servis entegrasyonu, komutlar
8. **`Views/`** — XAML, stil, tray icon
9. **`App.xaml.cs`** — başlatma/kapanış zinciri
10. **CLAUDE.md güncelle**
