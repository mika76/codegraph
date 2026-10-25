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
# a running `codegraph mcp` (started by an AI assistant) locks the exe; Windows can still rename it, so move it
# aside - the running server keeps working and the next start uses the new build
$exe = Join-Path $out "codegraph.exe"
Get-ChildItem $out -Filter "codegraph.old*.exe" -ErrorAction SilentlyContinue | ForEach-Object {
    try { Remove-Item $_.FullName -ErrorAction Stop } catch { }
}
if (Test-Path $exe) {
    try { [IO.File]::OpenWrite($exe).Close() }
    catch { Rename-Item $exe ("codegraph.old-" + (Get-Date -Format "yyyyMMddHHmmss") + ".exe") }
}
dotnet publish (Join-Path $PSScriptRoot "src\CodeGraph") -c Release -r $Runtime -o $out
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Built $out\codegraph$(if ($Runtime -like 'win-*') { '.exe' })"
