# 编译 WinPushReceiver.exe（仅需系统自带 csc，无需 VS / Windows SDK 安装 / NuGet）
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

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

& $csc /nologo /target:winexe /platform:anycpu /optimize+ `
  /win32icon:"$root\app.ico" /win32manifest:"$root\app.manifest" /out:"$root\WinPushReceiver.exe" `
  /r:"$winmd" /r:"$fac" "$root\PushReceiver.cs"
if ($LASTEXITCODE -ne 0) { throw "编译失败: $LASTEXITCODE" }

Get-Item "$root\WinPushReceiver.exe" | ForEach-Object { 'OK: ' + $_.FullName + '  ' + $_.Length + ' bytes  ' + $_.LastWriteTime }
if (-not (Test-Path "$root\WinPushReceiver.exe.config")) { throw '缺少 WinPushReceiver.exe.config（发布时需与 exe 一并分发）' }
'提示: 发布 Release 时请同时附上 WinPushReceiver.exe 与 WinPushReceiver.exe.config'
