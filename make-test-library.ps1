# 生成测试媒体库结构（模拟移动硬盘布局）
# 用法: .\make-test-library.ps1 [-Path D:\TestLibrary]
param(
    [string]$Path = 'D:\TestLibrary'
)

$ErrorActionPreference = 'Stop'

function New-MovieFolder([string]$base, [string]$movie, [string[]]$actors, [int]$stills) {
    $dir = Join-Path $base $movie
    New-Item -ItemType Directory -Path (Join-Path $dir 'actors') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $dir 'extrafanart') -Force | Out-Null

    # 演员照片占位（真图请自己替换）
    foreach ($a in $actors) {
        $png = Join-Path $dir "actors\$a.png"
        if (-not (Test-Path $png)) {
            # 生成一个 200x300 的占位 PNG
            $bytes = [byte[]](0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A)
            [IO.File]::WriteAllBytes($png, $bytes)
        }
    }

    # 剧照占位（真图请自己替换）
    for ($i = 1; $i -le $stills; $i++) {
        $f = Join-Path $dir "extrafanart\$i.jpg"
        if (-not (Test-Path $f)) {
            [IO.File]::WriteAllBytes($f, [byte[]](0xFF,0xD8,0xFF,0xE0))
        }
    }

    # 演员信息 json（示例，可手改）
    foreach ($a in $actors) {
        $jf = Join-Path $dir "actors\$a.json"
        if (-not (Test-Path $jf)) {
            Set-Content -Path $jf -Value "{`n  `"name`": `"$a`",`n  `"overview`": `"这是 $a 的本地简介，由插件从视频目录读取。`"`n}" -Encoding UTF8
        }
    }

    Write-Host "已创建: $dir （请把真实视频文件放进去）" -ForegroundColor Green
}

New-MovieFolder $Path '电影A (2020)' @('张三','李四') 3
New-MovieFolder $Path '电影B (2021)' @('王五') 2

Write-Host "`n下一步："
Write-Host "1. 往两个电影文件夹里各放一个真实视频文件（mkv/mp4 均可）"
Write-Host "2. 用真实演员照片替换 actors/*.png，用真实剧照替换 extrafanart/*.jpg"
Write-Host "3. Jellyfin 仪表盘 → 媒体库 → 添加媒体库 → 选择 $Path" -ForegroundColor Yellow
