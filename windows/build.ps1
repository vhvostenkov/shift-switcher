param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $RepoRoot
try {
    dotnet run --project windows/ShiftSwitcher.Checks -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core checks failed' }
    dotnet run --project windows/ShiftSwitcher.LicensingChecks -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Licensing checks failed' }
    dotnet run --project windows/ShiftSwitcher.LicensingNativeChecks -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Native licensing checks failed' }
    dotnet publish windows/ShiftSwitcher.Windows -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "release/windows/$Runtime"
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed' }
    Write-Host "Built release/windows/$Runtime/ShiftSwitcher.exe. Native Windows acceptance remains required."
} finally { Pop-Location }
