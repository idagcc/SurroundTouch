# SurroundTouch

A Windows 10/11 application that maps two independent touchscreen controllers to two regions of one combined desktop, including NVIDIA Surround. Both screens can be touched at the same time.

[Download the Windows x64 build](https://github.com/idagcc/SurroundTouch/releases/tag/v1.1.0). Extract the ZIP and run `Install.cmd`; see the installation and validation notes below.

## Update prepared on 24 October 2026

- Lower idle overhead and less allocation in the touch loop. Queued movement is merged within a Windows history read while presses, releases, cancellations, and independent controller contacts retain their order.
- **Start at Windows sign-in**, **Resume saved correction on launch**, and tray operation.
- **Load profile…** and **Save profile as…** for switching and backing up configurations.
- **Touch driver check** with automatic observation of simultaneous contacts, plus a report containing HID identities, advertised contact limits, driver versions, Device Manager errors, and runtime counters.

Both Windows executable variants compile and 656 automated assertions pass on macOS using Mono. This update has **not been run on Windows or tested with physical touch panels**. Earlier hands-on success with the previous build does not validate this update. See `TEST-RESULTS.txt` for the precise boundary and benchmark results.

## Install or update on Windows

1. Extract the complete package to a local folder. For an existing installation, use **Exit** from the SurroundTouch tray menu before running this package's `Uninstall.cmd`. Older builds can be closed normally. Per-user profiles are retained.
2. Run `Install.cmd` and review the installation disclosure. Windows requires a trusted, signed executable installed in Program Files for global touch routing. Setup signs the supplied executables locally and creates a Start menu shortcut.
3. Open **SurroundTouch** from that installed shortcut. Use a mouse and keyboard for setup. Connect each panel's USB touch cable as well as its video cable.
4. Select the combined display, side-by-side or stacked arrangement, split percentage, and any actual desktop bezel compensation.
5. Use **Identify by touch** to assign the physical first and second panels. Two detected controllers do not reveal which one is physically on the left: verify the assignments rather than relying on enumeration order. Leave rotation at 0°, flips off, and input ranges at 0–100% unless the touch test shows adjustment is needed.
6. Lift all fingers, press **Start correction**, and use **Test touch** to check both panels, edges, corners, and simultaneous contact. Starting correction saves the current profile.

From a source checkout, run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1` first. The ready-to-install update archive includes unsigned package executables; `Install.cmd` creates the signed installed copies. Do not overwrite an installed executable directly: changes invalidate its signature.

## Start automatically after a restart

After confirming a working profile:

1. Enable **Start at Windows sign-in** in the installed application.
2. Enable **Resume saved correction on launch**.
3. Press **Save profile** if you changed any configuration.

At the next Windows sign-in, the app opens in the notification tray. It waits up to 60 seconds for the saved USB controllers and display, checking every three seconds. Correction resumes only when both controller identities, the display name, resolution, position, and controllers' Windows display mappings match the saved profile. It shows the window and an explanation if these checks do not succeed. **Stop** cancels a pending restore.

Older profiles still load. Press **Start correction** once in the new build to save the additional source-layout confirmation needed for automatic restore. Moving a USB connection can change its identity; re-identify and verify the panels in that case.

Startup is per Windows user and occurs after sign-in, outside the secure logon desktop. Windows may delay startup or disable an entry in its Startup Apps settings. If it does not launch, check that Windows allows SurroundTouch to run at startup. The two startup options are independent: launching the tray app alone does not enable correction.

Closing or minimizing the main window keeps the app in the tray. Double-click its tray icon or reopen the installed shortcut to show it. Use **Stop**, **Ctrl + Alt + F10** while correction is active, or tray **Exit** to restore original Windows touch routing. Exit stops this launch; disabling startup requires clearing the sign-in checkbox. Display changes, controller failures, session lock/disconnect/logoff, and suspend stop correction and require review before restarting it.

## Load and save profiles

**Save profile** updates `%LOCALAPPDATA%\SurroundTouch\profile.json`, used for the next launch. **Save profile as…** also creates a named JSON backup anywhere you choose. **Load profile…** imports a validated JSON profile and makes it the default for the next launch; review its assignments and press Start correction to apply it now. Stop correction before switching configurations.

Profiles contain display layout, controller identities, rotation, flips, and crop settings. Startup preferences are kept separately in `settings.json`. Importing a profile does not alter the startup preferences. Invalid or oversized imports are rejected, and saves use a temporary file and atomic replacement.

## Check a new touch driver or panel

The touch path uses the Windows pointer interface and has no ASUS-specific brand check. It can work with generic, CoolTouch, and other panels whose installed drivers expose Windows touchscreen contacts. Compatibility depends on the interface supplied by the driver.

1. Stop correction, then open **Touch driver check**. This local test also works in the portable diagnostics executable without global UI Access installation.
2. Hold two fingers on the first panel inside the test, then repeat on the second. Hold one finger on each panel together. The test automatically measures the maximum simultaneous contacts; its report distinguishes per-controller contacts from distinct controllers active together.
3. Move, hold still, and lift. All markers should disappear on release. Press **Esc** to close the test and retain observations for this app launch.
4. Use **Save diagnostic report** in the same running app. The report includes the last test, physical pointer controllers, raw digitizer HID collections and VID/PID, advertised two-touch capacity, installed HID driver providers/versions/INF names/signing flags, HID/USB Device Manager errors, and current process/runtime counters. Driver inventory runs in the background only when requested.

A reported limit of two or more contacts is an advertised capability; observing two real contacts verifies delivery. A test performed with correction active measures injected input and is labeled accordingly. An incomplete test does not prove that hardware is single-touch. If the raw Windows mapping puts touch outside the local test window, that test cannot observe it; inspect the report and fix Windows calibration first.

Mouse-emulation-only, serial-only, or proprietary drivers that expose no Windows touch input require a compatible vendor driver or HID/multi-touch mode. The app cannot infer a second finger from a single mouse cursor. If no physical controller appears, inspect the USB data cable, Device Manager, and the vendor's driver settings. This update does not install or replace a hardware driver.

Run one touch mapper at a time. Known UPDD virtual duplicates and the app's synthetic digitizer are excluded. If UPDD still supplies virtual input or blocks physical contacts, resolve that conflict first. `Stop-UPDD.cmd` and `Start-UPDD.cmd` control the existing UPDD service with elevation; stopping the service can leave its HID filter active. Vendor removal should use the vendor's own procedure.

## Verify the Windows result

- With correction active, check targets, edges, corners, the seam, and ordinary target applications on both panels.
- Hold one finger on each panel. Move one while holding the other still; both contacts must remain present. Lift every finger and verify Active returns to zero.
- Stop and restart correction. Check that tray Exit restores original input and that reopening the installed app restores the saved setup when enabled.
- Restart Windows and confirm the app launches in the tray and resumes the verified profile. Test missing USB controllers or a changed display layout; correction should wait or show a reason for pausing.
- To investigate slowdown, export a report from the **running app** after reproducing it. Counters include injection retries, slow input reads, history merging, CPU time, memory, and garbage collections. A separate command-line diagnostics process has its own fresh runtime counters.

Queued movement is reduced to the latest positions between contact lifecycle boundaries within one message history. No extra input delay or smoothing filter is introduced. Applications needing every intermediate drawing sample should be checked for drawing fidelity as part of acceptance.

## Installation trust, removal, and limits

Setup creates a unique local code-signing certificate, signs only the two application executables, deletes the temporary nonexportable private key, and adds the public certificate to this PC's Trusted Root Certification Authorities and Trusted Publishers stores. It lasts ten years. This requires administrator approval and establishes local installation integrity, not a verified commercial publisher identity. Review the source and setup disclosure. [Microsoft UIAccess requirements](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-securityoverview).

Setup does not disable Secure Boot, enable test signing, weaken Windows' protected-location policy, or download application code. Package hashes detect accidental package corruption; they are not independent publisher authentication. Recovery metadata is stored in `%ProgramFiles%\SurroundTouch.InstallState\state.json` until removal finishes.

Use tray **Exit**, then run this package's `Uninstall.cmd` to remove the app, shortcut, and exact local certificate. Uninstall removes matching startup commands in loaded user accounts and retains profiles and reports. Disable startup in any other user accounts before removal; unloaded user registry hives are not edited. No Windows Installed Apps entry is created.

Global correction requires the signed installed UIAccess app; `SurroundTouch.Diagnostics.exe` supports read-only inspection and local tests. Secure desktops are outside scope. Apps requiring raw hardware input or rejecting injected touch may not work. Mouse-only apps do not gain two independent mouse cursors. Contact area and orientation metadata are not preserved.

The previous build was reported working on 23 October 2026 with two 10-contact CoolTouch controllers and a 2160×1920 combined desktop using equal 1080×1920 side-by-side regions after UPDD removal. Those observations are historical and specific to that PC. See `TECHNICAL.md` for implementation details and `tests/run-tests.ps1` for the default tests and optional interactive Windows test.
