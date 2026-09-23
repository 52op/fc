# 编译并运行全部测试。
# 用法： powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1
#
# 测试源码放在项目 tests\ 目录（随 git 管理，不再放临时目录，
# 避免被“一键清理”清掉 %TEMP% 时误删）。

$ErrorActionPreference = 'Stop'
$testsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projDir = Split-Path -Parent $testsDir
$fcExe = Join-Path $projDir 'bin\Debug\FC.exe'

# 引用：项目 exe + .NET Framework 4.8 参考程序集
$fx = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$names = @(
    'mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Configuration.dll',
    'System.Windows.dll', 'System.Xaml.dll', 'System.Data.dll', 'System.Xml.dll',
    'System.Xml.Linq.dll', 'System.Net.Http.dll', 'WindowsBase.dll',
    'PresentationCore.dll', 'PresentationFramework.dll', 'Microsoft.CSharp.dll',
    'System.Windows.Forms.dll'
)
$refs = ($names | ForEach-Object { Join-Path $fx $_ }) -join ','
$refs = "$fcExe,$refs"

# 定位 csc（Roslyn）
$cscCandidates = @(
    'D:\letvar\dev\MVS\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe',
    'C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\Roslyn\csc.exe'
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) {
    $csc = (Get-Command csc.exe -ErrorAction SilentlyContinue).Source
}
if (-not $csc) { throw "找不到 csc.exe（Roslyn）" }
Write-Host "csc: $csc"

if (-not (Test-Path $fcExe)) { throw "找不到 FC.exe，请先构建项目：$fcExe" }
# 把最新 FC.exe 复制进 tests（测试运行期 CLR 从这里加载）
Copy-Item $fcExe (Join-Path $testsDir 'FC.exe') -Force

$tests = @('Test', 'QuickTest', 'LockTest', 'CleanupFeatureTest', 'CleanupPropTest',
           'CleanupProgressTest', 'CleanupLogFormatTest', 'CleanupFastTest', 'StreamSizeTest',
           'RecycleBinTest', 'SnapshotTest', 'MenuCmdTest')

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
