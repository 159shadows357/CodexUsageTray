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

Support
-------
Open Buy me a coffee from the tray menu or visit:
https://ko-fi.com/PongsathonTan
