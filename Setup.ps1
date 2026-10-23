# Local installer for SurroundTouch. Review this script before approving elevation.
# It never downloads code, enables test-signing, disables Secure Boot, installs a
# driver/service, or weakens Windows' secure UIAccess location policy.
[CmdletBinding()]
param(
    [ValidateSet('Install', 'Uninstall')][string]$Mode = 'Install',
    [switch]$Elevated,
    [switch]$NoResultDialog
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$productId = 'SurroundTouch-28F2E5B7-ED34-46D9-A5C2-B179E60DD1A8'
$programFilesPath = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
$installDirectory = Join-Path $programFilesPath 'SurroundTouch'
$stateDirectory = Join-Path $programFilesPath 'SurroundTouch.InstallState'
$stateFile = Join-Path $stateDirectory 'state.json'
$pendingStateFile = Join-Path $stateDirectory 'state.pending.json'
$legacyRegistryKeys = @('HKLM:\SOFTWARE\SurroundTouch', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SurroundTouch')
$shortcutPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonPrograms)) 'SurroundTouch.lnk'
$ownedFileNames = @('SurroundTouch.exe', 'SurroundTouch.Diagnostics.exe', 'Setup.ps1', 'signing-certificate.cer')
$packageDirectory = $PSScriptRoot
$windowsPowerShell = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)) 'System32\WindowsPowerShell\v1.0\powershell.exe'
$script:failureDetails = ''

function Show-Result([string]$Message, [bool]$Failure = $false) {
    if (-not $NoResultDialog) {
        Add-Type -AssemblyName System.Windows.Forms
        $icon = [Windows.Forms.MessageBoxIcon]::Information
        if ($Failure) { $icon = [Windows.Forms.MessageBoxIcon]::Error }
        [void][Windows.Forms.MessageBox]::Show($Message, 'SurroundTouch setup', [Windows.Forms.MessageBoxButtons]::OK, $icon)
    }
    Write-Host $Message
}

function Assert-LocalSafePath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if ($fullPath -notmatch '^[A-Za-z]:\\' -or $fullPath.StartsWith('\\')) {
        throw ('Only a local drive path is allowed: ' + $Path)
    }
    $drive = New-Object IO.DriveInfo ([IO.Path]::GetPathRoot($fullPath))
    if ($drive.DriveType -ne [IO.DriveType]::Fixed) { throw ('Use a local fixed disk: ' + $Path) }
    $part = $fullPath
    while ($part) {
        if (Test-Path -LiteralPath $part) {
            $entry = Get-Item -LiteralPath $part -Force
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw ('Links, junctions, and redirected paths are not allowed during setup: ' + $part)
            }
        }
        $parent = [IO.Path]::GetDirectoryName($part)
        if ($parent -eq $part) { break }
        $part = $parent
    }
    return $fullPath
}

function Get-BytesHash([byte[]]$Bytes) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($Bytes))).Replace('-', '') }
    finally { $algorithm.Dispose() }
}

function Set-ProtectedDirectoryAcl([string]$Path) {
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $administrators = New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-544'
    $system = New-Object Security.Principal.SecurityIdentifier 'S-1-5-18'
    $users = New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545'
    $acl.SetOwner($administrators)
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    foreach ($identity in @($administrators, $system)) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($identity, [Security.AccessControl.FileSystemRights]::FullControl, $inherit, $propagation, $allow)))
    }
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($users, [Security.AccessControl.FileSystemRights]::ReadAndExecute, $inherit, $propagation, $allow)))
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Assert-NoLegacyRegistration {
    foreach ($path in $legacyRegistryKeys) {
        if (Test-Path -LiteralPath $path) {
            throw ('A record from the older registry-based installer exists. Use its matching uninstaller first. This setup leaves that registry key untouched: ' + $path)
        }
    }
}

function Assert-StateDirectory {
    $resolved = Assert-LocalSafePath $stateDirectory
    if ($resolved -ne (Join-Path $programFilesPath 'SurroundTouch.InstallState')) { throw 'Unexpected recovery directory path.' }
    if (Test-Path -LiteralPath $stateDirectory) {
        foreach ($entry in @(Get-ChildItem -LiteralPath $stateDirectory -Force)) {
            if ($entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $entry.Name -notin @('state.json', 'state.pending.json')) {
                throw ('Unexpected file or link in the recovery directory. It was left untouched: ' + $entry.FullName)
            }
        }
    }
}

function Remove-OwnedStateDirectory {
    Assert-StateDirectory
    if (Test-Path -LiteralPath $stateDirectory) {
        foreach ($path in @($stateFile, $pendingStateFile)) {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
        }
        Remove-Item -LiteralPath $stateDirectory -Force
    }
}

function Save-FailureLog([string]$Details) {
    # A unique CreateNew file cannot overwrite any existing file or hard link.
    # Temp storage is used only for human-readable diagnostics, never trusted state.
    $logDirectory = Assert-LocalSafePath ([IO.Path]::GetTempPath())
    $logPath = Join-Path $logDirectory ('SurroundTouch-setup-error-' + [Guid]::NewGuid().ToString('N') + '.log')
    $stream = New-Object IO.FileStream($logPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes(('UTC: ' + [DateTime]::UtcNow.ToString('o') + "`r`nMode: " + $Mode + "`r`n" + $Details))
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    } finally { $stream.Dispose() }
    return $logPath
}

function Assert-AppDirectory([string]$Path) {
    $resolved = Assert-LocalSafePath $Path
    $parent = [IO.Path]::GetDirectoryName($resolved)
    $name = [IO.Path]::GetFileName($resolved)
    if (-not $parent.Equals($programFilesPath, [StringComparison]::OrdinalIgnoreCase) -or
        ($name -ne 'SurroundTouch' -and $name -notmatch '^\.SurroundTouch-stage-[0-9a-f]{32}$')) {
        throw ('Refusing to modify an unexpected installation path: ' + $resolved)
    }
    if (Test-Path -LiteralPath $resolved) {
        foreach ($entry in @(Get-ChildItem -LiteralPath $resolved -Force)) {
            if ($entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $entry.Name -notin $ownedFileNames) {
                throw ('Unexpected file or link in the installation directory; nothing was recursively removed: ' + $entry.FullName)
            }
        }
    }
}

function Remove-OwnedDirectory([string]$Path) {
    Assert-AppDirectory $Path
    if (Test-Path -LiteralPath $Path) {
        # Deliberately no recursive deletion: only these four owned ordinary files.
        foreach ($name in $ownedFileNames) {
            $file = Join-Path $Path $name
            if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
        }
        Remove-Item -LiteralPath $Path -Force
    }
}

function Assert-Certificate([Security.Cryptography.X509Certificates.X509Certificate2]$Certificate, $State) {
    if ($Certificate.Thumbprint -ne $State.SigningThumbprint -or
        $Certificate.Subject -ne $State.CertificateSubject -or
        (Get-BytesHash $Certificate.RawData) -ne $State.CertificateSha256) {
        throw 'Certificate identity did not match the protected installation record. No certificate was removed.'
    }
}

function Remove-OwnedTrust($State) {
    # Validate both stores before changing either. Never search/remove by a broad name.
    foreach ($storeName in @('TrustedPublisher', 'Root')) {
        $certificatePath = 'Cert:\LocalMachine\' + $storeName + '\' + $State.SigningThumbprint
        if (Test-Path -LiteralPath $certificatePath) {
            Assert-Certificate (Get-Item -LiteralPath $certificatePath) $State
        }
    }
    foreach ($storeName in @('TrustedPublisher', 'Root')) {
        $certificatePath = 'Cert:\LocalMachine\' + $storeName + '\' + $State.SigningThumbprint
        if (Test-Path -LiteralPath $certificatePath) { Remove-Item -LiteralPath $certificatePath -Force }
    }
}

function Remove-OwnedShortcut {
    if (Test-Path -LiteralPath $shortcutPath) {
        [void](Assert-LocalSafePath $shortcutPath)
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        if ($shortcut.TargetPath -ne (Join-Path $installDirectory 'SurroundTouch.exe')) {
            throw 'The Start menu shortcut now belongs to another target. It was left untouched.'
        }
        Remove-Item -LiteralPath $shortcutPath -Force
    }
}

function Get-InstallationState {
    Assert-StateDirectory
    if (-not (Test-Path -LiteralPath $stateFile -PathType Leaf)) { throw 'No completed protected SurroundTouch installation record was found. No certificate trust is added until this record is saved.' }
    if ((Get-Item -LiteralPath $stateFile).Length -gt 32768) { throw 'The installation recovery record is unexpectedly large.' }
    $state = Get-Content -LiteralPath $stateFile -Encoding UTF8 -Raw | ConvertFrom-Json
    if ($state.ProductId -ne $productId -or $state.SchemaVersion -ne 1 -or
        $state.InstallDirectory -ne $installDirectory -or
        [IO.Path]::GetDirectoryName($state.StagingDirectory) -ne $programFilesPath -or
        [IO.Path]::GetFileName($state.StagingDirectory) -notmatch '^\.SurroundTouch-stage-[0-9a-f]{32}$' -or
        $state.SigningThumbprint -notmatch '^[0-9A-F]{40}$' -or
        $state.CertificateSha256 -notmatch '^[0-9A-F]{64}$' -or
        $state.CertificateSubject -notmatch '^CN=SurroundTouch Local Install [0-9a-f]{32}$') {
        throw 'The protected installation record is incomplete or does not match this application.'
    }
    return $state
}

function Write-InstallationState($CertificateState, [string]$StagePath) {
    Assert-StateDirectory
    # Flush, then rename within the protected directory: readers see either the
    # previous complete JSON file or the new complete JSON file, never half of it.
    $state = [ordered]@{
        SchemaVersion = 1
        ProductId = $productId; InstallDirectory = $installDirectory
        StagingDirectory = $StagePath
        SigningThumbprint = $CertificateState.SigningThumbprint
        CertificateSubject = $CertificateState.CertificateSubject
        CertificateSha256 = $CertificateState.CertificateSha256
        RecordedUtc = [DateTime]::UtcNow.ToString('o')
    }
    if (Test-Path -LiteralPath $pendingStateFile) { Remove-Item -LiteralPath $pendingStateFile -Force }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($state | ConvertTo-Json -Compress))
    $stream = New-Object IO.FileStream($pendingStateFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    } finally { $stream.Dispose() }
    if (Test-Path -LiteralPath $stateFile) { [IO.File]::Replace($pendingStateFile, $stateFile, $null) }
    else { [IO.File]::Move($pendingStateFile, $stateFile) }
    $readBack = Get-InstallationState
    if ($readBack.SigningThumbprint -ne $CertificateState.SigningThumbprint -or $readBack.CertificateSha256 -ne $CertificateState.CertificateSha256 -or $readBack.StagingDirectory -ne $StagePath) {
        throw 'The recovery record did not match after being saved. No certificate trust was added.'
    }
}

function Invoke-Uninstall {
    Assert-NoLegacyRegistration
    $state = Get-InstallationState
    Assert-AppDirectory $installDirectory
    Assert-AppDirectory $state.StagingDirectory
    $running = @(Get-CimInstance Win32_Process -Filter "Name = 'SurroundTouch.exe' OR Name = 'SurroundTouch.Diagnostics.exe'" |
        Where-Object { $_.ExecutablePath -and ([IO.Path]::GetDirectoryName($_.ExecutablePath) -eq $installDirectory) })
    if ($running.Count -gt 0) { throw 'Close the SurroundTouch application window completely, then run uninstall again.' }
    foreach ($storeName in @('TrustedPublisher', 'Root', 'My')) {
        $certificatePath = 'Cert:\LocalMachine\' + $storeName + '\' + $state.SigningThumbprint
        if (Test-Path -LiteralPath $certificatePath) { Assert-Certificate (Get-Item -LiteralPath $certificatePath) $state }
    }
    Remove-OwnedShortcut
    Remove-OwnedDirectory $installDirectory
    Remove-OwnedDirectory $state.StagingDirectory
    Remove-OwnedTrust $state
    $temporaryKeyPath = 'Cert:\LocalMachine\My\' + $state.SigningThumbprint
    if (Test-Path -LiteralPath $temporaryKeyPath) { Remove-Item -LiteralPath $temporaryKeyPath -DeleteKey -Force }
    Remove-OwnedStateDirectory
    Show-Result 'SurroundTouch and its exact local signing certificate have been removed. Your per-user settings and exported diagnostics were retained.'
}

function Invoke-Install {
    Assert-NoLegacyRegistration
    if (Test-Path -LiteralPath $installDirectory) { throw 'SurroundTouch already exists in Program Files. Close it and run Uninstall.cmd before installing this version. Per-user settings will be retained.' }
    foreach ($path in @($stateDirectory, $shortcutPath)) {
        if (Test-Path -LiteralPath $path) { throw ('An existing installation artifact was found. Run Uninstall.cmd first: ' + $path) }
    }
    [void](Assert-LocalSafePath $packageDirectory)
    [void](Assert-LocalSafePath $shortcutPath)
    $hashFile = Join-Path $packageDirectory 'package-hashes.json'
    [void](Assert-LocalSafePath $hashFile)
    if ((Get-Item -LiteralPath $hashFile).Length -gt 32768) { throw 'The package checksum file is unexpectedly large.' }
    $manifest = Get-Content -LiteralPath $hashFile -Raw | ConvertFrom-Json
    if ($manifest.SchemaVersion -ne 1) { throw 'Unsupported package checksum version. Rebuild the package.' }
    $packageBytes = @{}
    foreach ($name in @('SurroundTouch.exe', 'SurroundTouch.Diagnostics.exe', 'Setup.ps1')) {
        $source = Join-Path $packageDirectory $name
        [void](Assert-LocalSafePath $source)
        $file = Get-Item -LiteralPath $source
        if ($file.PSIsContainer -or $file.Length -lt 1 -or $file.Length -gt 67108864) { throw ('Invalid package file: ' + $name) }
        $expectedHash = $manifest.Files.PSObject.Properties[$name]
        if ($null -eq $expectedHash -or $expectedHash.Value -notmatch '^[0-9A-Fa-f]{64}$') { throw ('Missing checksum for ' + $name) }
        $bytes = [IO.File]::ReadAllBytes($source)
        if ((Get-BytesHash $bytes) -ne $expectedHash.Value) { throw ('Package checksum failed for ' + $name + '. Rebuild or obtain an intact package.') }
        if ($name.EndsWith('.exe') -and ($bytes.Length -lt 2 -or $bytes[0] -ne 77 -or $bytes[1] -ne 90)) { throw ('Invalid executable: ' + $name) }
        $packageBytes[$name] = $bytes
    }

    $stage = Join-Path $programFilesPath ('.SurroundTouch-stage-' + [Guid]::NewGuid().ToString('N'))
    $stageCreated = $false
    $appMoved = $false
    $recordCreated = $false
    $shortcutCreated = $false
    $signerThumbprint = $null
    $certificateState = $null
    $signingCertificate = $null
    $publicCertificate = $null
    $phase = 'Create protected staging directory'
    try {
        Assert-AppDirectory $stage
        if (Test-Path -LiteralPath $stage) { throw 'Staging directory collision. Retry setup.' }
        [void](New-Item -Path $stage -ItemType Directory)
        $stageCreated = $true
        Set-ProtectedDirectoryAcl $stage
        foreach ($name in $packageBytes.Keys) { [IO.File]::WriteAllBytes((Join-Path $stage $name), $packageBytes[$name]) }

        $phase = 'Create local signing certificate'
        $subject = 'CN=SurroundTouch Local Install ' + [Guid]::NewGuid().ToString('N')
        $signingCertificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
            -CertStoreLocation 'Cert:\LocalMachine\My' -KeyExportPolicy NonExportable `
            -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
            -Provider 'Microsoft Software Key Storage Provider' -NotAfter ((Get-Date).AddYears(10))
        $signerThumbprint = $signingCertificate.Thumbprint
        $certificateState = [pscustomobject]@{
            SigningThumbprint = $signerThumbprint
            CertificateSubject = $signingCertificate.Subject
            CertificateSha256 = Get-BytesHash $signingCertificate.RawData
        }
        $phase = 'Create protected recovery directory'
        Assert-StateDirectory
        if (Test-Path -LiteralPath $stateDirectory) { throw 'The recovery directory appeared during setup. It was left untouched.' }
        [void](New-Item -Path $stateDirectory -ItemType Directory)
        $recordCreated = $true
        $phase = 'Protect recovery directory permissions'
        Set-ProtectedDirectoryAcl $stateDirectory
        $phase = 'Save and verify recovery JSON record'
        Write-InstallationState $certificateState $stage
        $phase = 'Sign application executables'
        foreach ($name in @('SurroundTouch.exe', 'SurroundTouch.Diagnostics.exe')) {
            $signature = Set-AuthenticodeSignature -LiteralPath (Join-Path $stage $name) -Certificate $signingCertificate -HashAlgorithm SHA256
            if ($null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $signerThumbprint) {
                throw ('Signing failed for ' + $name)
            }
        }
        $publicCertificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList (,$signingCertificate.RawData)
        [IO.File]::WriteAllBytes((Join-Path $stage 'signing-certificate.cer'), $publicCertificate.RawData)
        # Destroy the private signing key BEFORE trusting this certificate. The
        # installed trust cannot then be used to sign additional binaries.
        $phase = 'Delete temporary private signing key'
        Remove-Item -LiteralPath ('Cert:\LocalMachine\My\' + $signerThumbprint) -DeleteKey -Force
        if (Test-Path -LiteralPath ('Cert:\LocalMachine\My\' + $signerThumbprint)) { throw 'Could not remove the temporary signing key.' }
        $signingCertificate.Dispose()
        $signingCertificate = $null
        if ($publicCertificate.HasPrivateKey) { throw 'The public certificate unexpectedly contains a private key.' }
        $phase = 'Trust public signing certificate'
        foreach ($storeName in @('Root', 'TrustedPublisher')) {
            $store = New-Object Security.Cryptography.X509Certificates.X509Store($storeName, 'LocalMachine')
            try {
                $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
                $store.Add($publicCertificate)
            } finally { $store.Close() }
        }
        $phase = 'Verify installed signatures'
        foreach ($name in @('SurroundTouch.exe', 'SurroundTouch.Diagnostics.exe')) {
            $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $stage $name)
            if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or $signature.SignerCertificate.Thumbprint -ne $signerThumbprint) {
                throw ('Windows did not validate the installed signature for ' + $name + ': ' + $signature.StatusMessage)
            }
        }
        # Same-volume directory rename publishes only the fully prepared files.
        $phase = 'Publish installed application'
        Assert-AppDirectory $stage
        Assert-AppDirectory $installDirectory
        [IO.Directory]::Move($stage, $installDirectory)
        $stageCreated = $false
        $appMoved = $true

        $phase = 'Create Start menu shortcut'
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = Join-Path $installDirectory 'SurroundTouch.exe'
        $shortcut.WorkingDirectory = $installDirectory
        $shortcut.Description = 'Map two touch controllers into one NVIDIA Surround display'
        $shortcutCreated = $true
        $shortcut.Save()
        Show-Result ("Installed successfully.`r`n`r`nOpen SurroundTouch from the Start menu as your normal user. Use a mouse/keyboard for initial setup.`r`n`r`nLocation: " + $installDirectory + "`r`nSigning certificate: " + $signerThumbprint + "`r`nThe temporary private signing key was deleted. To remove the application and this exact certificate, run the package's Uninstall.cmd. No Windows Installed Apps registration was added.")
    } catch {
        $failure = 'Step: ' + $phase + "`r`n" + $_.Exception.Message + "`r`n" + $_.InvocationInfo.PositionMessage
        $script:failureDetails = 'INSTALL_FAILURE: ' + $failure + "`r`n" + $_.Exception.ToString() + "`r`n" + $_.ScriptStackTrace
        Write-Output $script:failureDetails
        $rollbackErrors = New-Object 'Collections.Generic.List[string]'
        foreach ($cleanup in @(
            { if ($shortcutCreated) { Remove-OwnedShortcut } },
            { if ($appMoved) { Remove-OwnedDirectory $installDirectory } },
            { if ($stageCreated) { Remove-OwnedDirectory $stage } },
            { if ($null -ne $certificateState) { Remove-OwnedTrust $certificateState } },
            { if ($signerThumbprint -and (Test-Path -LiteralPath ('Cert:\LocalMachine\My\' + $signerThumbprint))) { Remove-Item -LiteralPath ('Cert:\LocalMachine\My\' + $signerThumbprint) -DeleteKey -Force } }
        )) {
            try { & $cleanup } catch { $rollbackErrors.Add($_.Exception.Message) }
        }
        if ($rollbackErrors.Count -eq 0) {
            if ($recordCreated -and (Test-Path -LiteralPath $stateDirectory)) {
                try { Remove-OwnedStateDirectory } catch { $rollbackErrors.Add($_.Exception.Message) }
            }
        } elseif ($null -ne $certificateState) {
            # Retain/recover the exact public certificate identity so that a later
            # Uninstall.cmd can finish cleanup without a broad certificate search.
            try {
                Assert-StateDirectory
                if ((Test-Path -LiteralPath $stateDirectory) -and -not $recordCreated) { throw 'A recovery directory belonging to another setup exists; it was not overwritten.' }
                if (-not (Test-Path -LiteralPath $stateDirectory)) { [void](New-Item -Path $stateDirectory -ItemType Directory) }
                Set-ProtectedDirectoryAcl $stateDirectory
                Write-InstallationState $certificateState $stage
            } catch { $rollbackErrors.Add('Could not save the recovery record: ' + $_.Exception.Message) }
        }
        if ($rollbackErrors.Count -gt 0) {
            $failure += "`r`n`r`nCleanup needs attention. Keep this package and rerun Uninstall.cmd after resolving the errors.`r`n" + ($rollbackErrors -join "`r`n")
            if ($signerThumbprint) { $failure += "`r`nExact certificate thumbprint: " + $signerThumbprint }
            if ($stageCreated) { $failure += "`r`nStaging directory: " + $stage }
        }
        throw $failure
    } finally {
        if ($null -ne $signingCertificate) { $signingCertificate.Dispose() }
        if ($null -ne $publicCertificate) { $publicCertificate.Dispose() }
    }
}

try {
    if (-not [Environment]::Is64BitOperatingSystem -or -not [Environment]::Is64BitProcess) { throw 'Run the provided Install.cmd or Uninstall.cmd on 64-bit Windows 10/11.' }
    if ([Environment]::OSVersion.Version.Major -lt 10) { throw 'Windows 10 or Windows 11 is required.' }
    [void](Assert-LocalSafePath $programFilesPath)
    [void](Assert-LocalSafePath $PSCommandPath)
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal $identity
    $isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $Elevated) {
        Add-Type -AssemblyName System.Windows.Forms
        if ($Mode -eq 'Install') {
            $message = "Install SurroundTouch for all users?`r`n`r`nWindows requires a trusted signature and a protected location for system-wide touch routing. Setup installs into " + $installDirectory + ".`r`n`r`nIt creates a unique local code-signing certificate, signs only the two application executables, deletes its nonexportable private key, and adds the public certificate to this computer's Trusted Root Certification Authorities and Trusted Publishers. The certificate is valid for 10 years. Uninstall removes this exact trust.`r`n`r`nOnly install this package if you trust its source. Locally generated signing proves installation integrity; it does not identify a verified publisher. Administrator approval is required. No touch routing starts during setup."
        } else {
            $message = 'Uninstall SurroundTouch, its Start menu shortcut, and its exact local signing certificate? Close the application first. Per-user settings and diagnostics will be retained.'
        }
        $choice = [Windows.Forms.MessageBox]::Show($message, 'SurroundTouch setup', [Windows.Forms.MessageBoxButtons]::OKCancel, [Windows.Forms.MessageBoxIcon]::Information)
        if ($choice -ne [Windows.Forms.DialogResult]::OK) { exit 0 }
    }
    if (-not $isAdmin) {
        # Hidden console; the interactive disclosure, UAC and result dialogs remain visible.
        $argumentText = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -Mode ' + $Mode + ' -Elevated'
        if ($NoResultDialog) { $argumentText += ' -NoResultDialog' }
        $process = Start-Process -FilePath $windowsPowerShell -ArgumentList $argumentText -Verb RunAs -WindowStyle Hidden -Wait -PassThru
        exit $process.ExitCode
    }
    if ($Mode -eq 'Install') { Invoke-Install } else { Invoke-Uninstall }
    exit 0
} catch {
    $message = "Setup did not complete.`r`n`r`n" + $_.Exception.Message
    $details = $script:failureDetails + "`r`n" + $_.Exception.ToString() + "`r`n" + $_.InvocationInfo.PositionMessage + "`r`n" + $_.ScriptStackTrace
    try { $message += "`r`n`r`nDiagnostic log: " + (Save-FailureLog $details) }
    catch { $message += "`r`n`r`nA diagnostic log could not be saved: " + $_.Exception.Message }
    Show-Result $message $true
    exit 1
}
