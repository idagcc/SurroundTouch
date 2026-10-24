# SurroundTouch technical notes

This is a standalone x64 Windows Forms application written in C#5 for the .NET Framework 4.x supplied with supported Windows installations. It is designed for Windows 10/11. It does not depend on UPDD, NVIDIA SDKs, a web service, or a custom device driver.

The implementation is a user-mode touch remapper. Success requires Windows to expose both controllers independently and deliver their contacts. Enumeration alone does not establish that an installed HID filter allows those contacts to reach the native pointer stack.

## Input path

1. `Native.Devices()` enumerates touch devices and records their runtime handle, stable device path, reported contact capacity, and current display rectangle.
2. `TouchEngine` creates a message-only window on a dedicated STA thread. After checking its UIAccess token, it registers that window as the global `PT_TOUCH` target and reserves **Ctrl + Alt + F10** as the stop shortcut.
3. Pointer frame history is read before retrieving the next message. History is requested only for coalesced updates; ordinary frames use a reusable 256-contact buffer. History storage is reused, rows are visited oldest first, and recently processed frame IDs are deduplicated. TouchFrameBatch merges consecutive move-only rows to their newest per-controller/pointer positions and flushes before every lifecycle boundary and before returning from the current message. The source device selects the mapping. The source rectangle is refreshed from Windows when correction starts.
4. Raw physical-pixel pointer coordinates are normalized, transformed, and mapped into the assigned panel region.
5. `TouchPipeline` merges contacts from all contributing devices into complete injection frames. `Native.Inject()` submits those frames using Windows touch injection.

Identification only receives physical input: it does not initialize touch injection, feed the pipeline, or create a synthetic digitizer. Correction initializes the application-wide injection context once. Known UPDD and `VIRTUAL_DIGITIZER` paths are excluded from physical-controller selection and validation.

Windows permits one global redirection target per pointer type on a desktop. Touch injected by the process that owns that target follows normal routing instead of returning to that process's redirection window. This is the basis for avoiding a recursion loop while suppressing the original incorrectly mapped contacts. A message-only receiver avoids activation changes. [RegisterPointerInputTarget](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerpointerinputtarget).

The implementation reads history and uses `SkipPointerFrameMessages` after processing a frame. Microsoft documents the history array as newest first and warns that frame data is associated with the message currently being processed. [GetPointerFrameTouchInfoHistory](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getpointerframetouchinfohistory).

The current frame reader expects incoming source handles to be present in the device snapshot. An unknown controller stops correction rather than inventing a source mapping. Known UPDD virtual devices are excluded from selection and identification, and their runtime copies are suppressed. Other unassigned devices in the snapshot are passed through at their original coordinates. Native contact delivery must still be verified after stopping UPDD.

## Coordinate model

`Source` describes the screen-coordinate range Windows currently associates with a controller. It is resolved from Windows at runtime and never trusted as a calibration rectangle. RoleProfile also persists its last confirmed source geometry for automatic-restore comparison; restore is blocked if the current Windows geometry differs. Pointer positions use `ptPixelLocationRaw`; the corresponding display rectangle comes from `GetPointerDeviceRects`. These API coordinates must not be confused with the device's separate HIMETRIC rectangle. [POINTER_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-pointer_info), [GetPointerDeviceRects](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getpointerdevicerects).

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

`TouchPipeline` identifies contacts by `(device path, input pointer ID)` and allocates injection IDs in `1..256`. Every emitted frame includes every remaining active contact, including stationary contacts on the other controller. Missing contacts in one controller's report do not implicitly release contacts from another controller. The receiver timer is configured for 1,000 ms while idle and 30 ms while contacts are active. It sends stationary updates only when at least 25 ms have passed since successful injection. It validates physical controllers every five seconds and immediately on relevant device notifications. Identification uses a 200 ms timeout-check timer.

Unknown Move/Up events are ignored until a Down is observed. A repeated Down for an existing key behaves as a Move. A changed release position produces a successful Move first, then an Up at that exact position. Cancel releases at the previous injected position; capture loss can cancel by input pointer ID. Contact state changes are committed only after the injection sink returns successfully. The engine stops when a nonrecoverable sink error occurs; it does not assume that the OS still retains every contact after an injection error.

Microsoft requires press-and-hold updates, requires Up to match the previous injected position, and defines cancellation using `CANCELED` with Up/Update. Windows may return `ERROR_NOT_READY` when injection calls are too close together; this implementation retries the same frame up to 12 times with a 1 ms pause. Pressure is clamped to 0–1024. It does not currently preserve contact rectangles or orientation. [InjectTouchInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-injecttouchinput).

Stop posts to the receiver without joining the worker on the UI thread. Final disposal allows a bounded two-second join; callbacks from old engine generations are ignored. Stopping unregisters the global target and attempts to cancel outstanding synthetic contacts. Generic `WM_DEVICECHANGE` notifications post a controller check to the input worker; they do not unconditionally stop it when Windows creates or removes a virtual digitizer. That check and the periodic check validate only physical controller handles/rectangles. Actual controller failures, display changes, and relevant suspend/session notifications stop correction; the main window registers for WTS notifications. Stop reasons retain their specific context. A fresh start requires lifting fingers first so a partial old gesture is not resumed as a new synthetic gesture.

The app keeps a bounded per-user `status.log` containing status messages and no touch coordinates. Diagnostic reports include its last 40 lines to distinguish controller errors from user stops and display/session notifications.

## Startup, profiles, diagnostics, and performance update (24 October 2026)

The installed UIAccess executable can register its quoted path plus `--startup` under the current user's `Software\Microsoft\Windows\CurrentVersion\Run`, value `SurroundTouch`. Only the installed Program Files UIAccess app can enable this registration. Disabling removes only the matching command. Reopening the app broadcasts a registered window message to show its existing tray instance; automatic duplicate launches exit quietly. The registry entry is added only through the explicit checkbox, and is not rewritten during startup. [Windows Run keys](https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys).

`settings.json` stores the independent ResumeCorrection preference. The last saved `profile.json` remains schema version 1 and adds optional per-role SourceX/SourceY/SourceWidth/SourceHeight. Profiles without these fields still load, but need a manual Start/Save in the new build before automatic correction can resume. Source geometry is always fetched again from Windows, not used as trusted runtime mapping. Named imports are bounded to 64 KiB and validate version, split, gap, and calibration; named and default writes use flushed temporary files and atomic replacement. Imports do not change startup preferences.

On launch, optional restore retries enumeration every three seconds for up to 60 seconds, matching exact controller paths and saved display identity, bounds, and both source display rectangles. It does not infer physical sides from device enumeration order. Detected UPDD duplicates block automatic correction. Failure displays a reason; Stop cancels the retry. Display changes, session lock/disconnect/logoff, and suspend cancel pending restore and stop active correction. Automatic restore after those mid-session events is intentionally not attempted.

The tray holds the app after close/minimize; Exit disposes input and restores physical routing. Uninstall checks for running installed copies and asks for tray Exit. It removes only the exact startup command in loaded user hives; unloaded hives are left untouched and users should disable their startup entries before removal.

The new DriverDiagnostics inventory uses raw input digitizer collections (usage page 0D), Windows system digitizer metrics, pointer controllers, Win32_PnPSignedDriver, and Win32_PnPEntity error codes. WMI queries are requested only during report export, on a background task, with five-second query timeouts, bounded output, and per-inventory error handling. No WMI or periodic driver inventory runs in the input loop. A command-line report runs in a separate process and cannot report the main app's runtime counters or in-memory observations. [Raw HID metadata](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rid_device_info_hid), [driver inventory](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/whqlprov/win32-pnpsigneddriver), [device errors](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-pnpentity).

The local test tracks observed contacts per source controller and distinct simultaneous sources, separately from advertised capability. Corrected/injected input is explicitly labeled as insufficient to verify a physical driver's multi-touch capability. Results persist in memory until the process exits and are included in report export after the test closes. No coordinates are saved. Local test drawing is throttled to about 30 fps with a bounded 600-point trail.

TouchPipeline reuses its validation/frame scratch lists and contact-ID bitmap, makes a release-position prelude copy only when required, builds sorted output arrays directly, and caches read-only stationary frames until state changes. Injection marshaling buffers are cached per contact count and per thread. Sinks must treat emitted frames as read-only. State is still committed only after a successful send. Movement counter UI updates are limited to four per second with at most one pending activity callback; contact count changes may notify sooner. The receiver preserves lifecycle events and merges only movement within an already-coalesced Windows history read; drawing applications should be checked for acceptable intermediate-sample fidelity.

RuntimeStats records aggregate input/injection/merged-frame counts, native busy retries, reads exceeding 10 ms, maximum read duration, process CPU time/memory, and GC collection counts. It does not collect coordinates or target application data. Counters are process-lifetime totals; periodic export is not enabled. Five alternating Mono benchmark runs on this Mac showed a two-contact managed movement median of 297.68 ms before and 253.31 ms after for 200,000 calls (Gen0 collections 74 versus 33); 1,000,000 held-contact Tick calls measured 119.61 ms versus 2.16 ms (Gen0 55 versus 0). These are managed microbenchmarks, not Windows timing, idle CPU measurements, native injection performance, or end-to-end touch latency. This update has not been installed or physically validated on Windows.

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

The test runner compiles the core, runtime, and interactive integration tests, and runs **only pure managed tests by default**. Output goes to a unique `SurroundTouch-tests-*` directory under the Windows temporary directory, or the directory provided with `-OutputDirectory`. It leaves binaries and reports there for inspection; it does not recursively delete anything.

`CoreTests.cs` covers endpoint mapping, negative origins, crop/rotation/flips, persistence of settings without runtime source geometry, independent same-number IDs on different controllers, complete frames, keepalive, moved releases, cancellation/capture loss, ID reuse, limits, validation, and injection-sink exceptions. The core suite reports **606 passing assertions**. RuntimeTests.cs adds **50 passing assertions** covering history batches, lifecycle ordering, failed flushes, named profile roundtrips and atomic replacement, invalid imports, restore identity/geometry checks, observed multi-touch, virtual-device exclusion, and x64 layouts. No Windows API is called by these test entry points. Add `-RunBenchmark` to run CoreBenchmark.cs on Windows. On macOS/Linux with Mono and Python 3 installed, `sh tests/run-tests.sh` compiles both Windows manifest variants and all test entry points, then runs only the two pure test suites.

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
