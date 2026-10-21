# Installs Vencord with the HideUsers plugin into your Discord.
# Run:  powershell -ExecutionPolicy Bypass -File .\install.ps1
$ErrorActionPreference = "Stop"
$env:Path += ";C:\Program Files\Git\cmd"

$vencord = Join-Path $env:USERPROFILE "Vencord"
$plugin  = Join-Path $PSScriptRoot "plugin\hideUsers"

if (-not (Test-Path $vencord)) {
    Write-Host "Downloading Vencord source..." -ForegroundColor Cyan
    git clone --depth 1 https://github.com/Vendicated/Vencord.git $vencord
} else {
    Write-Host "Updating Vencord source..." -ForegroundColor Cyan
    git -C $vencord pull --ff-only
}

$dest = Join-Path $vencord "src\userplugins\hideUsers"
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $plugin "*") $dest -Recurse -Force
Write-Host "Plugin copied to $dest" -ForegroundColor Green

if (-not (Get-Command pnpm -ErrorAction SilentlyContinue)) {
    Write-Host "Installing pnpm..." -ForegroundColor Cyan
    npm install -g pnpm
}

Push-Location $vencord
try {
    pnpm install --frozen-lockfile
    pnpm build
    Write-Host "`nClose Discord completely (also from the tray), then choose your Discord install in the next step." -ForegroundColor Yellow
    pnpm inject
} finally {
    Pop-Location
}

Write-Host "`nDone! Open Discord > Settings > Vencord > Plugins > HideUsers, enable it, and add names/IDs." -ForegroundColor Green
