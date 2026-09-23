# 一键打包发布版（zip，含说明）。
# 用法： powershell -ExecutionPolicy Bypass -File pack.ps1
# 产物： dist\FC-<版本>.zip  ← 把 zip 拷贝到别的机器解压即可，双击 FC.exe 运行。
#
# 分发只需要两个文件：FC.exe + FC.exe.config。
# 目标机器需装有 .NET Framework 4.8（Windows 10 1809+/11 自带，免装）。

$ErrorActionPreference = 'Stop'
$projDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$msbuild = 'd:\letvar\dev\MVS\18\Community\MSBuild\Current\Bin\MSBuild.exe'

# 1) Release 构建
Write-Host "== Release build =="
Push-Location $projDir
try {
    & $msbuild FC.csproj /p:Configuration=Release /v:m /nologo
    if ($LASTEXITCODE -ne 0) { throw "构建失败" }
} finally {
    Pop-Location
}

$rel = Join-Path $projDir 'bin\Release'
$exe = Join-Path $rel 'FC.exe'
$cfg = Join-Path $rel 'FC.exe.config'
if (-not (Test-Path $exe)) { throw "找不到 $exe" }

# 2) 测温内容
$ver = Get-Date -Format 'yyyyMMdd-HHmm'
$pkgName = 'FC-' + $ver
$stage = Join-Path $projDir ('dist\' + $pkgName)
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item $exe $stage
if (Test-Path $cfg) { Copy-Item $cfg $stage }

# 3) 简单说明
$readme = @"
FC - 磁盘目录分析迁移工具
【运行】双击 FC.exe（无需安装，无需管理员即可浏览；清理系统目录/迁移受保护目录时按要求提权）
【系统要求】Windows 10/11（自带 .NET Framework 4.8）或已安装 .NET Framework 4.8 的系统
【功能】
  - 扫描磁盘/目录，树形显示占用（按大小实时排序），定位"谁占满了 C 盘"
  - 一键清理建议：回收站、临时文件、更新缓存、缩略图等（可真实清理）
  - 最大文件 Top 100、文件类型统计、与上次扫描的增量对比
  - 系统审计：休眠文件(powercfg 一键关闭)、虚拟内存设置、WinSxS(DISM 一键清理)
  - 右键目录 → 迁移（robocopy 复制 + 校验 + junction 链接），可随时还原
【数据】程序把自己的配置/缓存写到 %APPDATA%\FC，卸载/删除本目录不影响系统
"@
[System.IO.File]::WriteAllText((Join-Path $stage '说明.txt'), $readme, [System.Text.Encoding]::UTF8)

# 4) zip
$dist = Join-Path $projDir 'dist'
$zip = Join-Path $dist ($pkgName + '.zip')
if (Test-Path $zip) { Remove-Item $zip -Force }
Push-Location $stage
try {
    & powershell -NoProfile -Command "Compress-Archive -Path '.\*' -DestinationPath '$zip'"
} finally {
    Pop-Location
}
Remove-Item $stage -Recurse -Force

Write-Host "`n打包完成: $zip"
Write-Host "分发内容: FC.exe + FC.exe.config"