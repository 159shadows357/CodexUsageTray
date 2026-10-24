Codex Usage Tray
================

Created by Pongsathon.T
Licensed under the MIT License. See LICENSE.txt.

Requirements
------------
- Windows 11 x64
- Codex Desktop or Codex CLI installed and signed in
- Administrator permission is not required
- A separate .NET installation is not required

Install
-------
1. Extract every file from the ZIP into the same folder.
2. Double-click CodexUsageTray-Setup.exe.
3. Setup installs the app to:
   %LOCALAPPDATA%\Programs\CodexUsageTray
4. Setup enables Start with Windows and opens the tray app.

Optional commands
-----------------
Install without Start with Windows:
  CodexUsageTray-Setup.exe --no-startup

Uninstall the program and startup entry:
  CodexUsageTray-Setup.exe --uninstall

Uninstall preserves settings, cache, and logs under:
  %LOCALAPPDATA%\CodexUsageTray

Files
-----
- CodexUsageTray-Setup.exe: current-user installer
- CodexUsageTray.exe: application payload required by setup
- SHA256SUMS.txt: SHA-256 hashes for package verification

Security note
-------------
The executables are not digitally signed. Windows SmartScreen may show a warning.

Public updates
--------------
Version 0.4.0 and later checks the public GitHub Releases feed without
a GitHub token. Open the tray menu, then Updates > Check now.
Automatic install is optional and disabled by default.

Reset credits
-------------
Open Reset credits from the tray menu. Off is the default.
Use Timing to choose 5 minutes through 7 days before expiry. The default is
6 hours. Notify before expiry shows one alert at the selected time.
Use before expiry is opt-in and requires confirmation. It can redeem the
earliest expiring available reset credit at the selected time, then at later
1-hour, 15-minute, or 5-minute fallback stages. The app acts only on live
details returned by Codex.

Support
-------
Open Buy me a coffee from the tray menu or visit:
https://ko-fi.com/PongsathonTan

Date versions and compatibility
-------------------------------
This release uses year.month.day.patch (2026.10.24.1).
Use the four-part-named ZIP for manual installation. The three-part-named
compatibility ZIP contains identical verified binaries for older updaters.

Tibo forecast and themes
-----------------------
Cadra Light and Cadra Dark are available alongside all original popup styles.
The optional Tibo reset forecast uses a free public API with no account data.
Data: https://codex-reset.com/ (linked by https://tiboreset.com/developers).
It is a community global-reset prediction, separate from your account quota
and reset-credit expiry. Missing or old data is labeled unavailable or stale.
Use Tibo forecast > Show community forecast to disable it.

Plans and estimates
-------------------
Plus and other ChatGPT plans display only quota windows returned by Codex.
Reset credits may be unavailable. API-equivalent estimates use current Standard
rates (2026-10-24), assume 30-minute cache writes, and are not billed charges.

Tray quota source
-----------------
Frontier only is the default. Tray quota source > Lowest remaining bucket
offers the alternate lowest-quota indicator. Frontier only never substitutes Spark.
All quota buckets remain accessible in the popup, with pages when needed.
