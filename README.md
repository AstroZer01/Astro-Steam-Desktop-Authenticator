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

## About This Project
This is a continuation of the original [Steam Desktop Authenticator](https://github.com/Jessecar96/SteamDesktopAuthenticator) created by **Jessecar96**. Astro Steam Desktop Authenticator is an actively maintained fork that serves to keep the desktop authenticator tool functional, secure, and compatible with Steam's modern API changes. 

If you need a reliable way to manage **Steam Guard**, handle **2FA (Two-Factor Authentication)**, and automatically accept or decline **trade confirmations** directly in C# without using a mobile phone, this project is for you.

All credit for the original design and implementation goes to Jessecar96 and the original contributors.

## Screenshots

| Main Page | Settings |
| :---: | :---: |
| <img src="screenshot%20main%20page.png" width="400"> | <img src="screenshot%20settings.png" width="400"> |


## How to Compile / Build

If you prefer to compile the program from source rather than downloading the pre-built binaries, follow these steps:

1. **Install Prerequisites**: You must have the **[.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)** installed on your machine.
2. **Clone the Repository**:
   ```bash
   git clone https://github.com/AstroZer01/Astro-Steam-Desktop-Authenticator.git
   cd Astro-Steam-Desktop-Authenticator
   ```
3. **Build the Application**:
   We have bundled a convenient MSBuild pipeline. You can build the entire project including the launcher by running:
   ```bash
   dotnet publish "Launcher/Launcher.csproj" -c Release
   ```
   *Alternatively, you can open `SteamDesktopAuthenticator.sln` in **Visual Studio 2022** and build the solution.*
4. **Run the Application**: The compiled executable will be located in `Launcher/bin/Release/net8.0-windows/win-x64/publish/` (or similar depending on your build configuration).

## Current Focus & To-Do

### To-Do List

#### Core API & Session
- [x] Fix SSL/TLS 1.2+ handshake errors breaking connections.
- [x] Implement QR Code Login (using mobile HMAC-SHA256 signature).
- [x] Stabilize Steam API endpoints for trade confirmations — fetch, accept, and decline are all functional.
- [x] API support for confirming/declining trade offers — implemented via `AcceptConfirmation` / `DenyConfirmation`.
- [x] Improve API session refresh reliability — `IsRefreshTokenExpired` + `RefreshAccessToken` checks are in place with user-facing alerts on expiry.

#### UI Rehaul
- [x] Full UI rehaul — replaced original WinForms UI with a modern WebView2-based interface (dark theme, Tailwind CSS, glassmorphism).
- [x] Animated startup loading screen with spinner and live status text.
- [x] Dark mode title bar (inherits Windows theme via DwmSetWindowAttribute).
- [x] Steam Guard code display with copy button and animated progress bar.
- [x] Account list with search/filter, scroll support, and one-click switching.
- [x] Trade Confirmations tab — list view with Accept/Decline per confirmation, per-account switcher dropdown, and refresh button.
- [x] Settings tab — periodic check toggle, check interval, auto-confirm options, with saved feedback.
- [x] Proxy Settings section (UI layout — Coming Soon, pending backend implementation).

#### In Progress / Remaining
- [ ] API endpoint for trading automation
- [ ] Proxy support backend implementation.
- [ ] Auto-confirm popup notifications reliability improvements.

## Disclaimer
We provide no warranty for using this tool. You use this program at your own risk, and accept the responsibility to make backups of your `maFiles` (which contain your 2FA secrets) and prevent unauthorized access to your computer.

**Notice:** Astro Steam Desktop Authenticator is an unofficial tool and is not affiliated with, endorsed by, or associated with Valve Corporation or the official Steam Guard in any way.
