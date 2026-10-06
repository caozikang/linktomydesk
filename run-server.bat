@echo off
chcp 65001 >nul
echo ======================================
echo    启动服务端（管理员模式）
echo ======================================
echo.

if not exist publish\Server\RemoteControl.Server.exe (
    echo 错误: 找不到可执行文件
    echo 请先运行 build.bat 编译项目
    pause
    exit /b 1
)

echo 正在以管理员权限启动服务端...
echo.

cd publish\Server
powershell -Command "Start-Process RemoteControl.Server.exe -Verb RunAs"

echo 服务端已启动
echo.
pause
