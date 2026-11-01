# 重新编译并发布单文件 exe
# 需要 .NET 8 SDK（dotnet --list-sdks 里能看到 8.x）
# 产物：dist\Suzu.exe（依赖 .NET 8 Desktop Runtime）

$ErrorActionPreference = "Stop"
Set-Location -Path $PSScriptRoot

dotnet publish .\Suzu.csproj -c Release -o .\dist

if ($LASTEXITCODE -ne 0) {
    Write-Host "发布失败，检查是否安装了 .NET 8 SDK。" -ForegroundColor Red
    exit 1
}

Get-ChildItem .\dist | Format-Table Name, Length, LastWriteTime -AutoSize
Write-Host "完成：dist\Suzu.exe（需要管理员权限运行）" -ForegroundColor Green
