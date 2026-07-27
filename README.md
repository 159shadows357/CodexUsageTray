# Codex Usage Tray

Windows 11 system tray application สำหรับอ่าน Codex usage จาก local `codex app-server` ผ่าน stdio JSONL

Created by **Pongsathon.T** · [MIT License](LICENSE)

โปรเจกต์ส่วนบุคคลนี้ไม่ใช่ผลิตภัณฑ์ทางการและไม่มีความเกี่ยวข้องกับ OpenAI

## ความสามารถ

- รันแบบ `WinExe` ไม่มี console window
- เลือก tray icon ได้ระหว่าง `Ring`, `Solid bar`, `Segmented bar` และ `Number only`
- เลือก usage popup ได้ระหว่าง `Classic`, `macOS style` และ `Paper style`
- เมนูคลิกขวาใช้ dark style ให้ใกล้เคียง Windows 11
- popup แสดงทุก rate-limit bucket ที่ server ส่งมา
- รองรับ Weekly-only, `rateLimitsByLimitId`, fallback `rateLimits`, `primary` และ `secondary`
- แสดง reset credits และ token activity เมื่อ endpoint รองรับ
- แสดง token รวมและค่าใช้จ่ายเทียบ Standard API โดยประมาณ พร้อมโปรเจกต์ที่ใช้สูงสุดจาก local Codex history
- มี `Usage details` สำหรับดู 7 วัน, 30 วัน, all time, daily chart และทุกโปรเจกต์
- ใช้ persistent incremental index เพื่ออ่านซ้ำเฉพาะ session file ที่เปลี่ยน
- adaptive refresh ทุก 1, 2 หรือ 5 นาทีตามการเปลี่ยนแปลง, หน้าต่างที่เปิดอยู่และสถานะไฟ พร้อม manual/notification/resume refresh
- ตรวจ public GitHub Releases ได้แบบ manual, notify หรือ automatic install โดยไม่ต้องใช้ GitHub token
- ตรวจ GitHub asset SHA-256, ป้องกัน ZIP path traversal และตรวจ `SHA256SUMS.txt` ก่อนเปิด Setup
- ตรวจ response shape ของ rate limits และ token usage เพื่อบันทึก schema drift แบบ sanitized
- cache เก่ากว่า 10 นาทีแสดง `Data stale`
- single instance และปิด child `codex app-server` ตอนออก
- Start with Windows ผ่าน `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`

## Requirements

- Windows 11 x64
- .NET 10 SDK สำหรับ build
- Codex Desktop หรือ Codex CLI ที่ login แล้ว

ตัว publish เป็น self-contained จึงไม่ต้องติดตั้ง .NET runtime บนเครื่องปลายทาง

## Build และ test

```powershell
pwsh -File .\scripts\build.ps1
pwsh -File .\scripts\test.ps1
```

test project ใช้ package-free runner และไม่เพิ่ม NuGet test framework ผลล่าสุดคือ 61 tests ผ่านทั้งหมด

## Run จาก source

```powershell
dotnet run --project .\src\CodexUsageTray\CodexUsageTray.csproj -c Release
```

โปรแกรมไม่มี console window ให้เปิด popup หรือออกจากโปรแกรมผ่าน tray icon

## Publish

```powershell
pwsh -File .\scripts\publish.ps1 -Version 0.4.0
```

ผลลัพธ์:

```text
artifacts\publish\win-x64\CodexUsageTray.exe
```

script ใช้ official Microsoft `dotnet-public` feed สำหรับ .NET runtime pack เพราะ `api.nuget.org` ถูก reset จากเครือข่ายที่ใช้ build ครั้งนี้ สามารถ override ได้ด้วย `-RuntimePackSource`

`-Version` ถูกส่งเข้า app publish, Setup และชื่อ package จาก `package-release.ps1` เพื่อไม่ให้ EXE กับ ZIP รายงานคนละ version

## Install และ uninstall

ติดตั้งเฉพาะ current user และเปิดพร้อม Windows:

```powershell
pwsh -File .\scripts\install.ps1 -StartWithWindows
```

ติดตั้งโดยไม่เปิดโปรแกรมทันที:

```powershell
pwsh -File .\scripts\install.ps1 -NoLaunch
```

ตำแหน่งติดตั้ง:

```text
%LOCALAPPDATA%\Programs\CodexUsageTray
```

ถอนการติดตั้ง:

```powershell
pwsh -File .\scripts\uninstall.ps1
```

uninstall จะเก็บ settings, cache และ logs ใต้ `%LOCALAPPDATA%\CodexUsageTray` ไว้

## สร้าง ZIP สำหรับติดตั้งเครื่องอื่น

```powershell
pwsh -File .\scripts\package-release.ps1 -Version 0.4.0
```

script จะ publish application ล่าสุดก่อนสร้าง Setup และ ZIP ใช้ `-SkipAppPublish` ได้เมื่อยืนยันแล้วว่า `artifacts\publish\win-x64` เป็น build ที่ต้องการ

ผลลัพธ์:

```text
artifacts\release\CodexUsageTray-0.4.0-win-x64.zip
```

ภายใน ZIP มี `CodexUsageTray-Setup.exe`, application payload, `README.txt`, `LICENSE.txt` และ `SHA256SUMS.txt` ผู้ใช้ต้อง extract ทุกไฟล์แล้วดับเบิลคลิก Setup ตัว installer ใช้สิทธิ์ current user, เปิด Start with Windows เป็นค่า default และไม่ต้องติดตั้ง .NET เพิ่ม

สำหรับเครื่องอื่น ให้ดาวน์โหลด ZIP ล่าสุดจาก [GitHub Releases](https://github.com/159shadows357/CodexUsageTray/releases)

ไฟล์ `CodexUsageTray-0.1.0-win-x64.zip` และ `CodexUsageTray-0.2.0-win-x64.zip` เป็น package รุ่นเก่า ไม่ต้องเขียนทับ ให้แจก ZIP ตาม version ล่าสุด

ถอนการติดตั้งจาก package เดิม:

```powershell
.\CodexUsageTray-Setup.exe --uninstall
```

### Authenticode

EXE และ Setup มี publisher metadata เป็น `Pongsathon.T` แต่ release จะมีลายเซ็น Authenticode เมื่อส่ง certificate thumbprint ที่มี private key เท่านั้น:

```powershell
$thumbprint = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
  Where-Object HasPrivateKey |
  Select-Object -First 1 -ExpandProperty Thumbprint
pwsh -File .\scripts\package-release.ps1 `
  -Version 0.4.0 `
  -CodeSigningThumbprint $thumbprint
```

ห้ามใช้ self-signed certificate สำหรับ public release เพราะเครื่องปลายทางไม่ได้ trust certificate นั้นและไม่ได้แก้ปัญหา SmartScreen

## Start with Windows

เปิดหรือปิดจาก context menu ของ tray icon หรือใช้ script:

```powershell
pwsh -File .\scripts\start-with-windows.ps1 -Enable
pwsh -File .\scripts\start-with-windows.ps1 -Disable
```

ไม่ต้องใช้ Administrator

## Tray icon style

คลิกขวาที่ tray icon แล้วเลือก `Tray icon style`:

- `Ring`: ค่า default เป็นวงแสดงสัดส่วนโดยไม่มีตัวเลข
- `Solid bar`: แถบเติมต่อเนื่อง
- `Segmented bar`: แถบแบ่งสี่ช่วง
- `Number only`: แสดงเฉพาะตัวเลขเปอร์เซ็นต์คงเหลือโดยไม่ใส่เครื่องหมาย `%` ใช้ `Segoe UI` บนพื้นโปร่งใส ไม่มี label, กรอบ หรือ card

โปรแกรมเปลี่ยน icon ทันทีและเก็บค่าที่เลือกใน `settings.json` ทั้งสองแบบ bar ไม่มีขั้วหรือกรอบรูป battery ตัวเลขเปอร์เซ็นต์ที่แน่นอนอยู่ใน tooltip

## Popup style

คลิกขวาที่ tray icon แล้วเลือก `Popup style`:

- `Classic`: รูปแบบเดิมขนาดกะทัดรัด
- `macOS style`: พื้นหลังน้ำเงินเข้ม, rounded cards, progress bar, Account และ Token activity ตามภาพอ้างอิง
- `Paper style`: พื้นกระดาษสีอ่อน, กรอบเหลี่ยมพร้อม offset shadow, ตัวเลข monospace และ progress bar สีเข้ม

ค่าที่เลือกเขียนลง `settings.json` และใช้ทันทีโดยไม่ restart ทั้ง `macOS style` และ `Paper style` ปรับความสูงตามจำนวน bucket ภายใน `Screen.WorkingArea` และแสดงเฉพาะข้อมูลที่ app-server ส่งมาจริง `Paper style` จะแสดงจำนวน reset credits แต่ไม่สร้าง expiry รายรายการเมื่อ server ไม่ส่งข้อมูลนั้น

## API-equivalent estimate

popup ทั้งสามรูปแบบแสดงยอด token จาก local Codex history และค่าใช้จ่ายสมมติหากคิดตาม Standard API ไม่ใช่ยอดเรียกเก็บจริงของ ChatGPT/Codex plan โปรแกรมจัดกลุ่มตาม Git root จาก `cwd` และแสดง 3 โปรเจกต์ที่มีค่าใช้จ่ายสูงสุดใน popup

ราคาอ้างอิงมีวันที่กำกับในหน้าจอ `gpt-5.3-codex-spark` ยังไม่มีราคาสาธารณะ จึงใช้ราคา `gpt-5.3-codex` เป็น proxy ส่วน token ที่ไม่มี model จะอยู่ใน `unpriced` และไม่ถูกบวกเพิ่ม

สูตรที่ใช้:

```text
uncached input = input - cached input - cache-write input

estimated USD =
  uncached input × input rate
  + cached input × cached-input rate
  + cache-write input × cache-write rate
  + output × output rate
```

โปรแกรมอ่าน `%USERPROFILE%\.codex\sessions\**\*.jsonl` แบบ read-only เฉพาะ `cwd`, `model`, timestamp และ token counters ไม่อ่าน prompt/response หรือ `auth.json`

คลิกขวาที่ tray icon แล้วเลือก `Usage details` เพื่อเปิดหน้าต่างแยก:

- `Last 7 days`
- `Last 30 days`
- `All time`
- daily estimated API cost chart
- ตารางทุกโปรเจกต์พร้อม token, estimated USD และ unpriced token

ช่วง 7/30 วันใช้ timestamp จริงจาก session record เท่านั้น record ที่ไม่มี timestamp จะรวมใน `All time` และมีหมายเหตุว่าไม่ถูกนับในช่วงวันที่ หน้าต่างนี้แยกจาก popup เพื่อไม่เพิ่ม scrollbar ให้ popup เดิม

## Adaptive refresh

- เมื่อข้อมูลเปลี่ยนหรือเปิด popup/details อยู่: 1 นาที
- สถานะปกติ: ใช้ค่า base interval ใน `settings.json` ซึ่ง default คือ 2 นาที
- ข้อมูลไม่เปลี่ยน 3 รอบหรือเครื่องใช้แบตเตอรี่: อย่างน้อย 5 นาที
- app-server notification, manual refresh และ resume จะ refresh ทันที
- error retry ยังใช้ backoff 2, 5, 10, 30 และ 60 วินาที

## Public GitHub updates

source และ release อยู่ที่ public repository [`159shadows357/CodexUsageTray`](https://github.com/159shadows357/CodexUsageTray) โปรแกรมที่ติดตั้งแล้วไม่มี `.git` จึงตรวจ version จาก GitHub Releases API ไม่ใช้ `git pull`

ตั้งค่าจาก `right-click tray > Updates`:

- `Check now`: ตรวจ release ล่าสุดทันที
- `Off`: ไม่ตรวจ update อัตโนมัติ และเป็นค่า default
- `Notify only`: ตรวจหลังเปิดโปรแกรม 30 วินาที จากนั้นทุก 24 ชั่วโมง และแจ้งเมื่อมีรุ่นใหม่
- `Install automatically`: ดาวน์โหลด ตรวจสอบ และเรียก Setup โดยไม่ถามซ้ำ

request ตรวจ update และดาวน์โหลด release ไม่ส่ง `Authorization` header เครื่องปลายทางจึงไม่ต้องสร้างหรือเก็บ GitHub token หากเคยใช้รุ่น `0.3.0` สามารถลบ credential target `CodexUsageTray/GitHub/159shadows357/CodexUsageTray` จาก Windows Credential Manager ได้

ก่อนติดตั้ง โปรแกรมตรวจ:

1. ชื่อ asset ต้องตรง `CodexUsageTray-X.Y.Z-win-x64.zip`
2. `asset.digest` ต้องเป็น SHA-256 และตรงกับไฟล์ที่ดาวน์โหลด
3. ZIP ต้องไม่มี path traversal และมีขนาดไม่เกิน limit
4. `CodexUsageTray-Setup.exe` กับ `CodexUsageTray.exe` ต้องตรง `SHA256SUMS.txt`

auto-install เป็น opt-in เพราะ executable ยังไม่มี Authenticode signature Setup เดิมจะรักษาสถานะ Start with Windows, ปิด instance เก่า, แทน executable, ตรวจ hash หลัง copy และเปิดรุ่นใหม่

## Support the project

หากโปรแกรมนี้ช่วยงานของคุณ สามารถสนับสนุนค่าใช้จ่ายในการพัฒนาและทดสอบได้ที่ [Ko-fi.com/PongsathonTan](https://ko-fi.com/PongsathonTan)

เมนู `Buy me a coffee...` ใน Widget จะเปิด Ko-fi โดยตรง

## ตรวจ login

```powershell
codex login status
```

หาก `codex` ใน PATH ชี้ไป packaged executable ใต้ `C:\Program Files\WindowsApps` และได้ `Access is denied` ให้ใช้ per-user executable ที่ Codex Desktop สร้างไว้:

```powershell
$codex = Get-ChildItem "$env:LOCALAPPDATA\OpenAI\Codex\bin\*\codex.exe" -File |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 1 -ExpandProperty FullName
& $codex login status
```

application resolver ตรวจตามลำดับนี้:

1. environment variable `CODEX_CLI_PATH`
2. `CODEX_CLI_PATH` ใน `%USERPROFILE%\.codex\config.toml`
3. `%LOCALAPPDATA%\OpenAI\Codex\bin\*\codex.exe`
4. PATH โดยข้าม WindowsApps ที่รันไม่ได้
5. Codex binary จาก VS Code extension

รองรับ `.exe`, `.cmd` และ `.bat`

## Logs และ runtime data

เปิด logs จาก context menu หรือไปที่:

```text
%LOCALAPPDATA%\CodexUsageTray\logs
```

ไฟล์ runtime:

```text
settings.json
usage-cache.json
local-usage-index.json
notification-state.json
updates\
logs\codex-usage-tray-yyyyMMdd.log
```

`local-usage-index.json` เก็บ SHA-256 ของ session/project path, file size, modified time และยอด token/cost รายวันเท่านั้น ไม่เก็บ full path, prompt หรือ response content

เก็บ log ไม่เกิน 7 วัน โปรแกรมไม่อ่าน `auth.json` และ diagnostics ไม่มี email, access token, refresh token, GitHub token, API key, authorization header, credential หรือ raw authentication response

## ภาษา

- [English README](README_EN.md)

## License

MIT License · Copyright © 2026 Pongsathon.T
