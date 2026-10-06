# Linux 部署中继服务器完整指南

## 系统要求

### 支持的 Linux 发行版
- ✅ Ubuntu 18.04 / 20.04 / 22.04
- ✅ Debian 10 / 11
- ✅ CentOS 7 / 8 / Stream
- ✅ RHEL 7 / 8
- ✅ Fedora 35+
- ✅ openSUSE Leap 15+

### 最低配置
- CPU: 1 核
- 内存: 512MB
- 磁盘: 1GB
- 带宽: 5 Mbps

### 推荐配置
- CPU: 2 核
- 内存: 1GB
- 磁盘: 10GB
- 带宽: 10 Mbps

## 快速部署

### 方法 1: 自动部署脚本（推荐）

```bash
# 1. 上传项目到服务器
scp -r RemoteControl root@your-server-ip:/root/

# 2. SSH 登录服务器
ssh root@your-server-ip

# 3. 进入项目目录
cd /root/RemoteControl

# 4. 给脚本执行权限
chmod +x deploy-linux.sh
chmod +x deploy-linux-service.sh

# 5. 运行部署脚本
sudo bash deploy-linux.sh

# 6. 配置 Systemd 服务（推荐）
sudo bash deploy-linux-service.sh

# 7. 启动服务
sudo systemctl start remote-relay
sudo systemctl enable remote-relay  # 开机自启

# 8. 查看状态
sudo systemctl status remote-relay
```

### 方法 2: 手动部署

#### 步骤 1: 安装 .NET 6.0 Runtime

**Ubuntu/Debian:**
```bash
wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb

sudo apt-get update
sudo apt-get install -y dotnet-runtime-6.0
```

**CentOS/RHEL:**
```bash
sudo rpm -Uvh https://packages.microsoft.com/config/centos/7/packages-microsoft-prod.rpm
sudo yum install -y dotnet-runtime-6.0
```

**验证安装:**
```bash
dotnet --version
# 应显示 6.0.x
```

#### 步骤 2: 编译项目

```bash
# 在开发机器上编译
cd RemoteControl
dotnet publish RelayServer/RemoteControl.RelayServer.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained false \
    -o ./publish-linux/RelayServer
```

#### 步骤 3: 上传到服务器

```bash
# 打包
tar -czf relay-server.tar.gz -C publish-linux RelayServer

# 上传
scp relay-server.tar.gz root@your-server-ip:/tmp/

# 在服务器上解压
ssh root@your-server-ip
cd /opt
sudo mkdir -p remote-relay
sudo tar -xzf /tmp/relay-server.tar.gz -C remote-relay
sudo chmod +x /opt/remote-relay/RemoteControl.RelayServer
```

#### 步骤 4: 配置防火墙

**Ubuntu (UFW):**
```bash
sudo ufw allow 16888/tcp
sudo ufw status
```

**CentOS (Firewalld):**
```bash
sudo firewall-cmd --permanent --add-port=16888/tcp
sudo firewall-cmd --reload
sudo firewall-cmd --list-ports
```

**阿里云/腾讯云安全组:**
- 登录云控制台
- 进入安全组设置
- 添加入站规则：TCP 16888

#### 步骤 5: 创建 Systemd 服务

```bash
# 创建服务用户
sudo useradd -r -s /bin/false relay

# 设置权限
sudo chown -R relay:relay /opt/remote-relay

# 创建服务文件
sudo nano /etc/systemd/system/remote-relay.service
```

内容如下：
```ini
[Unit]
Description=Remote Control Relay Server
After=network.target

[Service]
Type=simple
User=relay
Group=relay
WorkingDirectory=/opt/remote-relay
ExecStart=/opt/remote-relay/RemoteControl.RelayServer
Restart=always
RestartSec=10

StandardOutput=journal
StandardError=journal
SyslogIdentifier=remote-relay

NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true

[Install]
WantedBy=multi-user.target
```

#### 步骤 6: 启动服务

```bash
# 重载 Systemd
sudo systemctl daemon-reload

# 启动服务
sudo systemctl start remote-relay

# 开机自启
sudo systemctl enable remote-relay

# 查看状态
sudo systemctl status remote-relay
```

## 服务管理

### 常用命令

```bash
# 启动服务
sudo systemctl start remote-relay

# 停止服务
sudo systemctl stop remote-relay

# 重启服务
sudo systemctl restart remote-relay

# 查看状态
sudo systemctl status remote-relay

# 查看实时日志
sudo journalctl -u remote-relay -f

# 查看最近 100 行日志
sudo journalctl -u remote-relay -n 100

# 查看今天的日志
sudo journalctl -u remote-relay --since today

# 开机自启
sudo systemctl enable remote-relay

# 禁用自启
sudo systemctl disable remote-relay
```

### 手动运行（测试用）

```bash
# 前台运行
cd /opt/remote-relay
sudo ./RemoteControl.RelayServer

# 后台运行
nohup sudo ./RemoteControl.RelayServer > /var/log/remote-relay.log 2>&1 &

# 查看进程
ps aux | grep RemoteControl

# 停止后台进程
pkill -f RemoteControl.RelayServer
```

## Docker 部署

### 创建 Dockerfile

```dockerfile
FROM mcr.microsoft.com/dotnet/runtime:6.0

WORKDIR /app

# 复制已编译的文件
COPY publish-linux/RelayServer/ .

# 开放端口
EXPOSE 16888

# 启动程序
ENTRYPOINT ["./RemoteControl.RelayServer"]
```

### 构建和运行

```bash
# 构建镜像
docker build -t remote-relay:latest .

# 运行容器
docker run -d \
    --name relay-server \
    -p 16888:16888 \
    --restart always \
    remote-relay:latest

# 查看日志
docker logs -f relay-server

# 停止容器
docker stop relay-server

# 启动容器
docker start relay-server

# 删除容器
docker rm -f relay-server
```

### 使用 Docker Compose

创建 `docker-compose.yml`:

```yaml
version: '3.8'

services:
  relay-server:
    image: remote-relay:latest
    container_name: relay-server
    ports:
      - "16888:16888"
    restart: always
    logging:
      driver: "json-file"
      options:
        max-size: "10m"
        max-file: "3"
```

运行：
```bash
docker-compose up -d
docker-compose logs -f
docker-compose down
```

## 监控和维护

### 查看服务状态

```bash
# 系统状态
sudo systemctl status remote-relay

# 详细信息
sudo systemctl show remote-relay

# 检查端口监听
sudo netstat -tlnp | grep 16888
# 或
sudo ss -tlnp | grep 16888
```

### 查看日志

```bash
# 实时日志
sudo journalctl -u remote-relay -f

# 错误日志
sudo journalctl -u remote-relay -p err

# 导出日志
sudo journalctl -u remote-relay --since "2024-01-01" > relay.log
```

### 性能监控

```bash
# CPU 和内存使用
top -p $(pgrep -f RemoteControl.RelayServer)

# 详细统计
htop -p $(pgrep -f RemoteControl.RelayServer)

# 网络连接数
sudo netstat -an | grep :16888 | wc -l

# 带宽使用
sudo iftop -i eth0
```

### 日志轮转

创建 `/etc/logrotate.d/remote-relay`:

```
/var/log/remote-relay.log {
    daily
    rotate 7
    compress
    delaycompress
    missingok
    notifempty
    create 0640 relay relay
    sharedscripts
    postrotate
        systemctl reload remote-relay > /dev/null 2>&1 || true
    endscript
}
```

## 性能优化

### 系统参数调优

编辑 `/etc/sysctl.conf`:

```bash
# 增加最大文件描述符
fs.file-max = 65535

# TCP 优化
net.core.somaxconn = 1024
net.ipv4.tcp_max_syn_backlog = 2048
net.ipv4.tcp_fin_timeout = 30
net.ipv4.tcp_keepalive_time = 300
net.ipv4.tcp_tw_reuse = 1

# 应用设置
sudo sysctl -p
```

### 进程限制

编辑 `/etc/security/limits.conf`:

```
relay soft nofile 65535
relay hard nofile 65535
```

## 安全加固

### 1. 配置 SSL/TLS (Nginx 反向代理)

安装 Nginx:
```bash
sudo apt-get install nginx certbot python3-certbot-nginx
```

配置文件 `/etc/nginx/sites-available/relay`:
```nginx
upstream relay_backend {
    server 127.0.0.1:16888;
}

server {
    listen 443 ssl http2;
    server_name relay.yourdomain.com;

    ssl_certificate /etc/letsencrypt/live/relay.yourdomain.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/relay.yourdomain.com/privkey.pem;

    location / {
        proxy_pass http://relay_backend;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        
        # 超时设置
        proxy_connect_timeout 7d;
        proxy_send_timeout 7d;
        proxy_read_timeout 7d;
    }
}
```

启用配置:
```bash
sudo ln -s /etc/nginx/sites-available/relay /etc/nginx/sites-enabled/
sudo nginx -t
sudo systemctl restart nginx

# 获取 SSL 证书
sudo certbot --nginx -d relay.yourdomain.com
```

### 2. Fail2Ban 防护

安装:
```bash
sudo apt-get install fail2ban
```

配置 `/etc/fail2ban/jail.local`:
```ini
[relay-server]
enabled = true
port = 16888
logpath = /var/log/remote-relay.log
maxretry = 5
bantime = 3600
findtime = 600
```

### 3. 限流配置

使用 iptables:
```bash
# 限制每 IP 最多 10 个连接
sudo iptables -A INPUT -p tcp --dport 16888 -m connlimit --connlimit-above 10 -j REJECT

# 限制新连接速率
sudo iptables -A INPUT -p tcp --dport 16888 -m state --state NEW -m recent --set
sudo iptables -A INPUT -p tcp --dport 16888 -m state --state NEW -m recent --update --seconds 60 --hitcount 20 -j DROP
```

## 故障排除

### 服务无法启动

```bash
# 查看详细错误
sudo journalctl -u remote-relay -xe

# 检查二进制文件
ls -lah /opt/remote-relay/RemoteControl.RelayServer
file /opt/remote-relay/RemoteControl.RelayServer

# 检查权限
sudo -u relay /opt/remote-relay/RemoteControl.RelayServer
```

### 端口被占用

```bash
# 查看占用端口的进程
sudo lsof -i :16888
sudo netstat -tlnp | grep 16888

# 停止占用的进程
sudo kill -9 <PID>
```

### 无法连接

```bash
# 测试端口
telnet localhost 16888
nc -vz localhost 16888

# 检查防火墙
sudo iptables -L -n | grep 16888
sudo ufw status

# 测试外网连接
telnet your-server-ip 16888
```

### 内存占用高

```bash
# 查看内存使用
ps aux | grep RemoteControl
pmap -x $(pgrep -f RemoteControl)

# 重启服务
sudo systemctl restart remote-relay
```

## 升级和更新

```bash
# 1. 停止服务
sudo systemctl stop remote-relay

# 2. 备份当前版本
sudo cp -r /opt/remote-relay /opt/remote-relay.backup

# 3. 上传新版本
scp relay-server-new.tar.gz root@your-server-ip:/tmp/

# 4. 解压覆盖
cd /opt/remote-relay
sudo tar -xzf /tmp/relay-server-new.tar.gz --strip-components=1

# 5. 设置权限
sudo chown -R relay:relay /opt/remote-relay
sudo chmod +x /opt/remote-relay/RemoteControl.RelayServer

# 6. 启动服务
sudo systemctl start remote-relay

# 7. 验证
sudo systemctl status remote-relay
```

## 卸载

```bash
# 停止并禁用服务
sudo systemctl stop remote-relay
sudo systemctl disable remote-relay

# 删除服务文件
sudo rm /etc/systemd/system/remote-relay.service
sudo systemctl daemon-reload

# 删除程序文件
sudo rm -rf /opt/remote-relay

# 删除用户
sudo userdel relay

# 关闭防火墙端口
sudo ufw delete allow 16888/tcp
```

## 常见云服务商配置

### 阿里云

1. 购买轻量应用服务器
2. 安全组开放 16888 端口
3. 使用 deploy-linux.sh 部署

### 腾讯云

1. 购买轻量应用服务器
2. 防火墙开放 16888 端口
3. 使用 deploy-linux.sh 部署

### AWS EC2

1. 启动 Ubuntu 实例
2. Security Group 入站规则添加 TCP 16888
3. 使用 deploy-linux.sh 部署

### DigitalOcean

1. 创建 Droplet (Ubuntu)
2. Cloud Firewalls 开放 16888
3. 使用 deploy-linux.sh 部署

---

**推荐配置**: Ubuntu 22.04 + Systemd + Nginx (SSL)
