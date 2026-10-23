# SurroundTouch technical notes

This is a standalone x64 Windows Forms application written in C#5 for the .NET Framework 4.x supplied with supported Windows installations. It is designed for Windows 10/11. It does not depend on UPDD, NVIDIA SDKs, a web service, or a custom device driver.

The implementation is a user-mode touch remapper. Success requires Windows to expose both controllers independently and deliver their contacts. Enumeration alone does not establish that an installed HID filter allows those contacts to reach the native pointer stack.

## Input path

1. `Native.Devices()` enumerates touch devices and records their runtime handle, stable device path, reported contact capacity, and current display rectangle.
2. `TouchEngine` creates a message-only window on a dedicated STA thread. After checking its UIAccess token, it registers that window as the global `PT_TOUCH` target and reserves **Ctrl + Alt + F10** as the stop shortcut.
3. Pointer frame history is read before retrieving the next message. Coalesced frames are processed oldest first; recently processed frame IDs are deduplicated. The source device selects the mapping. The source rectangle is refreshed from Windows when correction starts.
4. Raw physical-pixel pointer coordinates are normalized, transformed, and mapped into the assigned panel region.
5. `TouchPipeline` merges contacts from all contributing devices into complete injection frames. `Native.Inject()` submits those frames using Windows touch injection.

Identification only receives physical input: it does not initialize touch injection, feed the pipeline, or create a synthetic digitizer. Correction initializes the application-wide injection context once. Known UPDD and `VIRTUAL_DIGITIZER` paths are excluded from physical-controller selection and validation.

Windows permits one global redirection target per pointer type on a desktop. Touch injected by the process that owns that target follows normal routing instead of returning to that process's redirection window. This is the basis for avoiding a recursion loop while suppressing the original incorrectly mapped contacts. A message-only receiver avoids activation changes. [RegisterPointerInputTarget](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerpointerinputtarget).

The implementation reads history and uses `SkipPointerFrameMessages` after processing a frame. Microsoft documents the history array as newest first and warns that frame data is associated with the message currently being processed. [GetPointerFrameTouchInfoHistory](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getpointerframetouchinfohistory).

The current frame reader expects incoming source handles to be present in the device snapshot. An unknown controller stops correction rather than inventing a source mapping. Known UPDD virtual devices are excluded from selection and identification, and their runtime copies are suppressed. Other unassigned devices in the snapshot are passed through at their original coordinates. Native contact delivery must still be verified after stopping UPDD.

## Coordinate model

`Source` describes the screen-coordinate range Windows currently associates with a controller. It is runtime state, not a persisted calibration rectangle. Pointer positions use `ptPixelLocationRaw`; the corresponding display rectangle comes from `GetPointerDeviceRects`. These API coordinates must not be confused with the device's separate HIMETRIC rectangle. [POINTER_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-pointer_info), [GetPointerDeviceRects](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getpointerdevicerects).

For each axis, with source origin `s`, width `S`, crop minimum `a`, and maximum `b`:

```text
u = (raw - s) / (S - 1)            # u = 0 for a single-pixel source axis
u = clamp((u - a) / (b - a), 0, 1)
```

Flips are applied in source coordinates, followed by a clockwise rotation of 0, 90, 180, or 270 degrees. The transformed normalized coordinates map to target endpoints:

```text
pixel = targetOrigin + round(u * (targetSize - 1))
```

Rounding uses midpoint-away-from-zero. Rectangle widths/heights remain conventional exclusive extents; mapping uses the last included pixel. For a 2160×1920 desktop at `(0,0)`, equal side-by-side regions are `[0..1079]×[0..1919]` and `[1080..2159]×[0..1919]`. Negative desktop origins are supported. Crop bounds must satisfy `0 <= minimum < maximum <= 1`.

The configurable split is applied to the combined dimension after subtracting the specified bezel gap. This creates two rectangular panel regions separated by any hidden compensation pixels. Neither the physical panel's portrait orientation nor its nominal monitor resolution alone determines whether an extra rotation is required.

## Contact lifecycle and failures

`TouchPipeline` identifies contacts by `(device path, input pointer ID)` and allocates injection IDs in `1..256`. Every emitted frame includes every remaining active contact, including stationary contacts on the other controller. Missing contacts in one controller's report do not implicitly release contacts from another controller. A 25 ms engine timer sends stationary updates.

Unknown Move/Up events are ignored until a Down is observed. A repeated Down for an existing key behaves as a Move. A changed release position produces a successful Move first, then an Up at that exact position. Cancel releases at the previous injected position; capture loss can cancel by input pointer ID. Contact state changes are committed only after the injection sink returns successfully. The engine stops when a nonrecoverable sink error occurs; it does not assume that the OS still retains every contact after an injection error.

Microsoft requires press-and-hold updates, requires Up to match the previous injected position, and defines cancellation using `CANCELED` with Up/Update. Windows may return `ERROR_NOT_READY` when injection calls are too close together; this implementation retries the same frame up to 12 times with a 1 ms pause. Pressure is clamped to 0–1024. It does not currently preserve contact rectangles or orientation. [InjectTouchInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-injecttouchinput).

Stopping unregisters the global target and attempts to cancel outstanding synthetic contacts. Generic `WM_DEVICECHANGE` notifications post a controller check to the input worker; they do not unconditionally stop it when Windows creates or removes a virtual digitizer. That check and the periodic check validate only physical controller handles/rectangles. Actual controller failures, display changes, and relevant suspend/session notifications stop correction; the main window registers for WTS notifications. Stop reasons retain their specific context. A fresh start requires lifting fingers first so a partial old gesture is not resumed as a new synthetic gesture.

The app keeps a bounded per-user `status.log` containing status messages and no touch coordinates. Diagnostic reports include its last 40 lines to distinguish controller errors from user stops and display/session notifications.

## Installation design

`app.manifest` requests `asInvoker` with `uiAccess="true"`. The diagnostics manifest requests `uiAccess="false"`. A portable unsigned UIAccess executable is not a supported launch mode. Windows requires the UIAccess manifest, an accepted Authenticode signature, and a protected location; merely launching an unsigned executable as administrator is not this application's deployment solution. [UIAccess security requirements](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-securityoverview).

`Setup.ps1` runs under 64-bit Windows PowerShell 5.1. The installer stages files under Program Files, applies a restricted directory ACL, creates a unique local RSA code-signing certificate, signs the two executables, destroys its private key, and trusts the public certificate in LocalMachine Root and TrustedPublisher. It validates the final signatures and publishes the prepared directory. The Start menu shortcut launches the installed executable as the normal user.

Recovery metadata is a protected filesystem record at `%ProgramFiles%\SurroundTouch.InstallState\state.json`, normally `C:\Program Files\SurroundTouch.InstallState\state.json`. It records product identity, the exact signing-certificate identity/hash, installation path, and the exact staging path. Setup writes and flushes `state.pending.json`, renames it into place, then reads it back before adding trust. Administrator and SYSTEM have full control; ordinary users receive read/execute access. Uninstall retains the recovery record until the owned application files, staging files, and exact certificate have been removed.

This version makes no application-registration writes to HKLM and creates no Windows Installed Apps entry. Removal is through the package's `Uninstall.cmd`; keep the package. Legacy registration keys from an older installer are detected and left untouched. The filesystem rewrite addresses an earlier installation attempt that failed to create its HKLM recovery key; the revised installer succeeded on 23 October 2026 at 17:56 UTC (21:56 Dubai time).

The trust is local to this computer and is explicitly disclosed by setup. The ten-year certificate has no external publisher identity. The package hash file protects against accidental corruption, not an attacker who can replace the whole package. The installer does not weaken secure UIAccess location policy, install a driver/service, enable test signing, or change Secure Boot. Uninstall targets the recorded certificate and owned files rather than searching for certificates by a broad name.

Rebuilding changes executable bytes and invalidates old executable signatures. Rebuild the package, uninstall the old installation, and reinstall it to obtain newly signed installed copies. Keep `package-hashes.json` synchronized by running `build.ps1` after changing an executable or `Setup.ps1`.

`DriverControl.ps1`, invoked by `Stop-UPDD.cmd` and `Start-UPDD.cmd`, uses elevated Windows service-control calls for the existing `updd` service. It does not modify startup type or uninstall a driver. Stopping the service can leave the vendor HID filter active, so successful service control is not proof that physical HID contacts are available. These helpers are separate from the application installer.

## Build and tests

From the package directory, in Windows PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\run-tests.ps1
```

The build uses `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`; no NuGet download or full Visual Studio installation is needed. Both executable variants use the same source and different manifests.

The test runner compiles both test programs and runs **only the pure core tests by default**. Output goes to a unique `SurroundTouch-tests-*` directory under the Windows temporary directory, or the directory provided with `-OutputDirectory`. It leaves binaries and reports there for inspection; it does not recursively delete anything.

`CoreTests.cs` covers endpoint mapping, negative origins, crop/rotation/flips, persistence of settings without runtime source geometry, independent same-number IDs on different controllers, complete frames, keepalive, moved releases, cancellation/capture loss, ID reuse, limits, validation, and injection-sink exceptions. The reported result is **606 passing assertions**.

`WindowsIntegration.cs` is a separate interactive integration test. It opens a visible test window, verifies its injection points are unobscured, injects two synthetic contacts, checks down/move/up counts and simultaneous contacts, then closes. It does not test physical controller capture, UIAccess registration, or real-panel accuracy. To run it deliberately on the interactive desktop, first stop SurroundTouch and other remappers, keep the test window uncovered, then use:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\run-tests.ps1 -RunIntegration
```

The explicit switch performs input injection into the test window. The default command performs no desktop injection.

For a read-only diagnostic report:

```powershell
.\SurroundTouch.Diagnostics.exe --diagnostics "$env:TEMP\SurroundTouch-diagnostics.txt"
```

## Validation boundary and target hardware

At the 23 October 2026 validation snapshot, core tests, compilation, x64 interop structure layout, and device enumeration had been checked. The revised installation succeeded at 17:56 UTC. `Get-AuthenticodeSignature` returned Valid for both installed executables. Both application and recovery directories had administrator ownership and protected ACLs. The exact public certificate was present in LocalMachine Root and TrustedPublisher, and its temporary LocalMachine My certificate/private-key entry was absent.

Installed `SurroundTouch.exe --diagnostics` at 17:57 UTC reported **UI Access token: True**, one 2160×1920 logical display, two CoolTouch devices with 10-contact capacity each, and two UPDD virtual devices with 64-contact capacity each. The main window opened with title `SurroundTouch` and `Responding=True`; the Start menu shortcut resolved to the installed Program Files executable. Identification subsequently failed with the UPDD application service stopped while its HID filter remained running.

The user authorized vendor uninstallation. At 18:00:29 UTC on 23 October 2026, the vendor log reported success, all components removed, and no restart required. The UPDD folder, uninstall registration, application service, and `upddhx` registration are absent; three residual driver-service registrations are stopped. No UPDD virtual touch devices remain. Physical input became available after removal.

The subsequent right-identification attempt stopped immediately. Code review found unnecessary injection initialization during Identify and blanket stopping on generic device broadcasts. The revised code removes both behaviors, excludes virtual devices from physical validation, and logs specific stop reasons. The default runner was rerun successfully: 606 core assertions passed, and the Windows integration executable compiled without being run.

The updated application was reinstalled successfully at **18:08:33 UTC** with a valid signature and opened normally. Installed diagnostics at **18:08:55 UTC** reported UIAccess True and exactly two CoolTouch controllers. The user confirmed both identifications, accurate simultaneous contacts with Active=2, accurate edges/corners/seam, clean release to Active=0, and accurate touch in ordinary Windows applications on both screens. The working profile is saved and backed up as `tested-profile.json` in the package.

Earlier restricted-environment injection returned Windows error 6, and computer-use tooling rejected the separate WindowsIntegration test and UI inspection. No automated desktop integration success is claimed; the final hardware and application checks were performed and confirmed by the user. Stop/restart and reboot recovery were not separately retested. Some target applications can reject injected touch; mouse-only applications do not gain independent cursors. Secure desktop/logon operation is outside scope.
