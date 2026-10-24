# Copies the Claude Code skills and settings into the .claude folder of this repo
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root ".claude"
New-Item -ItemType Directory -Force -Path (Join-Path $dest "skills") | Out-Null
Copy-Item -Recurse -Force (Join-Path $PSScriptRoot "skills\*") (Join-Path $dest "skills")
if (-not (Test-Path (Join-Path $dest "settings.json"))) { Copy-Item (Join-Path $PSScriptRoot "settings.json") $dest }
Write-Host "Installed skills: /graph-build /graph-ask /graph-release into $dest"
