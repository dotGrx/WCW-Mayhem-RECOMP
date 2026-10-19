# Fetches the psxrecomp framework into psxrecomp\ at the commit this port is built on and applies
# this project's patches (patches\psxrecomp-*.patch). Safe to run again: it resets the checkout to
# that commit and applies the patches afresh.
#
#   powershell -ExecutionPolicy Bypass -File scripts\setup-psxrecomp.ps1
#   ... -Source <path to an existing psxrecomp clone>   (offline: reuses its objects and submodules)
param(
    [string]$Source = 'https://github.com/mstan/psxrecomp.git',
    [string]$Dir = (Join-Path $PSScriptRoot '..\psxrecomp')
)
$ErrorActionPreference = 'Stop'
$Commit = '8077e71098ce0347d7d41d267630bc856816cf77'  # psxrecomp master, 2026-10-06
$Patches = @(
    'psxrecomp-pgxp-wrapped-shift.patch',     # PGXP keeps WCW's subdivided wrestler polygons precise
    'psxrecomp-signed-x-bound-16bit.patch'    # widescreen: widen the prop on-screen test's left edge
)
# LF files as upstream has them (the patches are LF), long paths (the recompiler's rabbitizer tables
# exceed 260 characters), and local submodule sources allowed for -Source clones.
$GitOptions = @('-c', 'core.autocrlf=false', '-c', 'core.longpaths=true', '-c', 'protocol.file.allow=always',
                '-c', 'http.sslBackend=schannel')

function Invoke-Git([string[]]$Arguments) {
    & git @GitOptions @Arguments
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed ($LASTEXITCODE)" }
}

$Dir = [IO.Path]::GetFullPath($Dir)
if (-not (Test-Path (Join-Path $Dir '.git'))) {
    Write-Host "Cloning psxrecomp from $Source"
    Invoke-Git @('clone', '--no-checkout', $Source, $Dir)
}
Invoke-Git @('-C', $Dir, 'config', 'core.longpaths', 'true')
Invoke-Git @('-C', $Dir, 'config', 'core.autocrlf', 'false')
Invoke-Git @('-C', $Dir, 'checkout', '--force', $Commit)
Invoke-Git @('-C', $Dir, 'clean', '-fdq', '--', 'runtime', 'recompiler', 'docs')

# Submodules: retcomm-rbengine (rewind) is linked into the game; recomp-net only for netplay builds.
Invoke-Git @('-C', $Dir, 'submodule', 'init')
if (Test-Path -LiteralPath $Source) {
    # An existing clone keeps each submodule's repository under .git\modules: use those.
    $modulesRoot = Join-Path ([IO.Path]::GetFullPath($Source)) '.git\modules'
    $entries = & git -C $Dir config -f .gitmodules --get-regexp '^submodule\..*\.path$'
    foreach ($entry in $entries) {
        $key, $path = $entry -split ' ', 2
        $name = $key -replace '^submodule\.', '' -replace '\.path$', ''
        $local = Join-Path $modulesRoot $path
        if (Test-Path -LiteralPath $local) { Invoke-Git @('-C', $Dir, 'config', "submodule.$name.url", $local) }
    }
}
Invoke-Git @('-C', $Dir, 'submodule', 'update', '--force')

foreach ($patch in $Patches) {
    $file = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\patches\$patch"))
    Write-Host "Applying $patch"
    Invoke-Git @('-C', $Dir, 'apply', '--whitespace=nowarn', $file)
}
Write-Host "psxrecomp ready at $Dir ($($Commit.Substring(0, 8)) + $($Patches.Count) patches)."
Write-Host 'Next: build.cmd "<path to your WCW Mayhem (Europe).cue>"'
