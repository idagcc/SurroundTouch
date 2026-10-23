# Requires only the in-box .NET Framework compiler on 64-bit Windows 10/11.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$packageDirectory = $PSScriptRoot
$compiler = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not [Environment]::Is64BitOperatingSystem -or -not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'A 64-bit Windows installation with .NET Framework 4.x is required.'
}
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $packageDirectory 'src') -Filter '*.cs' -File | Sort-Object Name | ForEach-Object FullName)
if ($sourceFiles.Count -eq 0) { throw 'No C# source files were found in src.' }
$commonArguments = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/warn:4', '/codepage:65001', '/langversion:5',
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Windows.Forms.dll',
    '/reference:System.Drawing.dll', '/reference:System.Runtime.Serialization.dll')
foreach ($variant in @(
    @{ Name = 'SurroundTouch.exe'; Manifest = 'app.manifest' },
    @{ Name = 'SurroundTouch.Diagnostics.exe'; Manifest = 'diagnostics.manifest' }
)) {
    $executablePath = Join-Path $packageDirectory $variant.Name
    $arguments = $commonArguments + @(('/win32manifest:' + (Join-Path $packageDirectory $variant.Manifest)), ('/out:' + $executablePath)) + $sourceFiles
    & $compiler $arguments
    if ($LASTEXITCODE -ne 0) { throw ('Compilation failed for ' + $variant.Name + '. Exit code: ' + $LASTEXITCODE) }
    Write-Host ('Built ' + $variant.Name)
}

# These checksums detect incomplete/corrupted packages. They are not a publisher signature.
$hashes = [ordered]@{ SchemaVersion = 1; Files = [ordered]@{} }
foreach ($name in @('SurroundTouch.exe', 'SurroundTouch.Diagnostics.exe', 'Setup.ps1')) {
    $hashes.Files[$name] = (Get-FileHash -LiteralPath (Join-Path $packageDirectory $name) -Algorithm SHA256).Hash
}
$hashes | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $packageDirectory 'package-hashes.json') -Encoding UTF8
Write-Host 'Build complete. Run Install.cmd to install the UIAccess application, or the diagnostics executable for read-only inspection.'
