#!/bin/bash

# 远程控制 - 中继服务器 Linux 部署脚本

set -e

echo "=========================================="
echo "  远程控制 - 中继服务器 Linux 部署"
echo "=========================================="
echo ""

# 检测操作系统
if [ -f /etc/os-release ]; then
    . /etc/os-release
    OS=$ID
    VER=$VERSION_ID
else
    echo "无法检测操作系统"
    exit 1
fi

echo "检测到操作系统: $OS $VER"
echo ""

# 安装 .NET 6.0 Runtime
echo "[1/5] 检查 .NET 6.0 Runtime..."

if command -v dotnet &> /dev/null; then
    DOTNET_VERSION=$(dotnet --version)
    echo "✓ 已安装 .NET: $DOTNET_VERSION"
else
    echo "未安装 .NET 6.0 Runtime，正在安装..."

    if [ "$OS" = "ubuntu" ] || [ "$OS" = "debian" ]; then
        # Ubuntu/Debian
        wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
        sudo dpkg -i packages-microsoft-prod.deb
        rm packages-microsoft-prod.deb

        sudo apt-get update
        sudo apt-get install -y dotnet-runtime-6.0

    elif [ "$OS" = "centos" ] || [ "$OS" = "rhel" ]; then
        # CentOS/RHEL
        sudo rpm -Uvh https://packages.microsoft.com/config/centos/7/packages-microsoft-prod.rpm
        sudo yum install -y dotnet-runtime-6.0

    else
        echo "不支持的操作系统，请手动安装 .NET 6.0 Runtime"
        echo "参考: https://dotnet.microsoft.com/download/dotnet/6.0"
        exit 1
    fi

    echo "✓ .NET 6.0 Runtime 安装完成"
fi

echo ""

# 创建部署目录
echo "[2/5] 创建部署目录..."
INSTALL_DIR="/opt/remote-relay"
sudo mkdir -p $INSTALL_DIR
echo "✓ 部署目录: $INSTALL_DIR"
echo ""

# 编译并发布
echo "[3/5] 编译中继服务器..."
dotnet publish RelayServer/RemoteControl.RelayServer.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained false \
    -o ./publish-linux/RelayServer

if [ $? -ne 0 ]; then
    echo "✗ 编译失败"
    exit 1
fi

echo "✓ 编译完成"
echo ""

# 复制文件
echo "[4/5] 部署文件..."
sudo cp -r ./publish-linux/RelayServer/* $INSTALL_DIR/
sudo chmod +x $INSTALL_DIR/RemoteControl.RelayServer
echo "✓ 文件已复制到 $INSTALL_DIR"
echo ""

# 配置防火墙
echo "[5/5] 配置防火墙..."

if command -v ufw &> /dev/null; then
    # Ubuntu UFW
    sudo ufw allow 16888/tcp
    echo "✓ UFW: 已开放端口 16888"
elif command -v firewall-cmd &> /dev/null; then
    # CentOS firewalld
    sudo firewall-cmd --permanent --add-port=16888/tcp
    sudo firewall-cmd --reload
    echo "✓ Firewalld: 已开放端口 16888"
else
    echo "⚠ 未检测到防火墙，请手动开放端口 16888"
fi

echo ""
echo "=========================================="
echo "  部署完成！"
echo "=========================================="
echo ""
echo "安装位置: $INSTALL_DIR"
echo "监听端口: 16888"
echo ""
echo "启动服务器:"
echo "  方式1 (前台运行): sudo $INSTALL_DIR/RemoteControl.RelayServer"
echo "  方式2 (后台运行): sudo systemctl start remote-relay"
echo "  方式3 (手动后台): nohup sudo $INSTALL_DIR/RemoteControl.RelayServer > /var/log/remote-relay.log 2>&1 &"
echo ""
echo "配置 Systemd 服务 (推荐):"
echo "  sudo bash deploy-linux-service.sh"
echo ""
