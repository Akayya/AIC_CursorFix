# Build the CursorFix plugin with the .NET Framework 4 csc compiler.
# Usage:  Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force; & .\build.ps1 -Root "D:\path\to\AliceInCradle_ver030"
param(
    [string]$Root = ''
)

$ErrorActionPreference = 'Stop'

if (-not $Root) { throw "Please pass the game root, e.g. -Root 'D:\Games\AliceInCradle_ver030'" }

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }

$src     = Join-Path $PSScriptRoot 'CursorFix.cs'
$out     = Join-Path $Root 'BepInEx\plugins\CursorFix.dll'
$managed = Join-Path $Root 'AliceInCradle_Data\Managed'
$core    = Join-Path $Root 'BepInEx\core'

$refs = @(
    (Join-Path $core 'BepInEx.dll'),
    (Join-Path $core '0Harmony.dll'),
    (Join-Path $managed 'UnityEngine.dll'),
    (Join-Path $managed 'UnityEngine.CoreModule.dll'),
    (Join-Path $managed 'UnityEngine.IMGUIModule.dll'),
    (Join-Path $managed 'netstandard.dll')
)
foreach ($r in $refs) { if (-not (Test-Path $r)) { throw "missing reference: $r" } }

$argsList = @('/target:library', '/platform:anycpu', '/optimize+', '/warn:0', '/nologo')
$argsList += "/out:`"$out`""
$argsList += $refs | ForEach-Object { "/reference:`"$_`"" }
# NOTE: source files are positional arguments; "/source:..." is not a csc option (CS2007).
$argsList += "`"$src`""

Write-Host "csc $($argsList -join ' ')"
& $csc @argsList
$code = $LASTEXITCODE
if ($code -ne 0) { throw "csc failed with exit code $code" }

Get-Item $out | Select-Object FullName, Length, LastWriteTime
