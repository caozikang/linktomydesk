@echo off
chcp 65001 >nul
echo ======================================
echo    启动客户端
echo ======================================
echo.

if not exist publish\Client\RemoteControl.Client.exe (
    echo 错误: 找不到可执行文件
    echo 请先运行 build.bat 编译项目
    pause
    exit /b 1
)

echo 正在启动客户端...
echo.

cd publish\Client
start RemoteControl.Client.exe

echo 客户端已启动
echo.
