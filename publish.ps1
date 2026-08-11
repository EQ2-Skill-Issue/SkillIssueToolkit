# publish.ps1 - Builds both projects and assembles a single distributable folder. Run this
# from anywhere - paths are resolved relative to this script's own location ($PSScriptRoot),
# not the current directory.
#
# Usage:
#   .\publish.ps1
# If PowerShell blocks running it (execution policy), run instead:
#   powershell -ExecutionPolicy Bypass -File publish.ps1

$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$actPluginDir = Join-Path $root "ActPlugin"
$overlayDir = Join-Path $root "Overlay"
$distRoot = Join-Path $root "dist"
$distOut = Join-Path $distRoot "SkillIssueToolkit"

Write-Host "== Cleaning previous dist output ==" -ForegroundColor Cyan
if (Test-Path $distOut) {
    Remove-Item $distOut -Recurse -Force
}
New-Item -ItemType Directory -Path $distOut | Out-Null
New-Item -ItemType Directory -Path (Join-Path $distOut "Overlay") | Out-Null

Write-Host "== Building SkillIssueToolkit.ActPlugin (net48) ==" -ForegroundColor Cyan
dotnet build $actPluginDir -c Release
if ($LASTEXITCODE -ne 0) {
    # If this fails with something about a missing net48 targeting pack, dotnet build's CLI
    # support for classic .NET Framework projects can be finicky depending on what's
    # installed - try building via Visual Studio (or msbuild.exe directly) instead if so.
    throw "ActPlugin build failed - see output above."
}

$actPluginOut = Join-Path $actPluginDir "bin\Release\net48"

Write-Host "== Publishing SkillIssueToolkit.Overlay (self-contained, win-x64) ==" -ForegroundColor Cyan
dotnet publish $overlayDir -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) {
    throw "Overlay publish failed - see output above."
}

$overlayPublishOut = Join-Path $overlayDir "bin\Release\net10.0-windows\win-x64\publish"

Write-Host "== Assembling dist folder ==" -ForegroundColor Cyan

# Plugin DLL + its dependency
Copy-Item (Join-Path $actPluginOut "SkillIssueToolkit.ActPlugin.dll") $distOut
Copy-Item (Join-Path $actPluginOut "Newtonsoft.Json.dll") $distOut

# Sample notification rules - copied straight from source rather than the build output, since
# they're static files the plugin reads at runtime, not something compiled. (They're also
# copied to the build output directly via the .csproj, for local dev convenience - this
# script doesn't depend on that, in case the two ever drift.) The default file is only a
# seed - the plugin re-fetches and overwrites it from GitHub on startup unless the user has
# turned that off.
Copy-Item (Join-Path $actPluginDir "eq2overlay-notifications.default.json") $distOut
Copy-Item (Join-Path $actPluginDir "eq2overlay-notifications.custom.json") $distOut

# Class-specific ability -> class name lookup, used as a local/offline fallback for class
# resolution alongside Census (see ClassAbilityLookup.cs). Static data, not compiled - just
# needs to travel alongside the plugin DLL like the notification rule files above.
Copy-Item (Join-Path $actPluginDir "eq2overlay-class-abilities.json") $distOut

# Deliberately NOT copying eq2overlay-settings.json here. It's tempting (it'd carry a
# pre-configured Census Service ID into what you distribute, saving guildmates a setup
# step), but Daybreak's own API policy explicitly says "please don't share your service ID
# with others" - shipping your personally-registered ID to anyone else does exactly that,
# regardless of which file it travels in. Everyone starts on the shared "example" ID, which
# is throttled per client IP (not a shared pool across everyone using it - confirmed
# directly from census.daybreakgames.com), so this is fine for normal use. Anyone who wants
# more headroom can register and configure their own ID individually, for their own
# machine's use, via the plugin's settings tab.

# Overlay pages
Copy-Item (Join-Path $actPluginDir "Overlays") (Join-Path $distOut "Overlays") -Recurse

# Overlay host - the full self-contained publish output, not just the exe
Copy-Item (Join-Path $overlayPublishOut "*") (Join-Path $distOut "Overlay") -Recurse

Write-Host "== Zipping ==" -ForegroundColor Cyan
$zipPath = Join-Path $distRoot "SkillIssueToolkit.zip"
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}
Compress-Archive -Path $distOut -DestinationPath $zipPath

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Distributable folder: $distOut"
Write-Host "  Zipped package:       $zipPath"