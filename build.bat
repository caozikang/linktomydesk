@echo off
chcp 65001 >nul
echo ======================================
echo    远程控制软件 - 编译脚本
echo ======================================
echo.

echo [1/4] 清理旧的生成文件...
if exist publish rmdir /s /q publish
if exist Server\bin rmdir /s /q Server\bin
if exist Client\bin rmdir /s /q Client\bin
if exist Common\bin rmdir /s /q Common\bin

echo.
echo [2/4] 还原 NuGet 包...
dotnet restore
if errorlevel 1 (
    echo 错误: NuGet 包还原失败
    pause
    exit /b 1
)

echo.
echo [3/4] 编译解决方案...
dotnet build -c Release
if errorlevel 1 (
    echo 错误: 编译失败
    pause
    exit /b 1
)

echo.
echo [4/4] 发布可执行文件...
mkdir publish\Server 2>nul
mkdir publish\Client 2>nul

echo   发布服务端...
dotnet publish Server\RemoteControl.Server.csproj -c Release -r win-x64 --self-contained false -o publish\Server
if errorlevel 1 (
    echo 错误: 服务端发布失败
    pause
    exit /b 1
)

echo   发布客户端...
dotnet publish Client\RemoteControl.Client.csproj -c Release -r win-x64 --self-contained false -o publish\Client
if errorlevel 1 (
    echo 错误: 客户端发布失败
    pause
    exit /b 1
)

echo.
echo ======================================
echo    编译完成！
echo ======================================
echo.
echo 输出目录:
echo   服务端: publish\Server\RemoteControl.Server.exe
echo   客户端: publish\Client\RemoteControl.Client.exe
echo.
echo 注意: 服务端需要管理员权限运行
echo.
pause
