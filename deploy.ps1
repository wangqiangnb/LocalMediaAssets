# 部署 Jellyfin.Plugin.LocalMediaAssets 到本机 Jellyfin（纯插件安装，无需改任何 jellyfin 文件）
# 用法: .\deploy.ps1
# 注意: 请先停止 Jellyfin 再运行（DLL 被进程占用时无法覆盖）。
# Web 注入由插件自愈式完成：插件启动时自动往 jellyfin-web/index.html 补一行脚本标签，
# Jellyfin 升级后重启一次即自动恢复，无需手工改文件。
$ErrorActionPreference = 'Stop'

$dll = Join-Path $PSScriptRoot 'bin\Release\Jellyfin.Plugin.LocalMediaAssets.dll'
if (-not (Test-Path $dll)) { throw "未找到编译产物: $dll（请先运行 .\build.ps1）" }

# 数据目录：优先用显式路径（构建脚本会重定向 LOCALAPPDATA，不能依赖该环境变量）
$dataRoot = Join-Path $env:USERPROFILE 'AppData\Local\jellyfin'
if (-not (Test-Path $dataRoot)) { throw "未找到 Jellyfin 数据目录: $dataRoot" }
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

# ---- 生成/更新 meta.json（修复"插件管理中无法关闭插件且报错"的问题）----
# 背景：若插件目录没有 meta.json，Jellyfin 启动时会用服务器版本号临时充当插件版本并缓存；
# 之后 CreatePluginInstance 虽会用真实版本更新 manifest 并写盘 meta.json，
# 但 LocalPlugin 内存中的版本缓存不会失效，导致插件管理页用真实版本号调用
# Disable/Enable 接口时版本匹配失败（返回 404），表现为"插件无法关闭且报错"。
# 解决：部署时直接生成正确的 meta.json（guid/version/targetAbi 与 DLL 和服务器一致）。
$metaFile = Join-Path $targetDir 'meta.json'
$pluginGuid = '8f2a9d0e-3c4b-4a5f-9e6d-1b2c3d4e5f60'

# 插件版本 = DLL 文件版本（如 0.9.1.0）
$pluginVersion = (Get-Item $dll).VersionInfo.FileVersion
if ([string]::IsNullOrWhiteSpace($pluginVersion)) { $pluginVersion = '0.9.1.0' }

# targetAbi = 当前 Jellyfin 服务器版本（从 jellyfin.exe 读取；找不到时回退为插件版本）
# jellyfin.path.txt 可能含多行（第一行是安装目录，其余是备注），只取第一行
$jellyfinExe = $null
$pathFile = Join-Path $PSScriptRoot 'jellyfin.path.txt'
if (Test-Path $pathFile) {
    $jfDir = ((Get-Content $pathFile -Raw) -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1).Trim()
    if ($jfDir) { $jellyfinExe = Join-Path $jfDir 'jellyfin.exe' }
}
if (-not $jellyfinExe -or -not (Test-Path $jellyfinExe)) {
    $jellyfinExe = Join-Path $dataRoot 'jellyfin.exe'
}
$targetAbi = if (Test-Path $jellyfinExe) { (Get-Item $jellyfinExe).VersionInfo.FileVersion } else { $pluginVersion }
if ([string]::IsNullOrWhiteSpace($targetAbi)) { $targetAbi = $pluginVersion }

# 保留已有 status（用户可能已禁用插件），不存在则 Active
$status = 'Active'
if (Test-Path $metaFile) {
    try {
        $existing = Get-Content $metaFile -Raw | ConvertFrom-Json
        if ($existing.status) { $status = [string]$existing.status }
    } catch { $status = 'Active' }
}

$manifest = [ordered]@{
    category    = 'Metadata'
    changelog   = ''
    description = 'Jellyfin 演员界面优化与预览图优化。项目主页：https://github.com/wangqiangnb/LocalMediaAssets'
    guid        = $pluginGuid
    name        = 'LocalMediaAssets'
    overview    = 'Jellyfin 演员界面优化与预览图优化：演员照片/简介跟随媒体文件本地化，详情页展示预览图片与剧照、预告片、演员卡片（绿光跳转详情页/红光触发刮削），支持四来源同步与 NFO 补写，换机器无需重新刮削。设置页：http://<服务器>/LocalMediaAssets/config。项目主页：https://github.com/wangqiangnb/LocalMediaAssets'
    owner       = 'local'
    targetAbi   = $targetAbi
    timestamp   = (Get-Date).ToUniversalTime().ToString('o')
    version     = $pluginVersion
    status      = $status
    autoUpdate  = $false
    imagePath   = $null
    assemblies  = @()
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $metaFile -Encoding UTF8
Write-Host "✓ meta.json 已生成（version=$pluginVersion, targetAbi=$targetAbi, status=$status）" -ForegroundColor Green

Write-Host "`n请启动 Jellyfin。首次启动时插件会自动："
Write-Host "  1) 向 jellyfin-web/index.html 注入详情页优化脚本（一行带标记，可精确移除）"
Write-Host "  2) 提供 /LocalMediaAssets/Stills 剧照 API"
Write-Host "浏览器请强制刷新 (Ctrl+F5) 后再看详情页。" -ForegroundColor Yellow
