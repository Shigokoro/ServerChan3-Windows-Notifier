# 编译 WinPushReceiver.exe（仅需系统自带 csc，无需 VS / Windows SDK 安装 / NuGet）
param([string]$OutDir = '')          # 产物目录；留空 = 与脚本同目录
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrEmpty($OutDir)) { $OutDir = $root }
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$exePath = Join-Path $OutDir 'WinPushReceiver.exe'

# csc（.NET Framework 4.x 自带）
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }

# WinRT 元数据：必须取 UnionMetadata\<版本>\Windows.winmd（数 MB 的真元数据），
# 不能取 Facade\Windows.winmd（百 KB 的类型转发桩，会导致 CS1070 找不到 WinRT 类型）
$winmdRoot = 'C:\Program Files (x86)\Windows Kits\10\UnionMetadata'
$winmd = Get-ChildItem $winmdRoot -Recurse -Filter 'Windows.winmd' -ErrorAction SilentlyContinue |
         Where-Object { $_.FullName -notlike '*\Facade\*' -and $_.Length -gt 1MB } |
         Sort-Object Length -Descending | Select-Object -First 1 -ExpandProperty FullName

# .NET Framework facade：WinRT 类型需要 System.Runtime 门面程序集
$fac = Get-ChildItem 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework' -Directory -ErrorAction SilentlyContinue |
       Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'Facades\System.Runtime.dll' } |
       Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not (Test-Path $csc))    { throw "找不到 csc: $csc" }
if (-not $winmd)              { throw "找不到 WinRT 元数据（$winmdRoot 下没有 >1MB 的 Windows.winmd；请安装 Windows SDK）" }
if (-not $fac)                { throw '找不到 .NET Framework Facades\System.Runtime.dll（需要 .NET Framework 4.5+ 的开发包或 4.8 运行时）' }

Write-Host "csc   : $csc"
Write-Host "winmd : $winmd"
Write-Host "facade: $fac"

# UI-GUARD：文字必须统一走 GDI（TextRenderer.DrawText）。GDI+ DrawString 在小字号下不做 hinting，
# 会出现锯齿/发虚，且与全局标签不一致 —— 一旦源码里再出现它，直接让构建失败。
$guard = Select-String -Path (Join-Path $root 'PushReceiver.cs') -Pattern 'DrawString(' -SimpleMatch
if ($guard) {
    $guard | ForEach-Object { Write-Host ('  ' + $_.LineNumber + ': ' + $_.Line.Trim()) }
    throw 'UI-GUARD 失败：PushReceiver.cs 中仍有 GDI+ DrawString(，请改用 TextRenderer.DrawText'
}

& $csc /nologo /target:winexe /platform:anycpu /optimize+ `
  /win32icon:"$root\app.ico" /win32manifest:"$root\app.manifest" /out:"$exePath" `
  /r:"$winmd" /r:"$fac" "$root\PushReceiver.cs"
if ($LASTEXITCODE -ne 0) { throw "编译失败: $LASTEXITCODE" }

Get-Item $exePath | ForEach-Object { 'OK: ' + $_.FullName + '  ' + $_.Length + ' bytes  ' + $_.LastWriteTime }
'提示: 产物是单个 exe，直接分发即可（DPI 感知由内嵌的 app.manifest 提供，无需 .exe.config）'
