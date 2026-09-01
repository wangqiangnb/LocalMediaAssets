# 编译 Jellyfin.Plugin.LocalMediaAssets
$ErrorActionPreference = 'Stop'

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Definition }

$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { throw "未找到 dotnet: $dotnet" }

# 读取本机 Jellyfin 安装目录（jellyfin.path.txt 不入库，避免泄露个人路径）
$pathFile = Join-Path $scriptDir 'jellyfin.path.txt'
if (Test-Path $pathFile) {
    $jfDir = (Get-Content $pathFile -Raw).Trim()
    if ($jfDir) { $env:JellyfinInstallDir = $jfDir }
}

# 把 dotnet/NuGet/MSBuild 的用户目录全部重定向到本工作区，避免写入权限问题
$env:DOTNET_CLI_HOME   = Join-Path $scriptDir '.dotnet-cli'
$env:NUGET_PACKAGES    = Join-Path $scriptDir '.nuget'
$env:TMP               = Join-Path $scriptDir '.tmp'
$env:TEMP              = Join-Path $scriptDir '.tmp'
$env:APPDATA           = Join-Path $scriptDir '.appdata'
$env:LOCALAPPDATA      = Join-Path $scriptDir '.localappdata'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

& $dotnet build (Join-Path $scriptDir 'Jellyfin.Plugin.LocalMediaAssets.csproj') -c Release -v minimal
if ($LASTEXITCODE -ne 0) { throw "编译失败 (exit $LASTEXITCODE)" }

Write-Host "`n产物: $scriptDir\bin\Release\Jellyfin.Plugin.LocalMediaAssets.dll" -ForegroundColor Green
