# Reader-Initiated TCP Setup on a Local Production Server

Use this guide for the permanent warehouse deployment. The reader and middleware server communicate on the local network; only the middleware communicates with the backend.

```text
RFID reader -- local TCP 1969 --> warehouse Linux server -- HTTPS/Twingate --> backend
```

Do not create an internet port-forward for TCP 1969. The vendor reader protocol is unauthenticated and unencrypted and must remain private to the warehouse LAN or VLAN.

## 1. Plan the production addresses

Reserve or assign these addresses before deployment:

| Value | Example | Purpose |
|---|---|---|
| `<SERVER_LAN_IP>` | `192.168.10.10` | Fixed address of the Linux middleware server |
| `<READER_LAN_IP>` | `192.168.10.20` | Fixed address of the RFID reader |
| `<SUBNET_MASK>` | `255.255.255.0` | Reader's local subnet mask |
| `<LAN_GATEWAY>` | `192.168.10.1` | Reader's local gateway |

The server address must not change. Use a static address or a DHCP reservation. Confirm that the reader can route to the server and that the server can reach the backend before commissioning.

You also need:

- Ubuntu 24.04 x86-64 with Docker Engine and Docker Compose;
- the backend URL, middleware ID, API token, and a new database password;
- a BAFO USB-to-serial adapter for commissioning;
- Ethernet or vendor-configured Wi-Fi on the reader.

One middleware instance supports one active reader. Run a separately configured middleware instance and port for each additional reader.

## 2. Prepare the local Linux server

Clone or copy the repository, enter its directory, and create the environment file:

```bash
git clone <REPOSITORY_URL> rrligthing-rfid-middleware
cd rrligthing-rfid-middleware
cp .env.production.example .env.production
chmod 600 .env.production
```

Edit `.env.production`:

```dotenv
RFID_MIDDLEWARE_ID=<MIDDLEWARE_ID>
RFID_API_TOKEN=<API_TOKEN>
RFID_SERVER_BASE_URL=https://<BACKEND_HOST>
RFID_DATABASE_USER=rfid
RFID_DATABASE_PASSWORD=<STRONG_DATABASE_PASSWORD>

RFID_READER_MODE=Vendor
RFID_READER_TRANSPORT=TcpListener
RFID_READER_PORT=1969
RFID_READER_BIND_ADDRESS=<SERVER_LAN_IP>
RFID_READER_ALARM_MODE=BuzzerAndDigitalOutput1
```

Binding to `<SERVER_LAN_IP>` prevents Docker from publishing the reader port on every server interface. The address must already exist on the host. `RFID_READER_ADDRESS` is ignored in listener mode.

## 3. Restrict the local firewall

First add a warehouse router/VLAN ACL allowing the reader's fixed IP to reach only `<SERVER_LAN_IP>:1969`. Do not create an internet port-forward.

Docker-published ports can bypass normal UFW input rules. After Docker has started, add the same restriction to Docker's `DOCKER-USER` chain:

```bash
sudo iptables -I DOCKER-USER 1 \
  -p tcp -m conntrack --ctorigdstport 1969 \
  ! -s <READER_LAN_IP>/32 -j DROP
sudo iptables -S DOCKER-USER
```

Persist this exact rule with the site's approved firewall/configuration-management system so it is applied after Docker starts. On a new server, use the [persistent `DOCKER-USER` service](FRESH_SERVER_READER_INITIATED_SETUP.md#7-configure-the-network-restriction). Verify it after every server or Docker reboot. UFW can still protect SSH and other host-native services, but it is not sufficient by itself for this published container port.

## 4. Validate and start the production service

Validate the complete Compose configuration:

```bash
docker compose -p rfid-prod \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  config
```

Start it:

```bash
docker compose -p rfid-prod \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  up -d --build
```

Monitor startup:

```bash
docker compose -p rfid-prod \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  logs -f middleware
```

Critical reader health is expected until the reader makes its first connection. The middleware must keep running while it waits.

## 5. Prepare serial commissioning

Stop any other software using the BAFO adapter. On the Ubuntu server or an Ubuntu/WSL2 commissioning laptop, identify its stable path:

```bash
ls -l /dev/serial/by-id/ /dev/ttyUSB* 2>/dev/null
export RFID_SERIAL_DEVICE=/dev/serial/by-id/<BAFO_DEVICE_ID>
export RFID_DIALOUT_GID="$(getent group dialout | cut -d: -f3)"
```

For a WSL2 laptop, attach the adapter first from elevated Windows PowerShell:

```powershell
usbipd list
usbipd bind --busid <BUSID>
usbipd attach --wsl --busid <BUSID>
```

Run the remaining commands inside Ubuntu/WSL from a checkout of this repository. The serial Compose override temporarily selects serial mode for the commissioning command; it does not change the server's saved listener configuration.

## 6. Configure the reader's local network

The reader's own IP configuration is different from its remote middleware target. Configure its local network first.

Preview:

```bash
docker compose -p rfid-commission \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.serial.yml \
  run --rm --no-deps middleware \
  reader network configure \
  --ip <READER_LAN_IP> \
  --mask <SUBNET_MASK> \
  --gateway <LAN_GATEWAY> \
  --port 1969 \
  --config /app/appsettings.json
```

Review the current and proposed values, then repeat the exact command with `--apply`:

```bash
docker compose -p rfid-commission \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.serial.yml \
  run --rm --no-deps middleware \
  reader network configure \
  --ip <READER_LAN_IP> \
  --mask <SUBNET_MASK> \
  --gateway <LAN_GATEWAY> \
  --port 1969 \
  --apply \
  --config /app/appsettings.json
```

Add `--dns <DNS_IP>` only when the reader requires it. The target below is a numeric local IP, so DNS is normally unnecessary. Configure Wi-Fi credentials with the vendor tool if Ethernet is not used.

The apply operation reboots the reader. Wait for the reader to finish booting and confirm that the serial device has reappeared before continuing.

## 7. Point the reader at the local server

Show the current target:

```bash
docker compose -p rfid-commission \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.serial.yml \
  run --rm --no-deps middleware \
  reader client show --json --config /app/appsettings.json
```

Preview the local production target:

```bash
docker compose -p rfid-commission \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.serial.yml \
  run --rm --no-deps middleware \
  reader client configure \
  --server-ip <SERVER_LAN_IP> \
  --port 1969 \
  --config /app/appsettings.json
```

After confirming the preview, repeat with `--apply`:

```bash
docker compose -p rfid-commission \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.serial.yml \
  run --rm --no-deps middleware \
  reader client configure \
  --server-ip <SERVER_LAN_IP> \
  --port 1969 \
  --apply \
  --config /app/appsettings.json
```

The apply operation preserves UDP report settings, writes an atomic JSON backup, verifies the exact TCP target by reading it back, and reboots the reader only after successful verification. Backups are in the `rfid-commission` commissioning volume under `/var/lib/rfid-middleware/commissioning`.

Wait for the reboot to complete, then remove the temporary serial cable. The permanent reader-to-server path is Ethernet or vendor-configured Wi-Fi, not RS-232.

## 8. Production acceptance checks

Check the service and recent reader activity:

```bash
docker compose -p rfid-prod \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  ps

docker compose -p rfid-prod \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  logs --tail=200 middleware

docker compose -p rfid-prod \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  exec middleware /app/rfid-middleware status --config /app/appsettings.json

docker compose -p rfid-prod \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  exec middleware /app/rfid-middleware reader test --config /app/appsettings.json

docker compose -p rfid-prod \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  exec middleware /app/rfid-middleware reader watch --duration 10 --config /app/appsettings.json
```

Complete all acceptance cases:

- reader health is healthy, it is connected, and inventory is running;
- approved tag scans pass silently and are stored in the local audit;
- an unauthorized tag activates the buzzer and digital output 1;
- disconnecting and reconnecting the reader cable restores inventory automatically;
- a reader power cycle reconnects automatically;
- a middleware or Linux server restart restores the listener and reader connection;
- a backend outage does not stop validation against a still-valid cached manifest;
- TCP 1969 is unreachable from outside the warehouse LAN/VLAN;
- the router has no public port-forward for TCP 1969.

Physical VM-6x TCP-client/VUP persistence, inventory, buzzer, and digital output 1 are release gates. A failure requires vendor clarification; do not substitute an invented wire protocol.

## 9. Routine operation and rollback

Use the status and log commands above for routine checks. When the reader disconnects, health becomes critical but the service continues listening. It should recover without an operator restart when the same reader reconnects.

To stop the stack while preserving the database:

```bash
docker compose -p rfid-prod \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.listener.yml \
  down
```

Do not use `--volumes` during a normal stop.

To disable reader-initiated TCP, reconnect serial and preview:

```bash
docker compose -p rfid-commission \
  --env-file .env.production \
  -f docker-compose.yml \
  -f docker-compose.serial.yml \
  run --rm --no-deps middleware \
  reader client disable --config /app/appsettings.json
```

Repeat with `--apply` only after reviewing the preview. To restore an earlier target instead, inspect the commissioning JSON backup and run `reader client configure` with that exact prior IP and port.
