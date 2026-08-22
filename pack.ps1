param(
    [string]$Configuration = "Release",
    [string]$Version = "1.5.0"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$project = Join-Path $root "src\CortexTransl.App\CortexTransl.App.csproj"
$publishDir = Join-Path $root "dist\win-x64"
$setupScript = Join-Path $root "installer\CortexTransl.iss"
$iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
$userModels = Join-Path $env:LOCALAPPDATA "Cortex Transl\models"
$bundledModels = Join-Path $publishDir "models"

Write-Host "Publishing Cortex Transl $Version..."
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

dotnet publish $project -c $Configuration -p:PublishProfile=Win64
if ($LASTEXITCODE -ne 0) {
    throw "Publish failed."
}

if (Test-Path $userModels) {
    Write-Host "Bundling offline Arabic translation files..."
    if (Test-Path $bundledModels) {
        Remove-Item $bundledModels -Recurse -Force
    }
    Copy-Item $userModels $bundledModels -Recurse
}

$exe = Join-Path $publishDir "CortexTransl.App.exe"
$native = Join-Path $publishDir "bergamot.dll"
if (-not (Test-Path $exe) -or -not (Test-Path $native)) {
    throw "Publish output is missing CortexTransl.App.exe or bergamot.dll."
}

if (-not (Test-Path $iscc)) {
    throw "Inno Setup 6 was not found at $iscc"
}

Write-Host "Building installer..."
& $iscc $setupScript
if ($LASTEXITCODE -ne 0) {
    throw "Installer compile failed."
}

$setup = Join-Path $root "dist\CortexTransl-$Version-Setup.exe"
if (-not (Test-Path $setup)) {
    throw "Installer was not created: $setup"
}

Write-Host ""
Write-Host "Ready: $setup"
Write-Host "Install this file, then open Cortex Transl from the Start menu."
