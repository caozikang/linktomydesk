@echo off
chcp 65001 >nul
echo.
echo ╔════════════════════════════════════════════════╗
echo ║                                                ║
echo ║          远程控制 - 中继服务器                  ║
echo ║                                                ║
echo ╚════════════════════════════════════════════════╝
echo.

if not exist publish\RelayServer\RemoteControl.RelayServer.exe (
    echo 错误: 找不到可执行文件
    echo 请先运行 build-relay.bat 编译项目
    pause
    exit /b 1
)

echo 正在启动中继服务器...
echo.
echo 默认监听端口: 16888
echo 按 Ctrl+C 停止服务器
echo.
echo ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
echo.

cd publish\RelayServer
RemoteControl.RelayServer.exe

pause
