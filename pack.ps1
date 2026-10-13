param(
    [string]$Configuration = "Release",
    [string]$Version = "1.6.0"
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

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Version must use three numeric components, for example 1.6.0.'
}
if (-not (Test-Path -LiteralPath $iscc)) {
    throw "Inno Setup 6 was not found at $iscc"
}

function Remove-PackageDirectory([string]$Path) {
    $resolved = [System.IO.Path]::GetFullPath($Path)
    $distRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'dist')) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($distRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a directory outside dist: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        # Do not follow a checkout-local junction into another directory.
        $item = Get-Item -LiteralPath $resolved
        if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            throw "Refusing to clean a linked package directory: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

Write-Host "Publishing Cortex Transl $Version..."
Remove-PackageDirectory $publishDir

dotnet publish $project -c $Configuration -p:PublishProfile=Win64 "-p:Version=$Version" "-p:FileVersion=$Version.0" "-p:InformationalVersion=$Version"
if ($LASTEXITCODE -ne 0) {
    throw "Publish failed."
}

if (Test-Path $userModels) {
    Write-Host "Bundling offline Arabic translation files..."
    Remove-PackageDirectory $bundledModels
    Copy-Item $userModels $bundledModels -Recurse
}

$exe = Join-Path $publishDir "CortexTransl.App.exe"
$native = Join-Path $publishDir "bergamot.dll"
if (-not (Test-Path $exe) -or -not (Test-Path $native)) {
    throw "Publish output is missing CortexTransl.App.exe or bergamot.dll."
}

Write-Host "Building installer..."
& $iscc "/DMyAppVersion=$Version" $setupScript
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
