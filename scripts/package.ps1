# Builds the release folder and its zip from build-release\ (build.cmd) and launcher\dist
# (launcher\build.cmd): the launcher, the game exe and the files it reads beside itself (bundled
# OpenBIOS, mod catalog, game.toml, window icon), README.txt and the licences. Nothing from the disc
# or from playing goes in: the script stops if it would.
#
#   powershell -ExecutionPolicy Bypass -File scripts\package.ps1 [-Destination <folder>] [-Zip <file.zip>] [-NoZip]
param(
    [string]$Destination,
    [string]$Zip,
    [switch]$NoZip,
    [string]$BuildDir = 'build-release'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$root = Split-Path $PSScriptRoot -Parent
$version = (Get-Content -LiteralPath "$root\VERSION" -Raw).Trim()
$name = "WCW-Mayhem-Recomp-v$version-win-x64"
if (-not $Destination) { $Destination = "$root\out\release\$name" }
$Destination = [IO.Path]::GetFullPath($Destination)
if (-not $Zip -and -not $NoZip) { $Zip = "$Destination.zip" }

$gameBuild = [IO.Path]::GetFullPath((Join-Path $root $BuildDir))
$launcherBuild = "$root\launcher\dist"
$framework = "$root\psxrecomp"
$gameExe = 'WCWMayhem_Recompiled.exe'
$required = @(
    "$gameBuild\$gameExe", "$gameBuild\bios\openbios.bin", "$gameBuild\bios\OpenBIOS.LICENSE",
    "$gameBuild\assets\psxrecomp.png", "$gameBuild\mods\bundled", "$root\game.toml", "$root\game_options.toml",
    "$launcherBuild\WCWMayhemLauncher.exe", "$launcherBuild\art\background.png",
    "$framework\LICENSE", "$framework\THIRD_PARTY_ATTRIBUTION.md", "$framework\runtime\licenses",
    "$framework\lib\retcomm-rbengine\LICENSE"
)
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Missing $path. Run build.cmd and launcher\build.cmd first." }
}

# A folder used for playing holds the player's settings, saves and state: never package those.
$ours = @($gameExe, 'psx_game_version.txt', 'game.toml', 'game_options.toml', 'bios', 'assets', 'mods',
          'WCWMayhemLauncher.exe', 'art', 'README.txt', 'THIRD-PARTY-NOTICES.txt', 'licenses')
if (Test-Path -LiteralPath $Destination) {
    $other = Get-ChildItem -LiteralPath $Destination -Force | Where-Object { $ours -notcontains $_.Name }
    if ($other) {
        throw "$Destination holds files that are not part of the release ($(($other.Name) -join ', ')). Use an empty folder."
    }
    Remove-Item -LiteralPath $Destination -Recurse -Force
}
New-Item -ItemType Directory -Force $Destination, "$Destination\bios", "$Destination\assets", "$Destination\mods",
    "$Destination\art", "$Destination\licenses" | Out-Null

# The game and what it reads beside itself. game.toml comes from the repository (the build tree's
# copy can be older), the rest from the build.
Copy-Item -LiteralPath "$gameBuild\$gameExe" -Destination $Destination
if (Test-Path -LiteralPath "$gameBuild\psx_game_version.txt") {
    Copy-Item -LiteralPath "$gameBuild\psx_game_version.txt" -Destination $Destination
}
Copy-Item -LiteralPath "$root\game.toml", "$root\game_options.toml" -Destination $Destination
Copy-Item -LiteralPath "$gameBuild\bios\openbios.bin", "$gameBuild\bios\OpenBIOS.LICENSE" -Destination "$Destination\bios"
Copy-Item -LiteralPath "$gameBuild\assets\psxrecomp.png" -Destination "$Destination\assets"
Copy-Item -LiteralPath "$gameBuild\mods\bundled" -Destination "$Destination\mods\bundled" -Recurse
if (Test-Path -LiteralPath "$gameBuild\mods\README.md") {
    Copy-Item -LiteralPath "$gameBuild\mods\README.md" -Destination "$Destination\mods"
}
# The launcher: one self-contained exe plus its home-screen art.
Copy-Item -LiteralPath "$launcherBuild\WCWMayhemLauncher.exe" -Destination $Destination
Copy-Item -Path "$launcherBuild\art\*" -Include *.png, *.jpg -Destination "$Destination\art"
Copy-Item -LiteralPath "$root\release\README.txt", "$root\release\THIRD-PARTY-NOTICES.txt" -Destination $Destination

# Licences: psxrecomp (PolyForm Noncommercial 1.0.0) and what its runtime links, SDL3 and the LLVM
# runtime from the toolchain, and the launcher's .NET runtime and native packages.
$lic = "$Destination\licenses"
Copy-Item -LiteralPath "$framework\LICENSE" -Destination "$lic\psxrecomp-LICENSE.txt"
Copy-Item -LiteralPath "$framework\THIRD_PARTY_ATTRIBUTION.md" -Destination "$lic\psxrecomp-THIRD_PARTY_ATTRIBUTION.md"
Copy-Item -Path "$framework\runtime\licenses\*" -Destination $lic
Copy-Item -LiteralPath "$framework\lib\retcomm-rbengine\LICENSE" -Destination "$lic\retcomm-rbengine-LICENSE.txt"

$bin = $null
foreach ($python in @(@('py', '-3'), @('python'), @('python3'))) {
    if (-not (Get-Command $python[0] -ErrorAction SilentlyContinue)) { continue }
    try {
        $ErrorActionPreference = 'Continue'
        $progress = & $python[0] @($python | Select-Object -Skip 1) "$framework\psxrecomp_cli.py" 'ensure-toolchain' '--project-root' $root '--no-download' '--json-progress' 2>$null
        $bin = ($progress | Where-Object { $_ -like '{*' } | ForEach-Object { $_ | ConvertFrom-Json } |
            Where-Object { $_.event -eq 'result' } | Select-Object -Last 1).toolchain_bin
    } catch {
        $bin = $null
    } finally {
        $ErrorActionPreference = 'Stop'
    }
    if ($bin) { break }
}
if (-not $bin) { throw 'The psxrecomp toolchain (needed for the SDL3 / LLVM licences) was not found.' }
$pack = Split-Path $bin -Parent
foreach ($p in @(@("$pack\deps\share\licenses\SDL3\LICENSE.txt", 'SDL3-LICENSE.txt'),
                 @("$pack\LICENSE.TXT", 'llvm-LICENSE.txt'))) {
    if (-not (Test-Path -LiteralPath $p[0])) { throw "Licence file not found: $($p[0])" }
    Copy-Item -LiteralPath $p[0] -Destination "$lic\$($p[1])"
}

$assets = Get-Content "$root\launcher\src\WcwLauncher\obj\project.assets.json" -Raw | ConvertFrom-Json
$packages = "$env:USERPROFILE\.nuget\packages"
if ($env:NUGET_PACKAGES) { $packages = $env:NUGET_PACKAGES }
function PackageVersion([string]$id) {
    ($assets.libraries.PSObject.Properties.Name | Where-Object { $_ -like "$id/*" } | Select-Object -First 1) -replace '^.*/', ''
}
# Stored as a version range, "[10.0.12, 10.0.12]".
$runtime = (($assets.project.frameworks.'net10.0'.downloadDependencies |
    Where-Object { $_.name -eq 'Microsoft.NETCore.App.Runtime.win-x64' } | Select-Object -First 1).version -replace '[\[\]\s]', '' -split ',')[0]
$fromPackages = @(
    @("microsoft.netcore.app.runtime.win-x64\$runtime\LICENSE.TXT", 'dotnet-runtime-LICENSE.txt'),
    @("microsoft.netcore.app.runtime.win-x64\$runtime\THIRD-PARTY-NOTICES.TXT", 'dotnet-runtime-THIRD-PARTY-NOTICES.txt'),
    @("skiasharp\$(PackageVersion 'SkiaSharp')\LICENSE.txt", 'SkiaSharp-LICENSE.txt'),
    @("skiasharp\$(PackageVersion 'SkiaSharp')\THIRD-PARTY-NOTICES.txt", 'SkiaSharp-THIRD-PARTY-NOTICES.txt'),
    @("harfbuzzsharp\$(PackageVersion 'HarfBuzzSharp')\LICENSE.txt", 'HarfBuzzSharp-LICENSE.txt'),
    @("avalonia.angle.windows.natives\$(PackageVersion 'Avalonia.Angle.Windows.Natives')\LICENSE", 'ANGLE-LICENSE.txt')
)
foreach ($p in $fromPackages) {
    $src = Join-Path $packages $p[0]
    if (-not (Test-Path -LiteralPath $src)) { throw "Licence file not found: $src" }
    Copy-Item -LiteralPath $src -Destination "$lic\$($p[1])"
}

# Release check: no disc data, generated code or player state.
$openBios = Join-Path $Destination 'bios\openbios.bin'   # the bundled, MIT-licensed BIOS ships
$bad = Get-ChildItem -LiteralPath $Destination -Recurse -Force | Where-Object { $_.FullName -ne $openBios } | Where-Object {
    $_.Extension -in '.cue', '.bin', '.iso', '.chd', '.img', '.mcd', '.mcr', '.pst', '.log', '.json', '.ini', '.c', '.h' -or
    $_.Name -in 'settings.toml', 'state.toml', 'SLES_021.93', 'disc.cfg', 'bios.cfg' -or
    $_.Name -like 'psx_freeze*' -or
    ($_.PSIsContainer -and $_.Name -in 'saves', 'cache', 'logs', 'generated', 'disc', 'installed', 'overlay_captures.json.d')
}
if ($bad) { throw "Refusing to package: $(($bad.FullName) -join ', ')" }

Write-Host "Release folder: $Destination"
Get-ChildItem -LiteralPath $Destination | ForEach-Object {
    Write-Host ("  {0,-28} {1,12:N0}" -f $_.Name, $(if ($_.PSIsContainer) { (Get-ChildItem $_.FullName -Recurse -File).Count } else { $_.Length }))
}
if ($Zip) {
    $Zip = [IO.Path]::GetFullPath($Zip)
    New-Item -ItemType Directory -Force (Split-Path $Zip -Parent) | Out-Null
    if (Test-Path -LiteralPath $Zip) { Remove-Item -LiteralPath $Zip -Force }
    # Everything under one folder named after the release, whatever the staging folder is called.
    $archive = [IO.Compression.ZipFile]::Open($Zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $Destination -Recurse -File) {
            $entry = "$name/" + $file.FullName.Substring($Destination.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry,
                [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally {
        $archive.Dispose()
    }
    $hash = (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash.ToLower()
    Set-Content -LiteralPath "$Zip.sha256" -Value "$hash  $([IO.Path]::GetFileName($Zip))" -Encoding ascii
    Write-Host ("Zip: {0} ({1:N1} MB), SHA-256 {2}" -f $Zip, ((Get-Item -LiteralPath $Zip).Length / 1MB), $hash)
}
