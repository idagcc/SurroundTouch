# SurroundTouch

SurroundTouch is a Windows desktop application that assigns two independent touchscreen controllers to two regions of one combined display. For your NVIDIA Surround desktop, the left controller should cover the left panel and the right controller should cover the right panel, while both remain usable together.

The application is installed and working on this PC. The user confirmed accurate simultaneous touch on both physical CoolTouch panels, accurate edges and the center seam, release without stuck contacts, and accurate touch in ordinary Windows applications on both screens.

## Start here

**From a Git checkout:** run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1` before `Install.cmd`. The repository contains source and build scripts; executables, generated package checksums, signing material, logs, and local hardware profiles are excluded from Git. To run the pure tests without desktop input injection, use `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\run-tests.ps1`.

Use a mouse and keyboard for the first setup. Keep both screens' USB touch connections connected as well as their video cables.

**On this PC, setup and physical testing are complete. Keep SurroundTouch running for correction. After restarting Windows, open it from Start and press Start correction with all fingers lifted; the tested profile is already saved.**

An earlier attempt to identify the right panel stopped immediately after the left succeeded. The revised build avoids creating an injection device during Identify and checks real controllers before reacting to generic device notifications. The user has now confirmed that both identification steps succeed.

The update was installed at **22:08:33 Dubai time** on 23 October 2026. Its signature is valid, its normal app window opened, and installed diagnostics at 22:08:55 confirmed **UI Access: True** and **exactly two CoolTouch controllers**, with no UPDD or synthetic digitizer listed before identification.

1. On another PC where UPDD is still installed, run `Stop-UPDD.cmd` and approve its Windows elevation prompt before testing. See the UPDD note below if physical touch remains unavailable. Skip this step on this PC, where UPDD has been uninstalled.
2. Open `Install.cmd` from this package on the target PC. Read its installation disclosure and approve Windows elevation if you accept it. Setup installs the application into `C:\Program Files\SurroundTouch` and creates a Start menu shortcut; it does not start correction.
3. Open **SurroundTouch** from the installed Start menu shortcut. Opening the unsigned package copy is not the installed launch path. The window should report that Windows UI Access is enabled.
4. Select the combined display. For the configuration observed on this PC, use **2160 × 1920**, **Side by side**, **50%**, and **0 px bezel gap**. That assigns **1080 × 1920** pixels to each panel.
5. Leave rotation at **0°**, both flips off, and each input range at **0–100%** initially. A portrait panel does not automatically require 90° here: Windows or the controller may already supply rotated coordinates.
6. Click **Identify by touch** for **LEFT SCREEN**, then tap the physical left panel once. Lift your finger. Repeat for **RIGHT SCREEN** and the physical right panel. Identification times out after 20 seconds. The two roles must use different controllers.
7. Lift every finger, click **Start correction**, then **Test touch**. Follow the acceptance checks below. Save the profile once the assignments are correct.

**Stop** or **Ctrl + Alt + F10** ends correction. Closing the main application also stops it. Windows then receives its original touch routing again; this restores the previous mapping, including any previous inaccuracy. **Esc** closes the touch test without stopping correction.

The app must remain running while correction is wanted. It does not add automatic startup, a background service, or a device driver.

`tested-profile.json` in the local delivery package is a backup for this PC's tested controllers and display; it is not committed to Git. To restore it, close SurroundTouch, copy it to `%LOCALAPPDATA%\SurroundTouch\profile.json`, and reopen the installed app. Re-identify the panels if USB connections or display geometry change.

## UPDD on this PC

The initial inspection on 23 October 2026 found two physical **CoolTouch** controllers reporting 10 contacts each and two **UPDD virtual** controllers reporting 64 contacts each. UPDD was in evaluation mode with a 200-touch limit, and both controller assignments were set to segment 1. Those virtual controllers are additional software devices, not two more physical panels.

Run only one touch mapper at a time. SurroundTouch excludes known UPDD virtual controllers from screen selection, rejects identification through them, and suppresses their duplicate runtime events. Identify the physical CoolTouch controllers after UPDD is stopped. If identification reports that UPDD is still supplying touch, or tapping produces no events, finish resolving the UPDD/native-driver conflict before trusting the result.

Stopping the UPDD service alone did not resolve identification on this PC: the service was stopped while its HID filter remained running. With the user's authorization, the vendor uninstaller was then run. Its log reported **success, all components removed, and no restart required** on 23 October 2026 at 22:00:29 Dubai time (18:00:29 UTC). The UPDD folder, uninstall entry, application service, and `upddhx` registration are gone. Three residual driver-service registrations remain stopped.

Diagnostics after removal showed **UI Access: True**, the **2160×1920** display, and **two physical CoolTouch controllers**. No UPDD virtual touch devices remained. After the application update, the user confirmed both physical controller assignments and accurate simultaneous corrected touch.

For PCs where UPDD remains installed, `Stop-UPDD.cmd` and `Start-UPDD.cmd` control its service with administrator approval without changing startup type; a reboot can restart it. Stop SurroundTouch before restarting UPDD. These helpers cannot restore UPDD after vendor uninstallation and are no longer needed on this PC.

## Check that the result is correct

Use the full-screen **Test touch** view with correction active:

- Tap the target centers on each panel, then touch near all four edges and corners of each physical screen. The yellow marker should stay under your finger throughout that panel, including near the center seam.
- Hold one finger on each panel simultaneously. **Active** should show 2. Move one finger while keeping the other still; both markers should remain present in the correct panels.
- Drag on each panel, hold still for several seconds, and lift. **Active** should return to 0 after all fingers are lifted, without a stuck marker.
- Try more fingers up to the capabilities of the panels and target application. Verify that touching one panel does not cancel a held contact on the other.
- Close the test with Esc, stop correction, and confirm that the old Windows behavior returns. Start again with all fingers lifted and repeat a two-panel check.

If the panels are exchanged, stop and use **Swap screens**. If motion goes in the wrong direction, stop and adjust that panel's rotation or flips, then repeat the edge checks. Rotations are clockwise; flips are applied before rotation. Change one setting at a time.

Use a nonzero **Bezel gap** only when NVIDIA's combined desktop actually includes hidden pixels for bezel compensation. An ordinary physical plastic bezel does not itself require a pixel gap. Input range controls are optional crop/calibration bounds; keep them at 0–100% unless measurements show that the controller uses a smaller portion of its reported range.

## Installation and trust

Windows requires UI Access for the API used to receive touch across applications. This includes a trusted Authenticode signature and a protected installation location. [Microsoft: pointer redirection](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerpointerinputtarget), [Microsoft: UIAccess requirements](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-securityoverview).

Setup creates a unique local code-signing certificate, signs the two application executables, deletes the temporary nonexportable private key, and places the public certificate in this PC's **Trusted Root Certification Authorities** and **Trusted Publishers** stores. The certificate lasts ten years. This is a machine-level trust change and requires administrator approval. It establishes local installation integrity; it is not a signature from a verified commercial publisher. Install only a package whose source you trust.

Setup does not disable Secure Boot, enable test signing, weaken Windows' protected-location policy, or download additional code. `package-hashes.json` detects package corruption; its checksums are not independent publisher authentication.

Keep this package for removal or recovery. To remove the application, close its main window and run the package's `Uninstall.cmd`. This version does not create a Windows Installed Apps entry. Uninstall removes the application, shortcut, and the exact locally created certificate. It retains per-user profiles and exported diagnostics. The profile is stored at `%LOCALAPPDATA%\SurroundTouch\profile.json`.

Setup saves its protected recovery record at `C:\Program Files\SurroundTouch.InstallState\state.json` before adding certificate trust. The record identifies the exact certificate and staging directory for cleanup. Do not delete it manually while the application or its local certificate remains installed. On systems with a different Program Files location, setup uses that location instead.

## Diagnostics, limits, and current validation

`SurroundTouch.Diagnostics.exe` can open the UI, inspect devices, and display the local touch test without installation. It **cannot start global correction or global identification** because its manifest does not request UI Access. Use **Save diagnostic report** to export device identities, reported ranges, display geometry, privilege status, and recent status messages. The bounded `%LOCALAPPDATA%\SurroundTouch\status.log` stores app status, not touch coordinates.

Correction applies to the current interactive Windows desktop. Logon and the UAC secure desktop are outside this application's scope. Applications that require raw hardware input or reject injected touch, including some games, may not work. Ordinary mouse-only applications generally have one primary mouse interaction; remapping cannot make them support two independent mouse cursors. Contact area and orientation metadata are not preserved by this version.

After a display change, disconnect, suspend/session change, or input error, review the status, refresh devices, and verify the assignments before restarting. Do not assume a saved profile remains correct after moving USB connections or changing Surround resolution.

Validation so far:

- **Passed:** C#5 compilation, 606 core assertions, x64 interop and enumeration checks, and installation on 23 October 2026 at 21:56 Dubai time (17:56 UTC). Both installed signatures are valid; application/recovery directories have protected administrator-owned permissions; the exact public certificate is present in both trust stores and its temporary private certificate is absent. Installed diagnostics at 21:57 reported **UI Access token: True**, and the main window opened.
- **Physical checks passed, confirmed by the user:** both controllers identify; both markers follow fingers accurately with Active=2; edges, corners and seam are accurate; lifting returns Active to 0 without stuck contacts; ordinary Windows applications respond accurately on both screens.
- **Not separately verified:** stopping/restarting correction, recovery after reboot, and every possible target application. Earlier restricted-environment and automated UI test limitations do not replace the successful hands-on results above.

Source code, build instructions, the optional Windows integration test, and API references are in `TECHNICAL.md`.
