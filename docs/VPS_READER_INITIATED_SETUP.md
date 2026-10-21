# Reader-Initiated TCP Setup on a Public VPS

Use this guide for temporary testing where the RFID reader connects outbound through the internet to middleware running on a VPS.

```text
reader at test site -- TCP 1969 --> public VPS --> middleware
                                             \\-- HTTPS/Twingate --> backend
```

The vendor reader protocol is not encrypted and has no application authentication. This setup is acceptable only when both the cloud firewall and the VPS firewall allow TCP port 1969 exclusively from the test site's stable public IP. Never expose port 1969 to `0.0.0.0/0`.

## 1. Before you start

You need:

- an Ubuntu 24.04 x86-64 VPS with Docker Engine and Docker Compose;
- the VPS public IPv4 address, referred to below as `<VPS_PUBLIC_IP>`;
- the test site's public internet IPv4 address, referred to as `<TEST_SITE_PUBLIC_IP>`;
- the backend URL, middleware ID, API token, and a new database password;
- a laptop with an Ubuntu, WSL2, or Linux VM environment for serial commissioning;
- the BAFO USB-to-serial adapter attached to that Linux environment;
- Ethernet or vendor-configured Wi-Fi on the reader.

`<TEST_SITE_PUBLIC_IP>` is the public egress address reported by the site's router or ISP. It is not the reader's private address such as `192.168.x.x`.

## 2. Prepare the VPS

Clone or copy the repository to the VPS, enter its directory, and create the production environment file:

```bash
git clone <REPOSITORY_URL> rrligthing-rfid-middleware
cd rrligthing-rfid-middleware
cp .env.production.example .env.production
chmod 600 .env.production
```

Edit `.env.production` and set at least these values:

```dotenv
RFID_MIDDLEWARE_ID=<MIDDLEWARE_ID>
RFID_API_TOKEN=<API_TOKEN>
RFID_SERVER_BASE_URL=https://<BACKEND_HOST>
RFID_DATABASE_USER=rfid
RFID_DATABASE_PASSWORD=<STRONG_DATABASE_PASSWORD>

RFID_READER_MODE=Vendor
RFID_READER_TRANSPORT=TcpListener
RFID_READER_PORT=1969
RFID_READER_BIND_ADDRESS=0.0.0.0
RFID_READER_ALARM_MODE=BuzzerAndDigitalOutput1
```

`RFID_READER_ADDRESS` is not used in `TcpListener` mode. The reader initiates the connection to the VPS.

## 3. Restrict the firewall

First make sure the VPS's existing SSH or management rule will remain available. In the VPS provider's cloud firewall/security group, add:

| Direction | Protocol | Port | Source |
|---|---|---:|---|
| Inbound | TCP | 1969 | `<TEST_SITE_PUBLIC_IP>/32` |

Do not add a rule for all internet addresses.

Docker-published ports can bypass normal UFW input rules. Do not treat UFW as the enforcement point for TCP 1969. Keep the provider cloud firewall rule above, then add a host rule in Docker's `DOCKER-USER` chain after Docker has started:

```bash
sudo iptables -I DOCKER-USER 1 \
  -p tcp -m conntrack --ctorigdstport 1969 \
  ! -s <TEST_SITE_PUBLIC_IP>/32 -j DROP
sudo iptables -S DOCKER-USER
```

Persist this exact rule with the site's approved firewall/configuration-management system so it is applied after Docker starts. On a new server, use the [persistent `DOCKER-USER` service](FRESH_SERVER_READER_INITIATED_SETUP.md#7-configure-the-network-restriction). Verify it after every server or Docker reboot. UFW may still protect SSH and other host-native services. Do not enable or reset UFW during a remote session unless SSH access has already been allowed and the provider's recovery console is available.

## 4. Validate and start the middleware

Render the combined Compose configuration before starting it:

```bash
docker compose -p rfid-vps \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  config
```

Start the database and middleware:

```bash
docker compose -p rfid-vps \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  up -d --build
```

Follow the middleware log:

```bash
docker compose -p rfid-vps \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  logs -f middleware
```

Before the reader connects, critical reader health and a waiting-for-reader message are expected. The service must remain running.

## 5. Attach the reader to the commissioning laptop

On native Ubuntu, identify the stable serial device:

```bash
ls -l /dev/serial/by-id/ /dev/ttyUSB* 2>/dev/null
export RFID_SERIAL_DEVICE=/dev/serial/by-id/<BAFO_DEVICE_ID>
export RFID_DIALOUT_GID="$(getent group dialout | cut -d: -f3)"
```

For WSL2, first attach the adapter from an elevated Windows PowerShell window:

```powershell
usbipd list
usbipd bind --busid <BUSID>
usbipd attach --wsl --busid <BUSID>
```

Then run the Ubuntu commands above inside WSL. Keep the adapter connected until all apply operations and reboots are complete.

Use a local checkout of this repository on the commissioning laptop. Create `.env.production` from the example if needed. The backend and database fields must be non-empty for Compose validation, but the `--no-deps` commissioning commands do not start or contact those services.

## 6. Configure the reader's own network

This step controls how the reader joins the test-site network. It is separate from the remote middleware target.

Preview the change first:

```bash
docker compose -p rfid-commission \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.serial.yml \
  run --rm --no-deps middleware \
  reader network configure \
  --ip <READER_PRIVATE_IP> \
  --mask <SUBNET_MASK> \
  --gateway <SITE_GATEWAY> \
  --port 1969 \
  --config /app/appsettings.json
```

Review the displayed current and proposed values. Repeat the same command with `--apply` only when they are correct:

```bash
docker compose -p rfid-commission \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.serial.yml \
  run --rm --no-deps middleware \
  reader network configure \
  --ip <READER_PRIVATE_IP> \
  --mask <SUBNET_MASK> \
  --gateway <SITE_GATEWAY> \
  --port 1969 \
  --apply \
  --config /app/appsettings.json
```

The gateway must allow the reader to reach the internet. Add `--dns <DNS_IP>` if the reader/site requires DNS; a numeric VPS target does not normally require it. Wi-Fi SSID and password setup is outside this middleware and must be performed with the vendor tool.

The apply operation reboots the reader. Wait for the reader to finish booting and confirm that the serial device has reappeared before continuing.

## 7. Point the reader at the VPS

Read the current client target:

```bash
docker compose -p rfid-commission \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.serial.yml \
  run --rm --no-deps middleware \
  reader client show --json --config /app/appsettings.json
```

Preview the new target:

```bash
docker compose -p rfid-commission \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.serial.yml \
  run --rm --no-deps middleware \
  reader client configure \
  --server-ip <VPS_PUBLIC_IP> \
  --port 1969 \
  --config /app/appsettings.json
```

If the preview is correct, repeat it with `--apply`:

```bash
docker compose -p rfid-commission \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.serial.yml \
  run --rm --no-deps middleware \
  reader client configure \
  --server-ip <VPS_PUBLIC_IP> \
  --port 1969 \
  --apply \
  --config /app/appsettings.json
```

Apply preserves the reader's UDP report settings, creates an atomic JSON backup, reads back the TCP target exactly, and reboots only after verification succeeds. Backups are stored in the `rfid-commission` commissioning volume under `/var/lib/rfid-middleware/commissioning`.

Wait for that reboot to finish, then disconnect the temporary serial cable. The reader should make its outbound connection to the VPS without the serial cable attached.

## 8. Verify the complete path

On the VPS, inspect service state and recent logs:

```bash
docker compose -p rfid-vps \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  ps

docker compose -p rfid-vps \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  logs --tail=200 middleware
```

Run the operational checks:

```bash
docker compose -p rfid-vps \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  exec middleware /app/rfid-middleware status --config /app/appsettings.json

docker compose -p rfid-vps \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  exec middleware /app/rfid-middleware reader test --config /app/appsettings.json

docker compose -p rfid-vps \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  exec middleware /app/rfid-middleware reader watch --duration 10 --config /app/appsettings.json
```

Accept the test only when all of the following are true:

- the reader is connected and inventory is running;
- tag scans appear in the local audit;
- an authorized tag passes silently;
- an unauthorized tag operates the buzzer and digital output 1;
- after a reader power cycle, the middleware accepts it again and inventory restarts;
- after a middleware restart, the reader reconnects;
- a connection attempt from a source outside the allowlist is denied.

Physical VM-6x TCP-client/VUP compatibility, buzzer, and relay verification are release gates. If any fail, stop and obtain vendor clarification rather than inventing a replacement protocol.

## 9. End the temporary VPS test

If the reader will not immediately be moved to the production target, disable its TCP client over serial. Preview first, then apply:

```bash
docker compose -p rfid-commission \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.serial.yml \
  run --rm --no-deps middleware \
  reader client disable --config /app/appsettings.json
```

Repeat with `--apply` after review. Then remove the TCP 1969 rule from the cloud firewall and remove the matching managed `DOCKER-USER` rule from the VPS. List the exact host rule first with `sudo iptables -S DOCKER-USER`; do not delete by an assumed rule number.

Stop the VPS stack without deleting its persistent data:

```bash
docker compose -p rfid-vps \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  down
```

Do not add `--volumes` unless the database is intentionally being destroyed.
