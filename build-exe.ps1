# Builds DiscordNameHider.exe (developer machine only; needs Node + Git).
# Run:  powershell -ExecutionPolicy Bypass -File .\build-exe.ps1
$ErrorActionPreference = "Stop"
$env:Path += ";C:\Program Files\Git\cmd"

$vencord = Join-Path $env:USERPROFILE "Vencord"
# Pinned so every build uses the same, tested code. Bump it when Discord updates break Vencord (v1.15.16).
$vencordCommit = "383409e6a6ec9008ee8a7496500f1e75f17824d1"
if (-not (Test-Path $vencord)) {
    git init -q $vencord
    git -C $vencord remote add origin https://github.com/Vendicated/Vencord.git # Vencord's build reads this
}
if ((git -C $vencord rev-parse HEAD 2>$null) -ne $vencordCommit) {
    git -C $vencord fetch -q --depth 1 origin $vencordCommit
    git -C $vencord checkout -q FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { throw "Could not download Vencord $vencordCommit" }
}

$dest = Join-Path $vencord "src\userplugins\hideUsers"
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $PSScriptRoot "plugin\hideUsers\*") $dest -Recurse -Force

Push-Location $vencord
try {
    # Use exactly the pnpm this Vencord version asks for.
    # Use exactly the pnpm this Vencord version asks for.
    $want = ((Get-Content package.json -Raw | ConvertFrom-Json).packageManager -split "@")[1]
    if (-not (Get-Command pnpm -ErrorAction SilentlyContinue) -or (pnpm --version) -ne $want) { npm install -g "pnpm@$want" }
    pnpm install --frozen-lockfile
    if ($LASTEXITCODE -ne 0) { throw "pnpm install failed (antivirus can block pnpm's program; the GitHub build is unaffected)" }
    pnpm build
    if ($LASTEXITCODE -ne 0) { throw "Vencord build failed" }
} finally { Pop-Location }

$dist = Join-Path $vencord "dist"
$out  = Join-Path $PSScriptRoot "out"
New-Item -ItemType Directory -Force $out | Out-Null

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$res = "patcher.js","preload.js","renderer.js","renderer.css" | ForEach-Object { "/resource:$dist\$_,vc.$_" }
$res += "/resource:$PSScriptRoot\assets\icon.png,app.icon.png"
& $csc /nologo /target:winexe /optimize+ /win32icon:"$PSScriptRoot\tool\app.ico" /out:"$out\DiscordNameHider.exe" `
    /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    @res (Join-Path $PSScriptRoot "tool\NameHider.cs")
if ($LASTEXITCODE -ne 0) { throw "csc failed" }
Copy-Item "$out\DiscordNameHider.exe" (Join-Path $PSScriptRoot "DiscordNameHider.exe") -Force
Write-Host "Built $out\DiscordNameHider.exe" -ForegroundColor Green
