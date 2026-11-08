# Builds the Windows receiver and runs the platform-neutral tests.
# Run from any directory:  powershell -File Windows\build.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
dotnet test (Join-Path $root "AimMac.Windows.sln") -c Release
dotnet publish (Join-Path $root "src\AimMac.Receiver\AimMac.Receiver.csproj") -c Release -r win-x64 --self-contained true -o (Join-Path $root "publish")
Write-Host "Published to $(Join-Path $root 'publish')\AimMacReceiver.exe"
