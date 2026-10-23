# Pure tests run by default. Desktop injection requires the explicit -RunIntegration switch.
[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('SurroundTouch-tests-' + [Guid]::NewGuid().ToString('N'))),
    [switch]$RunIntegration
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$packageDirectory = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not [Environment]::Is64BitOperatingSystem -or -not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw '64-bit Windows with the .NET Framework 4.x compiler is required.'
}
$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $resolvedOutput) {
    if (-not (Test-Path -LiteralPath $resolvedOutput -PathType Container)) { throw 'OutputDirectory must be a directory.' }
} else {
    [void](New-Item -ItemType Directory -Path $resolvedOutput)
}

$core = Join-Path $packageDirectory 'src\TouchCore.cs'
$commonArguments = @('/nologo', '/target:exe', '/platform:x64', '/optimize+', '/warn:4', '/codepage:65001', '/langversion:5',
    '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Xml.dll')
$coreExecutable = Join-Path $resolvedOutput 'CoreTests.exe'
$arguments = $commonArguments + @(('/out:' + $coreExecutable), '/main:CoreTests', $core, (Join-Path $PSScriptRoot 'CoreTests.cs'))
& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw ('Core test compilation failed: ' + $LASTEXITCODE) }

$integrationExecutable = Join-Path $resolvedOutput 'WindowsIntegration.exe'
$arguments = $commonArguments + @('/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
    ('/win32manifest:' + (Join-Path $packageDirectory 'diagnostics.manifest')), '/main:SurroundTouch.WindowsIntegration',
    ('/out:' + $integrationExecutable), $core, (Join-Path $packageDirectory 'src\Native.cs'), (Join-Path $PSScriptRoot 'WindowsIntegration.cs'))
& $compiler $arguments
if ($LASTEXITCODE -ne 0) { throw ('Windows integration test compilation failed: ' + $LASTEXITCODE) }

$coreReport = Join-Path $resolvedOutput 'CoreTests-report.txt'
$coreOutput = @(& $coreExecutable 2>&1)
$coreExitCode = $LASTEXITCODE
$coreOutput | Set-Content -LiteralPath $coreReport -Encoding UTF8
$coreOutput | ForEach-Object { Write-Host $_ }
if ($coreExitCode -ne 0) { throw ('Core tests failed. See ' + $coreReport) }

if ($RunIntegration) {
    Write-Host 'Opening the interactive touch-injection test. Stop other touch remappers first and keep its window uncovered.'
    $integrationReport = Join-Path $resolvedOutput 'WindowsIntegration-report.txt'
    & $integrationExecutable $integrationReport
    $integrationExitCode = $LASTEXITCODE
    if (Test-Path -LiteralPath $integrationReport) { Get-Content -LiteralPath $integrationReport | ForEach-Object { Write-Host $_ } }
    if ($integrationExitCode -ne 0) { throw ('Windows integration test failed. See ' + $integrationReport) }
} else {
    Write-Host 'Windows integration test compiled but not run. No desktop input was injected.'
}
Write-Host ('Test artifacts: ' + $resolvedOutput)
