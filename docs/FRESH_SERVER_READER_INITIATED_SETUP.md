# Fresh Ubuntu Server Setup for Reader-Initiated TCP

This guide starts with a new Ubuntu 24.04 x86-64 server and ends with the middleware waiting for one RFID reader on TCP port 1969. It supports either:

- a temporary public VPS, restricted to the test site's public IPv4 address; or
- a permanent warehouse server, restricted to the reader's private IPv4 address.

The RFID vendor protocol is unauthenticated and unencrypted. Never publish it without a source-IP restriction. Production must use the warehouse LAN/VLAN and must not have an internet port-forward.

## 1. Record the required values

Prepare these values before logging in:

| Placeholder | Meaning |
|---|---|
| `<SERVER_IP>` | Public VPS IP for testing, or fixed warehouse server LAN IP for production |
| `<ALLOWED_READER_SOURCE>` | Test site's public egress IP for a VPS, or reader's fixed LAN IP for production |
| `<BACKEND_URL>` | HTTPS URL of the manifest backend |
| `<MIDDLEWARE_ID>` | Identity registered for this middleware instance |
| `<API_TOKEN>` | Token issued for that middleware identity |
| `<DATABASE_PASSWORD>` | New strong password used only by the local PostgreSQL container |

For a VPS, `<ALLOWED_READER_SOURCE>` is the test site's public internet address, not the reader's `192.168.x.x` address.

## 2. Create and connect to the server

Provision Ubuntu Server 24.04 LTS on x86-64/amd64. Assign a stable address:

- VPS: reserve the provider's public IPv4 address.
- Production: configure a static LAN address or DHCP reservation.

Allow SSH only from approved administrator addresses. Connect using a non-root account with `sudo` access:

```bash
ssh <ADMIN_USER>@<SERVER_IP>
```

Confirm the OS and CPU architecture:

```bash
cat /etc/os-release
uname -m
```

`VERSION_ID` must be `24.04` and `uname -m` must report `x86_64`.

## 3. Update Ubuntu and install base tools

```bash
sudo apt update
sudo apt full-upgrade -y
sudo apt install -y ca-certificates curl git jq iptables
sudo reboot
```

Reconnect after the reboot.

Optionally set the local timezone:

```bash
sudo timedatectl set-timezone Asia/Jakarta
timedatectl
```

## 4. Install Docker Engine and Compose

Use Docker's official Ubuntu package repository:

```bash
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg \
  -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc
```

Add the repository:

```bash
sudo tee /etc/apt/sources.list.d/docker.sources >/dev/null <<EOF
Types: deb
URIs: https://download.docker.com/linux/ubuntu
Suites: $(. /etc/os-release && echo "${UBUNTU_CODENAME:-$VERSION_CODENAME}")
Components: stable
Architectures: $(dpkg --print-architecture)
Signed-By: /etc/apt/keyrings/docker.asc
EOF
```

Install and verify Docker:

```bash
sudo apt update
sudo apt install -y docker-ce docker-ce-cli containerd.io \
  docker-buildx-plugin docker-compose-plugin
sudo systemctl enable --now docker
sudo docker run --rm hello-world
sudo docker version
sudo docker compose version
```

The remaining commands use `sudo docker`. This avoids granting the deployment account permanent root-equivalent access through the `docker` group. If your organization deliberately uses that group, follow Docker's post-installation documentation and reconnect before continuing.

## 5. Download the middleware

Use `/opt` as the deployment location:

```bash
cd /opt
sudo git clone https://github.com/ichaspg/rrligthing-rfid-middleware.git
sudo chown -R "$USER":"$USER" /opt/rrligthing-rfid-middleware
cd /opt/rrligthing-rfid-middleware
```

Confirm that the listener override exists:

```bash
git status --short
test -f docker-compose.listener.yml
```

If this deployment must use an approved release tag or commit, check out that exact revision now. Do not run an unreviewed development branch in production.

## 6. Create the production environment

```bash
cp .env.production.example .env.production
chmod 600 .env.production
```

Edit it:

```bash
nano .env.production
```

Set these values:

```dotenv
RFID_MIDDLEWARE_ID=<MIDDLEWARE_ID>
RFID_API_TOKEN=<API_TOKEN>
RFID_SERVER_BASE_URL=<BACKEND_URL>
RFID_DATABASE_USER=rfid
RFID_DATABASE_PASSWORD=<DATABASE_PASSWORD>

RFID_READER_MODE=Vendor
RFID_READER_TRANSPORT=TcpListener
RFID_READER_PORT=1969
RFID_READER_BIND_ADDRESS=<SERVER_IP>
RFID_READER_ALARM_MODE=BuzzerAndDigitalOutput1
```

For a VPS with a provider-assigned address that is not directly present on the VM, use `RFID_READER_BIND_ADDRESS=0.0.0.0`. For a warehouse server, use its exact LAN address. `RFID_READER_ADDRESS` is ignored in listener mode.

Check that no placeholders remain:

```bash
grep -n "replace-me\|<.*>" .env.production
```

This command should produce no output. Never commit `.env.production`.

## 7. Configure the network restriction

Create the upstream rule before publishing the listener:

- VPS: in the provider's cloud firewall/security group, allow inbound TCP 1969 only from `<ALLOWED_READER_SOURCE>/32`.
- Production: in the warehouse router/VLAN ACL, allow TCP 1969 only from `<ALLOWED_READER_SOURCE>` to `<SERVER_IP>`. Do not add a public NAT or port-forward.

Docker-published ports are processed before normal UFW input rules. Therefore, a UFW rule by itself is not sufficient. Keep UFW for SSH and other host services, but enforce the reader restriction upstream and in Docker's `DOCKER-USER` chain.

Create a root-owned configuration file. Replace the example address before running it:

```bash
sudo tee /etc/rfid-listener-firewall.conf >/dev/null <<'EOF'
ALLOWED_SOURCE=203.0.113.10/32
LISTENER_PORT=1969
EOF
sudo chmod 600 /etc/rfid-listener-firewall.conf
sudo nano /etc/rfid-listener-firewall.conf
```

Create the idempotent firewall helper:

```bash
sudo tee /usr/local/sbin/rfid-listener-firewall >/dev/null <<'EOF'
#!/bin/sh
set -eu
. /etc/rfid-listener-firewall.conf

/usr/sbin/iptables -nL DOCKER-USER >/dev/null
/usr/sbin/iptables -N RFID-LISTENER 2>/dev/null || true
/usr/sbin/iptables -F RFID-LISTENER
/usr/sbin/iptables -A RFID-LISTENER -p tcp -s "$ALLOWED_SOURCE" \
  -m conntrack --ctorigdstport "$LISTENER_PORT" -j RETURN
/usr/sbin/iptables -A RFID-LISTENER -p tcp \
  -m conntrack --ctorigdstport "$LISTENER_PORT" -j DROP
/usr/sbin/iptables -A RFID-LISTENER -j RETURN
/usr/sbin/iptables -C DOCKER-USER -j RFID-LISTENER 2>/dev/null || \
  /usr/sbin/iptables -I DOCKER-USER 1 -j RFID-LISTENER
EOF
sudo chmod 750 /usr/local/sbin/rfid-listener-firewall
sudo chown root:root /usr/local/sbin/rfid-listener-firewall
```

Run it automatically after Docker starts and again whenever Docker is restarted:

```bash
sudo tee /etc/systemd/system/rfid-listener-firewall.service >/dev/null <<'EOF'
[Unit]
Description=Restrict the RFID Docker listener to its approved source
Requires=docker.service
After=docker.service network-online.target
PartOf=docker.service

[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=/usr/local/sbin/rfid-listener-firewall

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable --now rfid-listener-firewall.service
sudo systemctl status rfid-listener-firewall.service --no-pager
sudo iptables -S RFID-LISTENER
```

The resulting custom chain must return traffic from the approved source and drop other TCP connections whose original destination port is 1969. If the site already manages firewall rules centrally, implement the equivalent policy there instead of installing this unit.

## 8. Validate the deployment configuration

```bash
sudo docker compose -p rfid-prod \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  config
```

Before continuing, confirm the rendered configuration shows:

- reader mode `Vendor`;
- transport `TcpListener`;
- listener port `1969`;
- only the intended host bind address;
- no mock backend or development override.

## 9. Build and start the service

```bash
sudo docker compose -p rfid-prod \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  up -d --build
```

Inspect the containers and logs:

```bash
sudo docker compose -p rfid-prod \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  ps

sudo docker compose -p rfid-prod \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  logs --tail=200 middleware
```

Until the reader connects, the middleware remains running but reader health is critical. This is expected.

## 10. Commission the reader

The reader needs two separate configurations:

1. its own Ethernet/Wi-Fi address, subnet mask, and gateway;
2. the remote TCP client target, which must be `<SERVER_IP>:1969`.

Commission these over the temporary BAFO serial connection. Preview every change first and repeat with `--apply` only after review. The apply command creates a JSON backup, verifies exact read-back, and then reboots the reader.

Follow the guide matching the deployment:

- [Public VPS reader setup](VPS_READER_INITIATED_SETUP.md#5-attach-the-reader-to-the-commissioning-laptop)
- [Local production reader setup](LOCAL_PRODUCTION_READER_INITIATED_SETUP.md#5-prepare-serial-commissioning)

Wi-Fi SSID and password are configured with the vendor tool, not this middleware. After commissioning, remove the serial cable; the permanent connection is Ethernet or vendor-configured Wi-Fi.

## 11. Verify operation

```bash
sudo docker compose -p rfid-prod \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  exec middleware /app/rfid-middleware status --config /app/appsettings.json

sudo docker compose -p rfid-prod \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  exec middleware /app/rfid-middleware reader test --config /app/appsettings.json

sudo docker compose -p rfid-prod \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  exec middleware /app/rfid-middleware reader watch \
  --duration 10 --config /app/appsettings.json
```

The installation is ready only after:

- the reader is connected and automatic inventory is running;
- approved tags pass silently;
- an unauthorized tag activates the buzzer and digital output 1;
- reader and middleware restarts both recover automatically;
- an unapproved source cannot connect to TCP 1969;
- the firewall restriction still works after a full server reboot.

Physical VM-6x TCP-client/VUP compatibility and alarm-output verification are mandatory. Stop the release and ask the vendor if that compatibility test fails.

## 12. Reboot acceptance test

```bash
sudo reboot
```

After reconnecting:

```bash
cd /opt/rrligthing-rfid-middleware
sudo systemctl is-active docker
sudo iptables -S DOCKER-USER
sudo docker compose -p rfid-prod \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  ps
```

Confirm the source-IP drop rule is present, the containers started through their restart policies, and the reader reconnected.

## 13. Routine commands

Logs:

```bash
sudo docker compose -p rfid-prod \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  logs -f middleware
```

Restart middleware:

```bash
sudo docker compose -p rfid-prod \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  restart middleware
```

Stop without deleting data:

```bash
sudo docker compose -p rfid-prod \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  down
```

Never add `--volumes` to a routine stop.

## 14. Controlled application update

Schedule a maintenance window and back up the server before updating. Then:

```bash
cd /opt/rrligthing-rfid-middleware
git status --short
git fetch --all --prune
git pull --ff-only
sudo docker compose -p rfid-prod \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  up -d --build
```

Repeat the status, reader, alarm, reconnection, and firewall checks after every update.

## References

- [Docker Engine installation on Ubuntu](https://docs.docker.com/engine/install/ubuntu/)
- [Docker Linux post-installation](https://docs.docker.com/engine/install/linux-postinstall/)
- [Docker packet filtering and firewall behavior](https://docs.docker.com/engine/network/packet-filtering-firewalls/)
- [Docker iptables and `DOCKER-USER`](https://docs.docker.com/engine/network/firewall-iptables/)
