# Builds build-release\WCWMayhem_Recompiled.exe from your own disc (see README.md).
#
#   build.cmd "<path to WCW Mayhem (Europe).cue>"     first build: generates the game C from the disc
#   build.cmd                                          later builds: recompiles what changed
#   build.cmd "<cue>" -Regenerate                      regenerate the game C (after changing game.toml's
#                                                      [widescreen] sites, seeds or the patches)
#
# Uses the framework's portable toolchain (cmake-clang-v1: CMake, Ninja, clang/llvm-mingw, Python),
# which psxrecomp downloads once into %USERPROFILE%\.local\share\retcomm\toolchains and adds to your
# user PATH. Set PSXRECOMP_TOOLCHAIN_DIR to a pack you already have to skip the download.
param(
    [Parameter(Position = 0)][string]$Disc,
    [switch]$Regenerate,
    [string]$BuildDir = 'build-release'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$framework = Join-Path $root 'psxrecomp'
$cli = Join-Path $framework 'psxrecomp_cli.py'
if (-not (Test-Path -LiteralPath $cli)) {
    throw 'psxrecomp\ is missing: run  powershell -ExecutionPolicy Bypass -File scripts\setup-psxrecomp.ps1'
}

function Find-Python {
    foreach ($candidate in @(@('py', '-3'), @('python'), @('python3'))) {
        if (-not (Get-Command $candidate[0] -ErrorAction SilentlyContinue)) { continue }
        # The Microsoft Store alias for python, or a py launcher pointing at a removed install,
        # fails here: try the next one.
        $ok = $false
        try {
            $ErrorActionPreference = 'Continue'
            & $candidate[0] @($candidate | Select-Object -Skip 1) -c 'import sys; sys.exit(0 if sys.version_info >= (3, 9) else 1)' 2>&1 | Out-Null
            $ok = ($LASTEXITCODE -eq 0)
        } catch {
            $ok = $false
        } finally {
            $ErrorActionPreference = 'Stop'
        }
        if ($ok) { return , $candidate }
    }
    throw 'Python 3.9+ was not found (install it from python.org, or winget install Python.Python.3.12).'
}
$python = Find-Python
function Invoke-Python([string[]]$Arguments) {
    & $python[0] @($python | Select-Object -Skip 1) @Arguments
    if ($LASTEXITCODE -ne 0) { throw "python $($Arguments -join ' ') failed ($LASTEXITCODE)" }
}

# Toolchain: CMake, Ninja and clang from the framework's portable pack.
$progress = & $python[0] @($python | Select-Object -Skip 1) $cli 'ensure-toolchain' '--project-root' $root '--json-progress'
if ($LASTEXITCODE -ne 0) { throw 'psxrecomp could not provide its toolchain (ensure-toolchain failed).' }
$result = $progress | Where-Object { $_ -like '{*' } | ForEach-Object { $_ | ConvertFrom-Json } |
    Where-Object { $_.event -eq 'result' } | Select-Object -Last 1
$bin = $result.toolchain_bin
if (-not $bin -or -not (Test-Path -LiteralPath (Join-Path $bin 'cmake.exe'))) { throw "No usable toolchain bin reported ($bin)." }
$env:Path = "$bin;$env:Path"
Write-Host "Toolchain: $bin"

# Game C: recompiled from the EXE on your disc. Nothing from the disc is committed.
$marker = Join-Path $root 'generated\SLES_021.93_dispatch.c'
if ($Regenerate -or -not (Test-Path -LiteralPath $marker)) {
    if (-not $Disc) {
        throw 'First build: pass your disc, for example  build.cmd "D:\Games\WCW Mayhem (Europe).cue"'
    }
    $Disc = [IO.Path]::GetFullPath($Disc)
    if (-not (Test-Path -LiteralPath $Disc)) { throw "Disc not found: $Disc" }
    Write-Host "Generating the game C from $Disc (checks the disc, extracts SLES_021.93, recompiles it)"
    Invoke-Python @($cli, 'generate', '--config', (Join-Path $root 'game.toml'), '--project-root', $root, '--disc', $Disc)
}

$build = Join-Path $root $BuildDir
& cmake -S $root -B $build -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_C_COMPILER=clang -DCMAKE_CXX_COMPILER=clang++
if ($LASTEXITCODE -ne 0) { throw 'CMake configure failed.' }
& cmake --build $build --target psx-runtime
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
# The build only re-stages game.toml when the exe relinks; keep it current either way.
Copy-Item -LiteralPath (Join-Path $root 'game.toml'), (Join-Path $root 'game_options.toml') -Destination $build -Force
Write-Host ''
Write-Host "Built: $build\WCWMayhem_Recompiled.exe"
