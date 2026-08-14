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

## How to Compile / Build

If you prefer to compile the program from source rather than downloading the pre-built binaries, follow these steps:

1. **Install Prerequisites**: You must have the **[.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)** installed on your machine.
2. **Clone the Repository**:
   ```bash
   git clone https://github.com/AstroZer01/Astro-Steam-Desktop-Authenticator.git
   cd Astro-Steam-Desktop-Authenticator
   ```
3. **Publish the Application**:
   Create the same portable release layout used by the release workflow:
   ```bash
   dotnet publish "Steam Desktop Authenticator/Steam Desktop Authenticator.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:PublishTrimmed=false -p:EnableCompressionInSingleFile=false -p:DebugSymbols=false -o publish/ASDA
   ```
   *Alternatively, open `SteamDesktopAuthenticator.sln` in **Visual Studio 2022**, right-click the **Steam Desktop Authenticator** project, and choose **Publish** (not Build). Use a Folder target with the same Windows x64, framework-dependent, single-file settings and output folder shown above.*
4. **Run the Application**: Start `publish/ASDA/Steam Desktop Authenticator.exe`. Keep the complete `publish/ASDA` folder together; it contains the WebView files and runtime dependencies. The `maFiles` folder is created there on first launch and stores your local account data.

### UI stylesheet for contributors

The WebView UI ships with committed, local CSS and fonts. Install Node.js 22 or later, then run `npm ci` once after cloning; this enables the repository's pre-commit hook.

- `npm run build:ui` regenerates `Steam Desktop Authenticator/wwwroot/assets/css/app.css`.
- `npm run check:ui` regenerates the stylesheet and fails if the generated file was not committed.

The hook runs only when staged UI templates, the Tailwind configuration, or the Tailwind input stylesheet change. CI verifies the same invariant, and the release workflow regenerates and commits the stylesheet before publishing.


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
