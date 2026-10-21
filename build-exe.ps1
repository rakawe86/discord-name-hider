# Builds DiscordNameHider.exe (developer machine only; needs Node + Git).
# Run:  powershell -ExecutionPolicy Bypass -File .\build-exe.ps1
$ErrorActionPreference = "Stop"
$env:Path += ";C:\Program Files\Git\cmd"

$vencord = Join-Path $env:USERPROFILE "Vencord"
if (-not (Test-Path $vencord)) { git clone --depth 1 https://github.com/Vendicated/Vencord.git $vencord }

$dest = Join-Path $vencord "src\userplugins\hideUsers"
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $PSScriptRoot "plugin\hideUsers\*") $dest -Recurse -Force

Push-Location $vencord
try {
    if (-not (Get-Command pnpm -ErrorAction SilentlyContinue)) { npm install -g pnpm@11.9.0 }
    pnpm install --frozen-lockfile
    if ($LASTEXITCODE -ne 0) { throw "pnpm install failed" }
    pnpm build
    if ($LASTEXITCODE -ne 0) { throw "Vencord build failed" }
} finally { Pop-Location }

$dist = Join-Path $vencord "dist"
$out  = Join-Path $PSScriptRoot "out"
New-Item -ItemType Directory -Force $out | Out-Null

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$res = "patcher.js","preload.js","renderer.js","renderer.css" | ForEach-Object { "/resource:$dist\$_,vc.$_" }
& $csc /nologo /target:winexe /optimize+ /out:"$out\DiscordNameHider.exe" `
    /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    @res (Join-Path $PSScriptRoot "tool\NameHider.cs")
if ($LASTEXITCODE -ne 0) { throw "csc failed" }
Write-Host "Built $out\DiscordNameHider.exe" -ForegroundColor Green
