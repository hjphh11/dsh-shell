# DshShell one-click build script
# Usage:  pwsh .\build.ps1            -> build + publish to .\publish
#         pwsh .\build.ps1 -RebuildIcon  -> also regenerate app.ico from deepseek-color.svg (requires tools/sharp)
param(
    [switch]$RebuildIcon
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

if ($RebuildIcon) {
    Write-Host "==> Regenerating app.ico from deepseek-color.svg ..."
    Push-Location "$root\tools"
    if (-not (Test-Path package.json)) { npm init -y | Out-Null; npm i sharp | Out-Null }
    node make-ico.js
    Pop-Location
}

Write-Host "==> Building ..."
dotnet build "$root\DshShell.csproj" -c Release
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Host "==> Publishing self-contained single-file exe ..."
dotnet publish "$root\DshShell.csproj" -c Release -p:PublishProfile=WinX64 -o "$root\publish"
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# strip debug/doc artifacts from the distribution
Remove-Item "$root\publish\DshShell.pdb", "$root\publish\*.xml" -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Done. Output:" -ForegroundColor Green
Get-ChildItem "$root\publish" | ForEach-Object { Write-Host ("  " + $_.Name) }
Write-Host ""
Write-Host "Single standalone exe: publish\DshShell.exe (.NET runtime + splash embedded, no other files needed)."
