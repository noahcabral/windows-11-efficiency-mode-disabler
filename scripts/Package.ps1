param(
    [string]$OutputDir = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..")) "dist"),
    [string]$PackageName = "DcaEfficiencyModeDisabler-Installer"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$buildDir = Join-Path $root "build"
$stageRoot = Join-Path $OutputDir $PackageName
$payloadDir = Join-Path $stageRoot "payload"
$zipPath = Join-Path $OutputDir "$PackageName.zip"

if (Test-Path $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$builtExe = & (Join-Path $PSScriptRoot "Build.ps1") -OutputDir $buildDir
$builtExe = [string]($builtExe | Select-Object -Last 1)
Copy-Item -LiteralPath $builtExe -Destination (Join-Path $payloadDir "DcaEfficiencyModeService.exe") -Force
Copy-Item -LiteralPath (Join-Path $root "src\DcaEfficiencyModeService.cs") -Destination (Join-Path $stageRoot "DcaEfficiencyModeService.cs") -Force

@'
param(
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
$ServiceName = "DcaEfficiencyModeDisabler"
$DisplayName = "DCA Efficiency Mode Disabler"
$InstallDir = Join-Path $env:ProgramFiles "DcaEfficiencyModeDisabler"
$PayloadExe = Join-Path $PSScriptRoot "payload\DcaEfficiencyModeService.exe"
$TargetExe = Join-Path $InstallDir "DcaEfficiencyModeService.exe"

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdmin)) {
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -NoPause"
    Start-Process -FilePath "powershell.exe" -ArgumentList $argList -Verb RunAs
    exit 0
}

if (-not (Test-Path $PayloadExe)) {
    throw "Missing payload executable: $PayloadExe"
}

$query = & sc.exe query $ServiceName 2>$null
if ($LASTEXITCODE -eq 0) {
    & sc.exe stop $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -LiteralPath $PayloadExe -Destination $TargetExe -Force

& sc.exe create $ServiceName `
    binPath= "`"$TargetExe`"" `
    start= auto `
    obj= LocalSystem `
    DisplayName= $DisplayName | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "sc.exe create failed with exit code $LASTEXITCODE."
}

& sc.exe description $ServiceName "Clears Windows EcoQoS/Efficiency Mode and restores Low/Idle process priority in the background." | Out-Host
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/none/60000 | Out-Host
& sc.exe start $ServiceName | Out-Host

Write-Output ""
Write-Output "Installed and started $DisplayName."
Write-Output "Installed executable: $TargetExe"

if (-not $NoPause) {
    Write-Output ""
    Read-Host "Press Enter to close"
}
'@ | Set-Content -LiteralPath (Join-Path $stageRoot "Install.ps1") -Encoding UTF8

@'
param(
    [switch]$NoPause,
    [switch]$KeepFiles
)

$ErrorActionPreference = "Stop"
$ServiceName = "DcaEfficiencyModeDisabler"
$InstallDir = Join-Path $env:ProgramFiles "DcaEfficiencyModeDisabler"

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-IsAdmin)) {
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -NoPause"
    if ($KeepFiles) {
        $argList += " -KeepFiles"
    }
    Start-Process -FilePath "powershell.exe" -ArgumentList $argList -Verb RunAs
    exit 0
}

& sc.exe query $ServiceName 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    & sc.exe stop $ServiceName | Out-Null
    Start-Sleep -Seconds 2
    & sc.exe delete $ServiceName | Out-Host
} else {
    Write-Output "Service $ServiceName is not installed."
}

if (-not $KeepFiles -and (Test-Path $InstallDir)) {
    Remove-Item -LiteralPath $InstallDir -Recurse -Force
    Write-Output "Removed $InstallDir"
}

if (-not $NoPause) {
    Write-Output ""
    Read-Host "Press Enter to close"
}
'@ | Set-Content -LiteralPath (Join-Path $stageRoot "Uninstall.ps1") -Encoding UTF8

@'
param(
    [switch]$NoPause
)

$ServiceName = "DcaEfficiencyModeDisabler"
$InstallDir = Join-Path $env:ProgramFiles "DcaEfficiencyModeDisabler"
$LogPath = Join-Path $InstallDir "DcaEfficiencyModeService.log"

& sc.exe queryex $ServiceName

Write-Output ""
if (Test-Path $LogPath) {
    Write-Output "Recent log lines:"
    Get-Content $LogPath -Tail 20
} else {
    Write-Output "No service log found at $LogPath"
}

if (-not $NoPause) {
    Write-Output ""
    Read-Host "Press Enter to close"
}
'@ | Set-Content -LiteralPath (Join-Path $stageRoot "Status.ps1") -Encoding UTF8

@'
@echo off
if not exist "%~dp0Install.ps1" (
    echo This launcher must be run from an extracted installer folder.
    echo Windows appears to have run it from inside the zip preview.
    echo Right-click the zip, choose Extract All..., then run Install.cmd from the extracted folder.
    echo.
    pause
    exit /b 1
)
if not exist "%~dp0payload\DcaEfficiencyModeService.exe" (
    echo Missing payload\DcaEfficiencyModeService.exe.
    echo Extract the full zip before running Install.cmd.
    echo.
    pause
    exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
'@ | Set-Content -LiteralPath (Join-Path $stageRoot "Install.cmd") -Encoding ASCII

@'
@echo off
if not exist "%~dp0Uninstall.ps1" (
    echo This launcher must be run from an extracted installer folder.
    echo Windows appears to have run it from inside the zip preview.
    echo Right-click the zip, choose Extract All..., then run Uninstall.cmd from the extracted folder.
    echo.
    pause
    exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall.ps1"
'@ | Set-Content -LiteralPath (Join-Path $stageRoot "Uninstall.cmd") -Encoding ASCII

@'
@echo off
if not exist "%~dp0Status.ps1" (
    echo This launcher must be run from an extracted installer folder.
    echo Windows appears to have run it from inside the zip preview.
    echo Right-click the zip, choose Extract All..., then run Status.cmd from the extracted folder.
    echo.
    pause
    exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Status.ps1" -NoPause
set "STATUS_EXIT=%ERRORLEVEL%"
echo.
if not "%STATUS_EXIT%"=="0" echo Status script exited with code %STATUS_EXIT%.
pause
exit /b %STATUS_EXIT%
'@ | Set-Content -LiteralPath (Join-Path $stageRoot "Status.cmd") -Encoding ASCII

@'
DCA Efficiency Mode Disabler
============================

This package installs a compact Windows Service that clears Windows Efficiency
Mode / EcoQoS from accessible processes, restores Low/Idle process priority to
Normal, and reapplies HighQoS to accessible threads in the background. It has no
tray app and no persistent UI.

Install:
1. Extract this zip.
2. Right-click Install.cmd and choose "Run as administrator".

Status:
- Run Status.cmd.
- Service name: DcaEfficiencyModeDisabler
- Install folder: C:\Program Files\DcaEfficiencyModeDisabler

Uninstall:
- Right-click Uninstall.cmd and choose "Run as administrator".

Notes:
- The service targets EcoQoS / execution-speed throttling and restores Low/Idle
  process priority classes to Normal.
- Protected Windows processes can still deny access.
- Source for the service is included as DcaEfficiencyModeService.cs.
'@ | Set-Content -LiteralPath (Join-Path $stageRoot "README.txt") -Encoding UTF8

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
$archiveItems = Join-Path $stageRoot "*"
Compress-Archive -Path $archiveItems -DestinationPath $zipPath -Force

if (-not (Test-Path $zipPath)) {
    throw "Package zip was not created at $zipPath."
}

Write-Output "Package folder: $stageRoot"
Write-Output "Package zip: $zipPath"
