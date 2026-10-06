#!/bin/bash

# 配置 Systemd 服务

set -e

echo "=========================================="
echo "  配置 Systemd 服务"
echo "=========================================="
echo ""

INSTALL_DIR="/opt/remote-relay"
SERVICE_FILE="/etc/systemd/system/remote-relay.service"

# 检查是否已部署
if [ ! -f "$INSTALL_DIR/RemoteControl.RelayServer" ]; then
    echo "✗ 未找到中继服务器，请先运行 deploy-linux.sh"
    exit 1
fi

# 创建服务用户
echo "[1/3] 创建服务用户..."
if id "relay" &>/dev/null; then
    echo "✓ 用户 relay 已存在"
else
    sudo useradd -r -s /bin/false relay
    echo "✓ 已创建用户 relay"
fi
echo ""

# 设置目录权限
echo "[2/3] 设置权限..."
sudo chown -R relay:relay $INSTALL_DIR
echo "✓ 权限已设置"
echo ""

# 创建 Systemd 服务文件
echo "[3/3] 创建 Systemd 服务..."

sudo tee $SERVICE_FILE > /dev/null <<EOF
[Unit]
Description=Remote Control Relay Server
After=network.target

[Service]
Type=simple
User=relay
Group=relay
WorkingDirectory=$INSTALL_DIR
ExecStart=$INSTALL_DIR/RemoteControl.RelayServer
Restart=always
RestartSec=10

# 日志
StandardOutput=journal
StandardError=journal
SyslogIdentifier=remote-relay

# 安全设置
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/log

[Install]
WantedBy=multi-user.target
EOF

echo "✓ 服务文件已创建: $SERVICE_FILE"
echo ""

# 重载 Systemd
sudo systemctl daemon-reload

echo "=========================================="
echo "  配置完成！"
echo "=========================================="
echo ""
echo "常用命令:"
echo "  启动服务:   sudo systemctl start remote-relay"
echo "  停止服务:   sudo systemctl stop remote-relay"
echo "  重启服务:   sudo systemctl restart remote-relay"
echo "  查看状态:   sudo systemctl status remote-relay"
echo "  查看日志:   sudo journalctl -u remote-relay -f"
echo "  开机自启:   sudo systemctl enable remote-relay"
echo "  禁用自启:   sudo systemctl disable remote-relay"
echo ""
echo "现在启动服务："
echo "  sudo systemctl start remote-relay"
echo "  sudo systemctl enable remote-relay  # 开机自启"
echo ""
