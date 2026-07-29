<h1 align="center">
  <img src="icon.png" height="64" width="64" />
  <br/>
  Astro Steam Desktop Authenticator
</h1>

<p align="center">
  A continuation of the Steam Desktop Authenticator app.
</p>

<p align="center">
  <a href="https://github.com/AstroZer01/Astro-Steam-Desktop-Authenticator/releases/latest">
    <img src="https://img.shields.io/badge/Download_Latest_Release-Windows_10+-blue?style=for-the-badge&logo=windows" alt="Download Latest Release" />
  </a>
</p>

## About This Project
This is a continuation of the original [Steam Desktop Authenticator](https://github.com/Jessecar96/SteamDesktopAuthenticator) created by **Jessecar96**. Astro Steam Desktop Authenticator is an actively maintained fork that serves to keep the tool functional, secure, and compatible with Steam's modern API changes.

All credit for the original design and implementation goes to Jessecar96 and the original contributors.

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
