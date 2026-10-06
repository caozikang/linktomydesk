@echo off
chcp 65001 >nul
echo ======================================
echo    编译 Linux 版本中继服务器
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
echo [2/2] 发布 Linux x64 可执行文件...
dotnet publish RelayServer\RemoteControl.RelayServer.csproj -c Release -r linux-x64 --self-contained false -o publish-linux\RelayServer
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
echo 输出目录: publish-linux\RelayServer\
echo.
echo 下一步：上传到 Linux 服务器
echo   scp -r publish-linux\RelayServer root@your-server-ip:/opt/remote-relay/
echo.
echo 或使用自动部署脚本：
echo   将整个项目上传后运行 deploy-linux.sh
echo.
pause
