# 部署 Jellyfin.Plugin.LocalMediaAssets 到本机 Jellyfin（纯插件安装，无需改任何 jellyfin 文件）
# 用法: .\deploy.ps1
# 注意: 请先停止 Jellyfin 再运行（DLL 被进程占用时无法覆盖）。
# Web 注入由插件自愈式完成：插件启动时自动往 jellyfin-web/index.html 补一行脚本标签，
# Jellyfin 升级后重启一次即自动恢复，无需手工改文件。
$ErrorActionPreference = 'Stop'

$dll = Join-Path $PSScriptRoot 'bin\Release\Jellyfin.Plugin.LocalMediaAssets.dll'
if (-not (Test-Path $dll)) { throw "未找到编译产物: $dll（请先运行 .\build.ps1）" }

$dataRoot = Join-Path $env:LOCALAPPDATA 'jellyfin'
$pluginsRoot = Join-Path $dataRoot 'plugins'
if (-not (Test-Path $pluginsRoot)) { throw "未找到 Jellyfin 插件目录: $pluginsRoot" }

$targetDir = Join-Path $pluginsRoot 'LocalMediaAssets'
New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
try {
    Copy-Item $dll $targetDir -Force
    Write-Host "✓ 插件已部署: $targetDir\Jellyfin.Plugin.LocalMediaAssets.dll" -ForegroundColor Green
} catch {
    Write-Host "✗ DLL 被占用，请先停止 Jellyfin 再运行本脚本: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

Write-Host "`n请启动 Jellyfin。首次启动时插件会自动："
Write-Host "  1) 向 jellyfin-web/index.html 注入剧照脚本标签（自动、幂等、可自愈）"
Write-Host "  2) 提供 /LocalMediaAssets/Stills 剧照 API"
Write-Host "浏览器请强制刷新 (Ctrl+F5) 后再看详情页。" -ForegroundColor Yellow
