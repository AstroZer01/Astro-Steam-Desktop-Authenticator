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
Currently, this rework is strictly focused on maintaining and fixing the underlying API support to ensure logins, session management, and trade confirmations remain functional.

### To-Do List
- [x] Fix SSL/TLS 1.2+ handshake errors breaking connections.
- [x] Implement QR Code Login (using mobile HMAC-SHA256 signature).
- [ ] Stabilize Steam API endpoints for trade confirmations.
- [ ] Improve API session refresh reliability.
- [ ] **Only API support for now** (No major UI redesigns or new non-essential features planned currently).

## Disclaimer
We provide no warranty for using this tool. You use this program at your own risk, and accept the responsibility to make backups of your `maFiles` (which contain your 2FA secrets) and prevent unauthorized access to your computer.
