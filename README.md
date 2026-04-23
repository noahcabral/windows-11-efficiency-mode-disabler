# DCA Efficiency Mode Disabler

A compact Windows Service that keeps Task Manager Efficiency Mode off for accessible processes.

It targets both parts of Windows Efficiency Mode:

- clears EcoQoS / execution-speed throttling
- restores Low/Idle process priority back to Normal
- reapplies HighQoS to accessible threads so thread-only Efficiency Mode gets reset too

There is no tray app and no persistent UI. The service runs in the background as `LocalSystem`.

## Install

Download the installer zip from a release, extract it, then right-click `Install.cmd` and choose **Run as administrator**.

The service installs to:

```text
C:\Program Files\DcaEfficiencyModeDisabler
```

Service name:

```text
DcaEfficiencyModeDisabler
```

## Status

Run `Status.cmd` from the extracted installer folder, or use:

```powershell
sc.exe queryex DcaEfficiencyModeDisabler
Get-Content "C:\Program Files\DcaEfficiencyModeDisabler\DcaEfficiencyModeService.log" -Tail 20
```

## Uninstall

Right-click `Uninstall.cmd` and choose **Run as administrator**.

## Build

This project builds with the built-in .NET Framework C# compiler on Windows:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build.ps1
```

Create a redistributable installer folder and zip:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Package.ps1
```

Artifacts are written under `build\` and `dist\`.

## Command-Line Checks

Run one scan without changing anything:

```powershell
.\build\DcaEfficiencyModeService.exe --once --dry-run
```

Run one scan and apply fixes:

```powershell
.\build\DcaEfficiencyModeService.exe --once
```

## Notes

- Protected Windows processes can still deny access.
- The service does not raise already-normal processes to high priority.
- Above-normal or custom priority classes are left alone.
- Low/Idle priority is restored to Normal because Task Manager Efficiency Mode can leave the text label visible when priority remains lowered after EcoQoS is cleared.

## License

MIT
