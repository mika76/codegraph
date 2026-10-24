# Builds the native codegraph.exe into .\publish for this machine (x64 or ARM64).
#   .\publish.ps1            -> publish\codegraph.exe
#   .\publish.ps1 linux-x64  -> publish\linux-x64\codegraph
# Native AOT needs the "Desktop development with C++" workload of Visual Studio (or the Build Tools).
param([string]$Runtime)

$ErrorActionPreference = "Stop"
# the native linker step calls vswhere.exe, which is not on PATH by default
$installer = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if (Test-Path $installer) { $env:PATH = "$installer;$env:PATH" }

$out = Join-Path $PSScriptRoot "publish"
if (-not $Runtime) {
    $Runtime = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "win-arm64" } else { "win-x64" }
} else {
    $out = Join-Path $out $Runtime
}
dotnet publish (Join-Path $PSScriptRoot "src\CodeGraph") -c Release -r $Runtime -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built $out\codegraph$(if ($Runtime -like 'win-*') { '.exe' })"
