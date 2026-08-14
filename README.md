<h1 align="center">
  <img src="ASDA-icon.png" height="64" width="64" />
  <br/>
  Astro Steam Desktop Authenticator
</h1>

<p align="center">
  A modernized, actively maintained continuation of the Steam Desktop Authenticator app. Manage your Steam Guard 2FA, trade confirmations, and secure your account right from your desktop.
</p>

<p align="center">
  <a href="https://github.com/AstroZer01/Astro-Steam-Desktop-Authenticator/releases/latest">
    <img src="https://img.shields.io/badge/Download_Latest_Release-Windows_10+-blue?style=for-the-badge&logo=windows" alt="Download Latest Release" />
  </a>
</p>

<p align="center">
  <img src="https://img.shields.io/github/v/release/AstroZer01/Astro-Steam-Desktop-Authenticator" alt="Latest Release" />
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet" alt=".NET 8.0" />
  <img src="https://img.shields.io/github/license/AstroZer01/Astro-Steam-Desktop-Authenticator" alt="License" />
  <img src="https://img.shields.io/github/stars/AstroZer01/Astro-Steam-Desktop-Authenticator?style=social" alt="Stars" />
</p>

<p align="center">
  <img src="github-banner.jpg" alt="Astro Steam Desktop Authenticator" />
</p>

## About This Project
This is a continuation of the original [Steam Desktop Authenticator](https://github.com/Jessecar96/SteamDesktopAuthenticator) created by **Jessecar96**. Astro Steam Desktop Authenticator is an actively maintained fork that serves to keep the desktop authenticator tool functional, secure, and compatible with Steam's modern API changes. 

A reliable way to manage **Steam Guard**, handle **2FA (Two-Factor Authentication)**, and automatically accept or decline **trade confirmations** directly on you're pc without using a mobile phone.
All credit for the original design and implementation goes to Jessecar96 and the original contributors.

## Key Features

- **Steam Guard codes and account management** — view, copy, and manage Steam Guard authenticators for multiple accounts.
- **Trade confirmations** — review and accept or decline pending confirmations across managed accounts.
- **Login approvals** — review Steam login requests with device and location details, then approve or deny them manually. Optional account-wide rules can automatically approve persistent sign-ins or deny requests, with IP allowlisting controls.
- **Desktop notifications** — receive Windows notifications for pending trade confirmations and login requests, with navigation back to the relevant view.

## Version 1.1.0.0 Highlights

- Added a dedicated Login Actions experience for managing pending Steam login approvals and denials.
- Added automatic login rules, including persistent approval, automatic denial, and optional IP allowlisting.
- Added multi-account trade-confirmation monitoring and notification support.
- Added optional diagnostic logging so users can view and report issues.

## Build from Source on Windows

The committed UI assets are included in a normal .NET publish, so Node.js is not required for an ordinary release build.

### Prerequisites

- Windows 10 or later, 64-bit
- **[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)**

To run the built application, install the .NET 8 Desktop Runtime if the SDK is not installed, and install the [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/#download-section).

### Build a release package from Command Prompt

1. Clone the repository and open the cloned folder:

   ```powershell
   git clone https://github.com/AstroZer01/Astro-Steam-Desktop-Authenticator.git
   cd Astro-Steam-Desktop-Authenticator
   ```

2. From the repository root, run this command in Command Prompt:

   ```bat
   dotnet publish "Launcher\Launcher.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:PublishTrimmed=false -p:EnableCompressionInSingleFile=false -p:DebugSymbols=false -o "publish\ASDA"
   ```

   Close any copy of the app running from that folder before rebuilding so Windows can replace its executable. `dotnet publish` restores the required Windows x64 dependencies automatically and preserves an existing root `publish\ASDA\maFiles` folder.

3. When `dotnet publish` completes successfully, start:

   ```text
   publish\ASDA\Steam Desktop Authenticator.exe
   ```

Keep the complete `publish\ASDA` folder together in a folder your Windows account can write to; do not install this portable package under `Program Files`. The root executable is the Launcher; it starts the real app from `publish\ASDA\bin`. The `bin` folder contains the WebView files and runtime dependencies, while the application creates `publish\ASDA\maFiles` beside the Launcher on first launch. Back up that `maFiles` folder securely.

#### Reusable personal build script

If you keep a personal `.bat` file outside the repository, use this version. Change the two paths to your own locations. It intentionally never deletes `%OUTPUT%`, so an existing `%OUTPUT%\maFiles\manifest.json` and its `.maFile` account secrets survive every rebuild. `dotnet clean` only removes intermediate files from the source checkout; it does not affect the release folder.

```bat
@echo off
setlocal EnableExtensions
title Build Astro Steam Desktop Assistant

set "PROJECT_ROOT=E:\Desktop\cursor projects\SteamDesktopAuthenticator-master"
set "OUTPUT=E:\Desktop\Astro Steam Desktop Assistant"

cd /d "%PROJECT_ROOT%" || goto :failed

dotnet clean "SteamDesktopAuthenticator.sln" -c Release || goto :failed
dotnet publish "Launcher\Launcher.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:PublishTrimmed=false -p:EnableCompressionInSingleFile=false -p:DebugSymbols=false -o "%OUTPUT%" || goto :failed

del /q "%OUTPUT%\*.pdb" "%OUTPUT%\*.xml" "%OUTPUT%\*.config" 2>nul
del /q "%OUTPUT%\bin\*.pdb" "%OUTPUT%\bin\*.xml" "%OUTPUT%\bin\*.config" 2>nul

echo.
echo Compilation finished:
echo %OUTPUT%
pause
exit /b 0

:failed
echo.
echo Compilation failed. Close any running copy of the app and try again.
pause
exit /b 1
```

Never use `rmdir /s`, `del /s`, or a file-cleanup tool on the release root. If you need to discard a release, first copy `maFiles` somewhere secure, then remove only the files you explicitly intend to replace. Git ignores `maFiles` folders and `.maFile` exports so account secrets do not enter commits, pull requests, or releases from this repository.

### Build with Visual Studio 2022

1. Open `SteamDesktopAuthenticator.sln`.
2. Select the `Release` configuration.
3. In Solution Explorer, right-click **Launcher** — not **Steam Desktop Authenticator** — then choose **Publish**.
4. Select a **Folder** target. Use `publish\ASDA` as the target location and select **Windows x64** with **Framework-dependent** deployment.
5. In publish settings, enable **Produce single file** and leave trimming disabled, then select **Publish**.

Visual Studio's regular **Build** command places development output under `bin`; use **Publish** when you want the portable release folder described above.

### UI stylesheet for contributors

The WebView UI ships with committed, local CSS and fonts. Normal .NET builds use the committed `ui/assets/css/app.css` and do not require Node.js.

After changing an HTML template, `tailwind.config.js`, or `ui/tailwind-input.css`, install **[Node.js 22 LTS](https://nodejs.org/)** and run:

```powershell
npm ci # once after cloning or when package-lock.json changes
npm run build:ui
```

Commit the resulting `Steam Desktop Authenticator/ui/assets/css/app.css` with the UI change.

- `npm run build:ui` regenerates `Steam Desktop Authenticator/ui/assets/css/app.css`.
- `npm run check:ui` regenerates the stylesheet and fails if the generated file was not committed.

The hook runs only when staged UI templates, the Tailwind configuration, or the Tailwind input stylesheet change. CI verifies the same invariant, and the release workflow regenerates the stylesheet before publishing.


<p align="center">
  <strong>
    ❕ Alternatively you can download the latest version without technical knowledge and run it directly<br>
    <a href="https://github.com/AstroZer01/Astro-Steam-Desktop-Authenticator/releases/latest">
      Click here to Download
    </a>
  </strong>
</p>


## Current Focus & To-Do

### To-Do List

#### Core API & Session
- [x] Fix SSL/TLS 1.2+ handshake errors breaking connections.
- [x] Implement QR Code Login (using mobile HMAC-SHA256 signature).
- [x] Stabilize Steam API endpoints for trade confirmations — fetch, accept, and decline are all functional.
- [x] API support for confirming/declining trade offers — implemented via `AcceptConfirmation` / `DenyConfirmation`.
- [x] Improve API session refresh reliability — `IsRefreshTokenExpired` + `RefreshAccessToken` checks are in place with user-facing alerts on expiry.
- [x] Manage Steam login approvals and denials, including optional automatic login rules and IP allowlisting.

#### UI Rehaul
- [x] Full UI rehaul — replaced original WinForms UI with a modern WebView2-based interface (dark theme, Tailwind CSS, glassmorphism).
- [x] Animated startup loading screen with spinner and live status text.
- [x] Dark mode title bar (inherits Windows theme via DwmSetWindowAttribute).
- [x] Steam Guard code display with copy button and animated progress bar.
- [x] Account list with search/filter, scroll support, and one-click switching.
- [x] Trade Confirmations tab — list view with Accept/Decline per confirmation, per-account switcher dropdown, and refresh button.
- [x] Settings tab — periodic check toggle, check interval, auto-confirm options, with saved feedback.
- [x] Proxy Settings section (UI layout — Coming Soon, pending backend implementation).
- [x] Desktop notifications for pending trade confirmations and login requests.

#### In Progress / Remaining
- [ ] API endpoint for trading automation
- [ ] Proxy support backend implementation.

## Disclaimer
We provide no warranty for using this tool. You use this program at your own risk, and accept the responsibility to make backups of your `maFiles` (which contain your 2FA secrets) and prevent unauthorized access to your computer.

**Notice:** Astro Steam Desktop Authenticator is an unofficial tool and is not affiliated with, endorsed by, or associated with Valve Corporation or the official Steam Guard in any way.
