# ARCHITECTURE.md — StandbyAndTimer

**StandbyAndTimer**, **.NET 10 (`net10.0-windows`)** üzerinde çalışan, katmanlı MVVM mimarisine sahip bir Windows sistem optimizasyonu ve oyuncu yardımcı uygulamasıdır.

---

## 1. Mimari İlkeler

| İlke | Uygulama Şekli |
|---|---|
| **Tek Noktadan Win32 İzolasyonu** | Tüm P/Invoke bildirimleri yalnızca [`Services/Native/NativeMethods.cs`](StandbyAndTimer/Services/Native/NativeMethods.cs) içinde (`internal`) tanımlıdır; `Core`, `ViewModels` veya `Views` katmanlarına sızmaz. |
| **Sıfır Kaynak Sızıntısı** | `Process` nesneleri her kullanımda `using` ile anında temizlenir; süreç takibi yalnızca PID (`HashSet<int>` / `ConcurrentDictionary<int, byte>`) üzerinden yapılır. |
| **Ultra-Düşük İzleme Maliyeti** | Bellek listeleri `PerformanceCounter` (~150 µs) yerine `NtQuerySystemInformation(SystemMemoryListInformation = 80)` ile ~0.2 µs'de okunur ([`MemoryLists.cs`](StandbyAndTimer/Services/MemoryLists.cs)). Süreç listesi `Process.GetProcesses()` (435 KB/sn tahsisat) yerine `NtQuerySystemInformation(SystemProcessInformation = 5)` ile 18 KB tahsisatla taranır ([`RunningGames.cs`](StandbyAndTimer/Services/RunningGames.cs)). |
| **Geriye Dönük Ayar Uyumluluğu** | Kullanıcı ayarları (`HKCU\SOFTWARE\StandbyAndTimer`) güncellemeler arasında korunur; mevcut kayıt defteri anahtarları ve enum adları değiştirilmez. |
| **Sabit Düzen & Görsel Doğrulama** | Pencere `760×520` sabit boyuttadır (`ScrollViewer` kullanılmaz). [`tools/UiRender`](tools/UiRender/Program.cs) aracı tüm temaları (`Dark`, `Black`, `Light`, `White`) ve dilleri (`en-US`, `tr-TR`) PNG olarak render edip taşma/kesilmeleri otomatik denetler. |

---

## 2. Çözüm Yapısı

```
StandbyAndTimer.slnx
├── StandbyAndTimer/                         ← Ana WPF Uygulaması (net10.0-windows, x64)
│   ├── app.manifest                         ← requireAdministrator + PerMonitorV2 DPI + Win10/11 uyumluluğu
│   ├── App.xaml / App.xaml.cs               ← Tek örnek (Mutex + EventWaitHandle), DLL planting koruması,
│   │                                          EcoQoS opt-out, DI kurulumu, tepsi ve kısayol yaşam döngüsü
│   ├── Core/                                ← Saf kontratlar ve modeller (platform bağımlılığı yok)
│   │   ├── Interfaces/                      ← ITimerResolutionService, IStandbyPurgeService, IMemoryMonitorService,
│   │   │                                      IProcessOptimizationService, IGameDetectionService, ISettingsService,
│   │   │                                      IGlobalTimerSetting, ICleanupTarget, ISystemInfoService, vb.
│   │   └── Models/                          ← AppSettings, GameEntry, MemorySnapshot, PurgeStats, CleanupStats, vb.
│   │
│   ├── Services/                            ← Win32, NT Kernel ve Registry implementasyonları
│   │   ├── Native/NativeMethods.cs          ← Tüm DllImport tanımları (ntdll, kernel32, user32, avrt, advapi32,
│   │   │                                      dwmapi, shell32, ole32, powrprof)
│   │   ├── TimerResolutionService.cs        ← NtSetTimerResolution + 50 ms MMCSS "Pro Audio" watchdog thread
│   │   ├── StandbyPurgeService.cs           ← Normal, Light (öncelik-0) ve Deep (modified + file cache) temizlik
│   │   ├── MemoryLists.cs                   ← NtQuerySystemInformation(80) ile 8 öncelikli standby okuma
│   │   ├── MemoryMonitorService.cs          ← 1 sn PeriodicTimer döngüsü: bellek, boşta kalma, oyun ve oto-temizlik
│   │   ├── RunningGames.cs                  ← NtQuerySystemInformation(5) ile hafif süreç ve PID anlık görüntüsü
│   │   ├── ProcessOptimizationService.cs    ← High CPU önceliği + tam çekirdek affinity + EcoQoS/Timer opt-out
│   │   ├── GameDetectionService.cs          ← 3 sn tam ekran ön plan penceresi yoklaması (2 ardışık doğrulama)
│   │   ├── GlobalTimerSetting.cs            ← HKLM GlobalTimerResolutionRequests (Windows 11)
│   │   ├── SettingsService.cs               ← HKCU\SOFTWARE\StandbyAndTimer kalıcılığı
│   │   ├── PurgeStatsStore.cs               ← purge-history.json kalıcılığı
│   │   ├── UpdateService.cs                 ← GitHub Releases kontrolü + SHA-256 doğrulamalı güncelleme
│   │   ├── ThemeService.cs / LocalizationService.cs ← Canlı ResourceDictionary değişimi
│   │   ├── Tray/                            ← WinForms NotifyIcon + temalı ContextMenuStrip (TrayMenuPalette)
│   │   └── Maintenance/                     ← Bakım sekmesi servisleri (SafeFolderCleaner, FolderCleanupTarget,
│   │                                          RecycleBinTarget, WindowsOldTarget, DeliveryOptimizationTarget,
│   │                                          DownloadsService, SystemInfoService, SystemToolLauncher)
│   │
│   ├── Infrastructure/
│   │   ├── AppBootstrapper.cs               ← Microsoft.Extensions.DependencyInjection singleton kayıtları
│   │   ├── AppConstants.cs                  ← Sabitler
│   │   └── CrashHandler.cs                  ← Beklenmeyen hata yakalama ve timer serbest bırakma güvencesi
│   │
│   ├── ViewModels/
│   │   ├── MainViewModel.cs                 ← Durum sekmesi, timer kilit/boşta modları, oyun listesi, temizlik geçmişi
│   │   ├── MaintenanceViewModel.cs          ← Bakım sekmesi, paralel tarama/temizlik, grafikli geçmiş, Windows bilgileri
│   │   ├── SettingsViewModel.cs             ← Ayarlar sekmesi, kısayol kaydı, dışa/içe aktarma, güncellemeler, loglar
│   │   ├── SystemViewModel.cs               ← Başlık çubuğu Administrator rozeti altındaki Windows düzeyi ayarlar
│   │   └── DownloadsViewModel.cs            ← İndirilenler klasörü seçmeli Geri Dönüşüm Kutusu diyaloğu
│   │
│   └── Views/
│       ├── MainWindow.xaml / .xaml.cs       ← 3 sekmeli ana pencere (Status, Maintenance, Settings) ve modal katmanlar
│       ├── Cards/                           ← TitleBar, TimerCard, MemoryStatsCard, StandbyPurgeCard, GameModeCard, StatusBar
│       ├── Overlays/                        ← About, Wizard, System, PurgeHistory, Downloads, ShaderCaches,
│       │                                      CleanupHistory, GlobalTimerPrompt, CloseConfirmation
│       ├── Styles/                          ← Theme.xaml + Palette.{Dark,Black,Light,White}.xaml
│       └── Strings/                         ← Strings.{en-US,tr-TR}.xaml
│
├── StandbyAndTimer.Tests/                   ← xUnit birim ve entegrasyon testleri (sahte servislerle güvenli koşum)
├── tools/
│   ├── UiRender/                            ← XAML/tema/dil değişikliklerini PNG'ye render eden düzen denetleyicisi
│   └── IconGen/                             ← Uygulama ikonunu (16–256 px) vektörel üreten araç
├── installer/Setup.iss                      ← Inno Setup 6 kurulum betiği
└── winget-manifests/                        ← Microsoft Winget paket manifestleri (Layellie.StandbyAndTimer)
```

---

## 3. Eşzamanlılık ve Çalışma Döngüsü

```
UI Thread (WPF Dispatcher)
  ├── MainWindow / Tepsi Simgesi / Global Kısayollar (WM_HOTKEY)
  └── Dispatcher.InvokeAsync ile anlık görüntü ve bildirim güncellemeleri
        ↑
Arka Plan İş Parçacıkları
  ├── TimerResolution-Watchdog (Dedicated Thread, AboveNormal, MMCSS "Pro Audio")
  │     └── Her 50 ms'de bir NtSetTimerResolution ile 0.5 ms hedefini tazeler ve ölçer
  │
  ├── MemoryMonitorService (PeriodicTimer, 1 sn)
  │     ├── GlobalMemoryStatusEx() + MemoryLists.TryRead() (NtQuerySystemInformation 80)
  │     ├── RunningGames.Take() (NtQuerySystemInformation 5) → çalışan oyunları eşleştirir
  │     ├── IdleMonitor.TimeSinceLastInput (GetLastInputInfo)
  │     ├── Koşullar sağlanırsa → StandbyPurgeService.PurgeAsync(PurgeTrigger.Auto)
  │     └── GameMode açıksa → ProcessOptimizationService.CheckAndOptimizeAsync()
  │
  ├── GameDetectionService (PeriodicTimer, 3 sn)
  │     └── GetForegroundWindow() + GetMonitorInfo() → 2 ardışık tam ekran yoklamasında otomatik oyun ekler
  │
  └── MaintenanceViewModel (Yalnızca Bakım sekmesi açıkken aktif)
        └── SafeFolderCleaner.Scan / Clean (sekmeden çıkıldığında CancellationToken ile iptal edilir)
```

---

## 4. Güvenlik ve Dayanıklılık Önlemleri

- **DLL Planting Koruması:** Uygulama başlangıcında `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS)` çağrılarak yönetici yetkili sürecin çalışma dizininden sahte DLL yüklemesi engellenir.
- **Güvenli Dosya Temizliği (`SafeFolderCleaner`):** Bakım sekmesindeki temizleyici hiçbir zaman sembolik bağları veya junction noktalarını (`FileAttributes.ReparsePoint`) takip etmez; yalnızca izin verilen kök klasörlerin içini temizler ve kullanımdaki dosyaları sessizce atlar.
- **SHA-256 Doğrulamalı Güncelleme (`UpdateService`):** İndirilen kurulum paketi, yazma/yeniden adlandırma kilitli bir dosya tanıtıcısı (`FileShare.Read`) üzerinden SHA-256 özet kontrolünden geçirilir ve kilit tutulurken başlatılır (TOCTOU koruması).
- **Gizli Başlangıçta Sıfır GPU Yükü:** `-hidden` argümanıyla (Windows başlangıcında) açıldığında pencere ilk kez gösterilene kadar `RenderMode.SoftwareOnly` kullanılır; Direct3D iş parçacıkları ve GPU belleği tepside boşuna tutulmaz.
