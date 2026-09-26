# 编译并运行全部测试。
# 用法： powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1
#
# 测试源码放在项目 tests\ 目录（随 git 管理，不再放临时目录，
# 避免被“一键清理”清掉 %TEMP% 时误删）。

$ErrorActionPreference = 'Stop'
$testsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projDir = Split-Path -Parent $testsDir
$fcExe = Join-Path $projDir 'bin\Debug\FC.exe'

# ==================== 环境发现（不写死路径，换机免改） ====================

# 定位 MSBuild：vswhere → PATH → 常见安装根扫描
function Find-MsBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $found = & $vswhere -all -products * -requires Microsoft.Component.MSBuild `
            -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null | Select-Object -First 1
        if ($found -and (Test-Path $found)) { return $found }
    }
    $cmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($root in @("$env:ProgramFiles\Microsoft Visual Studio",
                        "${env:ProgramFiles(x86)}\Microsoft Visual Studio")) {
        if (-not (Test-Path $root)) { continue }
        $hit = Get-ChildItem $root -Recurse -Filter MSBuild.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\MSBuild\\.*\\Bin\\MSBuild\.exe$' } |
            Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

# 定位 csc（Roslyn）：优先从 MSBuild 同级 Roslyn 目录派生，再 PATH / 常见根
function Find-Csc {
    $msbuild = Find-MsBuild
    if ($msbuild) {
        $csc = Join-Path (Split-Path $msbuild -Parent) 'Roslyn\csc.exe'
        if (Test-Path $csc) { return $csc }
    }
    $cmd = Get-Command csc.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($root in @("$env:ProgramFiles\Microsoft Visual Studio",
                        "${env:ProgramFiles(x86)}\Microsoft Visual Studio")) {
        if (-not (Test-Path $root)) { continue }
        $hit = Get-ChildItem $root -Recurse -Filter csc.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\Roslyn\\csc\.exe$' } |
            Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    return $null
}

# 定位 .NET Framework v4.8 引用程序集（编译测试用）：
#   1) 标准 Reference Assemblies 路径（装了 Targeting Pack 才有）
#   2) 从 v4 运行时目录（v4.0.30319 全在 mscorlib 等）回落
function Find-FrameworkRefDir {
    $std = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
    if (Test-Path (Join-Path $std 'mscorlib.dll')) { return $std }
    foreach ($d in @("$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319",
                     "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319")) {
        if (Test-Path (Join-Path $d 'mscorlib.dll')) { return $d }
    }
    return $null
}

$csc = Find-Csc
if (-not $csc) { throw "找不到 csc.exe（Roslyn）。请安装 Visual Studio 或 Build Tools（含 .NET 桌面开发工作负载）。" }
Write-Host "csc: $csc"

$fx = Find-FrameworkRefDir
if (-not $fx) { throw "找不到 .NET Framework 4.x 引用/运行时程序集目录。" }
Write-Host "framework refs: $fx"

# 引用：项目 exe + .NET Framework 程序集
$names = @(
    'mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Configuration.dll',
    'System.Windows.dll', 'System.Xaml.dll', 'System.Data.dll', 'System.Xml.dll',
    'System.Xml.Linq.dll', 'System.Net.Http.dll', 'WindowsBase.dll',
    'PresentationCore.dll', 'PresentationFramework.dll', 'Microsoft.CSharp.dll',
    'System.Windows.Forms.dll'
)
$refs = ($names | ForEach-Object { Join-Path $fx $_ }) -join ','
$refs = "$fcExe,$refs"

if (-not (Test-Path $fcExe)) { throw "找不到 FC.exe，请先构建项目：$fcExe" }
# 把最新 FC.exe 复制进 tests（测试运行期 CLR 从这里加载）
Copy-Item $fcExe (Join-Path $testsDir 'FC.exe') -Force

$tests = @('Test', 'QuickTest', 'LockTest', 'CleanupFeatureTest', 'CleanupPropTest',
           'CleanupProgressTest', 'CleanupLogFormatTest', 'CleanupFastTest', 'StreamSizeTest',
           'RecycleBinTest', 'SnapshotTest', 'MenuCmdTest', 'EnvVarTest', 'EnvUiTest')

$failed = @()
foreach ($t in $tests) {
    $src = Join-Path $testsDir "$t.cs"
    if (-not (Test-Path $src)) { Write-Host "SKIP $t (no source)"; continue }
    $exe = Join-Path $testsDir "$t.exe"
    Write-Host "`n=== build $t ==="
    & $csc /nologo /target:exe "/out:$exe" "/reference:$refs" $src
    if ($LASTEXITCODE -ne 0) { $failed += "$t(build)"; continue }

    Write-Host "=== run $t ==="
    $out = & $exe 2>&1
    $out | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { $failed += "$t(run exit $LASTEXITCODE)" }
}

Write-Host "`n================================"
if ($failed.Count -eq 0) {
    Write-Host "ALL TEST SUITES PASSED"
    exit 0
} else {
    Write-Host ("FAILED: " + ($failed -join ', '))
    exit 1
}
