param(
    [string]$OutputDir = (Join-Path $PSScriptRoot "..\build")
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$source = Join-Path $root "src\DcaEfficiencyModeService.cs"
$resolvedOutputDir = [System.IO.Path]::GetFullPath($OutputDir)
$exe = Join-Path $resolvedOutputDir "DcaEfficiencyModeService.exe"

$cscCandidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) {
    throw "Could not find the .NET Framework C# compiler."
}

New-Item -ItemType Directory -Force -Path $resolvedOutputDir | Out-Null

& $csc `
    /nologo `
    /optimize+ `
    /platform:anycpu `
    /target:exe `
    /reference:System.ServiceProcess.dll `
    /out:$exe `
    $source

if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

Write-Output $exe
