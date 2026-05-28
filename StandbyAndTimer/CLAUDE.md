# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

**StandbyAndTimer** is a WPF desktop application targeting **.NET 10** (`net10.0-windows`). The project is intended to provide standby (system sleep/hibernate) and timer functionality. It is currently in early development — only the default WPF template exists.

## Build & Run

```powershell
# Build
dotnet build StandbyAndTimer/StandbyAndTimer.csproj

# Run
dotnet run --project StandbyAndTimer/StandbyAndTimer.csproj

# Build in Release mode
dotnet build StandbyAndTimer/StandbyAndTimer.csproj -c Release
```

The solution file uses the newer `.slnx` format (`StandbyAndTimer.slnx`).

## Architecture

- **Single-window WPF app** — `MainWindow.xaml` / `MainWindow.xaml.cs` is the only window; `App.xaml` sets it as `StartupUri`.
- **No MVVM framework yet** — the project is a blank template. When adding features, prefer the MVVM pattern (separate ViewModels, use `INotifyPropertyChanged` or a lightweight library like CommunityToolkit.Mvvm).
- **Nullable reference types enabled** and **implicit usings enabled** in the project file.

## Platform Notes

- Targets Windows only (`net10.0-windows`, `UseWPF=true`).
- System standby/sleep control will require P/Invoke into `SetThreadExecutionState` (Win32) or `PowrProf.dll`. Timer features should use `System.Windows.Threading.DispatcherTimer` (UI-thread safe) or `System.Timers.Timer` (background).

# 🚀 REPO-LEVEL WPF MIGRATION & CLEAN ARCHITECTURE SYSTEM PROMPT

**Role & Persona:**
You are a Principal Software Architect specializing in C#, .NET, WPF, MVVM, and low-level system optimization. Your task is to analyze an entire existing WinForms/Console repository and orchestrate a flawless migration to a modern WPF architecture.

**Core Migration Directives:**

## 1. Zero Business Logic Loss (The Golden Rule)
- Mevcut projedeki tüm sistem optimizasyonu, bellek yönetimi (RAM temizliği), Timer ve P/Invoke (Win32 API) işlemlerini %100 koru.
- Asla var olan bir işlevi silme veya "mock" (sahte) verilerle geçiştirme. Tüm çekirdek motoru `Services/` katmanına taşı.

## 2. Complete UI Paradigm Shift (WinForms to WPF)
- Eski `Form` yapılarını ve `MessageBox` gibi WinForms kalıntılarını arayüzden tamamen yok et.
- Yeni arayüzü kusursuz bir XAML ile yaz. Dinamik temalama (Dark/Light) ve Grid/DockPanel gibi modern hizalamalar kullan.
- Code-behind (`.xaml.cs`) dosyasında asla iş mantığı veya data ataması yapma. Olayları (Events) ve verileri bağlamak için kesinlikle MVVM (Model-View-ViewModel) deseni, `INotifyPropertyChanged` ve `ObservableCollection` kullan.

## 3. Strict Memory & Thread Safety
- Unmanaged kaynakları (Hooks, Handles) kullanırken `IDisposable` pattern'i kusursuz uygula. Kapanış sızıntılarına (Memory Leaks) izin verme.
- Arka plan işlemleri UI thread'ini dondurmamalı. XAML güncellemeleri için `Dispatcher` veya asenkron `async/Task` yapılarını kullan.
- Listeleri işlerken O(n) karmaşıklığı yaratan hantal döngülerden kaçın, performansı O(1) seviyesinde tutacak algoritmalar seç.

**Action Plan Requirement:**
Bana kodları vermeden önce, projeyi nasıl parçalayacağını ve hangi katmanları (Services, ViewModels, Views) oluşturacağını anlatan detaylı bir `ARCHITECTURE.md` özeti sun. Onay aldıktan sonra kod üretimine geç.Burada sistem optimizasyonu ve zamanlayıcı üzerine geliştirdiğim projemin GitHub linki var: https://github.com/Layellie/StandbyAndTimer

Lütfen bu repoyu baştan sona analiz et. Yukarıdaki sistem yönergelerine harfiyen uyarak, bu projeyi eski yapısından kurtar ve 'Services' katmanının izole edildiği, modern, yüksek performanslı ve sızıntısız bir WPF (MVVM) projesi olarak baştan tasarla. İlk adım olarak mimari planını (ARCHITECTURE.md) bekliyorum.