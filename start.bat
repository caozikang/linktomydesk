@echo off
chcp 65001 >nul
echo.
echo ╔════════════════════════════════════════════════╗
echo ║                                                ║
echo ║        远程控制软件 - 快速启动向导              ║
echo ║                                                ║
echo ╚════════════════════════════════════════════════╝
echo.
echo 请选择操作:
echo.
echo   [1] 编译项目
echo   [2] 启动服务端 (被控端 - 需要管理员权限)
echo   [3] 启动客户端 (控制端)
echo   [4] 同时启动服务端和客户端 (本机测试)
echo   [5] 查看帮助文档
echo   [0] 退出
echo.
set /p choice=请输入选项 (0-5):

if "%choice%"=="1" goto BUILD
if "%choice%"=="2" goto SERVER
if "%choice%"=="3" goto CLIENT
if "%choice%"=="4" goto BOTH
if "%choice%"=="5" goto HELP
if "%choice%"=="0" goto END

echo 无效选项，请重试
pause
goto START

:BUILD
echo.
echo ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
echo  开始编译项目...
echo ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
echo.
call build.bat
goto END

:SERVER
echo.
echo ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
echo  启动服务端 (管理员权限)
echo ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
echo.
call run-server.bat
goto END

:CLIENT
echo.
echo ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
echo  启动客户端
echo ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
echo.
call run-client.bat
goto END

:BOTH
echo.
echo ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
echo  本机测试模式
echo ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
echo.
echo 正在启动服务端和客户端...
echo.
echo 服务端将在新窗口中启动（需要管理员权限）
echo 客户端将在 3 秒后启动
echo.
start run-server.bat
timeout /t 3 /nobreak >nul
start run-client.bat
echo.
echo ✓ 两个程序已启动
echo.
echo 使用说明:
echo   1. 在服务端窗口点击"启动服务"
echo   2. 在客户端中连接到 127.0.0.1:5900
echo   3. 输入密码: 123456
echo.
goto END

:HELP
echo.
echo ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
echo  帮助文档
echo ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
echo.
echo 项目文档:
echo   - README.md       : 项目概述和功能介绍
echo   - INSTALL.md      : 详细的安装和使用指南
echo   - DEVELOPMENT.md  : 开发文档和技术细节
echo.
echo 快速开始:
echo   1. 首次使用请先编译项目 (选项 1)
echo   2. 本机测试选择选项 4
echo   3. 远程控制:
echo      - 被控端运行选项 2
echo      - 控制端运行选项 3
echo.
echo 常见问题:
echo   - 无法连接: 检查防火墙和 IP 地址
echo   - 画面卡顿: 降低帧率和画质
echo   - 输入无响应: 确保服务端以管理员权限运行
echo.
start INSTALL.md
goto END

:END
echo.
pause
