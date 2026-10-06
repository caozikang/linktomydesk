@echo off
chcp 65001 >nul
echo ======================================
echo    编译中继服务器
echo ======================================
echo.

echo [1/2] 编译项目...
dotnet build RelayServer\RemoteControl.RelayServer.csproj -c Release
if errorlevel 1 (
    echo 错误: 编译失败
    pause
    exit /b 1
)

echo.
echo [2/2] 发布可执行文件...
dotnet publish RelayServer\RemoteControl.RelayServer.csproj -c Release -r win-x64 --self-contained false -o publish\RelayServer
if errorlevel 1 (
    echo 错误: 发布失败
    pause
    exit /b 1
)

echo.
echo ======================================
echo    编译完成！
echo ======================================
echo.
echo 输出目录: publish\RelayServer\RemoteControl.RelayServer.exe
echo.
pause
