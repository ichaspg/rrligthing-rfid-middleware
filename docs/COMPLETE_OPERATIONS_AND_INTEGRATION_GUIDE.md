# RFID Middleware Complete Operations and Integration Guide

This is the single handover document for developers, Ubuntu/server administrators, network engineers, Android/backend developers, and the RFID hardware team. It covers the system flow, installation, all supported command-line operations, Docker and systemd deployment, BAFO serial commissioning, permanent LAN operation, alarm wiring tests, manifest/mobile behavior, acceptance tests, and troubleshooting.

The supported production platform is Ubuntu 24.04 x86-64. The application targets .NET 8 LTS. Repository builds use the .NET 10 SDK because the solution uses the `.slnx` format.

## 1. System purpose and responsibility

The middleware is a local authorization gate. Android and the backend decide which EPCs are authorized. The middleware downloads that decision, reads tags from the RFID reader, checks them locally, stores the audit locally, and raises an alarm only when authorization cannot be established.

```text
Android application
  | authorize / revoke / complete
  v
Production backend
  | immutable ACTIVE or COMPLETED manifest versions over HTTPS
  v
RFID middleware on Ubuntu
  | local authorization and local PostgreSQL audit
  | outbound TCP connection to reader
  v
RFID reader at a fixed private LAN address, normally TCP 1969
  |-- authorized EPC: no alarm
  `-- unauthorized/invalid/no valid manifest: configured alarm
```

The middleware never uploads reader observations, EPC counts, RSSI, antenna data, departure flags, or return reconciliation results. Those observations remain in the local `rfid_edge` PostgreSQL database.

Android and the backend own:

- initial batch authorization;
- departure and return workflow;
- additional-tag registration;
- missing-tag reconciliation;
- tag revocation;
- explicit batch completion.

The middleware owns:

- polling the current manifest every five seconds;
- retaining the last valid unexpired manifest during backend outages;
- validating every reader observation immediately;
- alarming when a tag cannot be authorized;
- local scan and decision audit;
- reader, database, manifest, and alarm health reporting.

## 2. Authorization behavior

An observation is silent only when the EPC is valid, a current unexpired `ACTIVE` manifest exists, and that exact normalized EPC is listed. The alarm is triggered for:

- an EPC absent from the active manifest;
- malformed EPC data;
- no locally active manifest;
- an expired manifest;
- any scan after a batch has been completed, until another active manifest is assigned.

Repeated unauthorized observations use a three-second per-EPC alarm cooldown. Technical faults such as database failure, synchronization failure, queue overflow, or reader disconnection do not directly pulse the hardware alarm. They make health critical and appear in structured logs and metrics. If a technical fault makes authorization impossible, the next observed tag still fails closed and alarms.

Authorization state survives restart:

| State | Meaning | Scan behavior |
|---|---|---|
| `Uninitialized` | No manifest has ever been applied | Every observation alarms; health is critical |
| `Active` | A valid manifest version is active | Listed EPCs are silent; all others alarm |
| `Completed` | A newer completion directive cleared the manifest | Every observation alarms; intentional idle state is healthy |

Manifest versions must increase. Equal or older versions are rejected. Completion is persisted so restarting cannot reactivate an older cached version.

## 3. Reader connection design

### Temporary commissioning connection

Use the BAFO USB-to-RS-232 adapter only while standing near the reader for initial network setup or recovery:

```text
Ubuntu host or VM -- USB/BAFO RS-232 --> reader
```

Serial commissioning uses 115200 baud and reader address `0`. It reads and writes the reader's own IPv4 configuration. It does not save the middleware server address in the reader.

### Permanent operating connection

Normal operation uses Ethernet:

```text
Ubuntu middleware -- private warehouse LAN TCP 1969 --> fixed reader IP
Ubuntu middleware -- HTTPS/Twingate --> production backend
```

The middleware initiates the reader connection. The Ubuntu server may therefore use DHCP, although a reserved server address is operationally convenient. The reader needs a fixed address or DHCP reservation. Do not publish reader port 1969 to the internet, expose it through Docker, or route it through Twingate unless the network design explicitly requires and secures that path.

Keep each copper Ethernet segment at or below 100 metres. Use an intermediate switch or fiber/media converters for longer distances. Do not create a long permanent RS-232 cable run.

## 4. Hardware alarm modes

Supported values are:

| Value | Action for an unauthorized observation |
|---|---|
| `Buzzer` | Pulse only the reader's built-in buzzer |
| `DigitalOutput1` | Pulse reader digital output 1 |
| `BuzzerAndDigitalOutput1` | Attempt the buzzer and output 1 independently |

`BuzzerAndDigitalOutput1` is the hardware setup default. Digital output 0 is intentionally unused because the tested reader reports it as unsupported. If the buzzer or output 1 fails, the middleware still attempts the other channel and continues processing and auditing scans. The failed channel leaves alarm health critical and is reported in `alarm.error` and logs. Shutoff is scheduled before the combined error is raised, preventing a successfully activated channel from being left on.

The default pulse is 1,000 milliseconds. Configure it with `RFID_VendorReader__AlarmPulseMilliseconds` outside Docker or the corresponding JSON setting. Docker currently exposes the mode as `RFID_READER_ALARM_MODE`; pulse duration can be supplied directly as `RFID_VendorReader__AlarmPulseMilliseconds` in an additional Compose environment override.

Hardware safety:

- Confirm output voltage, current rating, active polarity, and whether the output is dry-contact/open-collector with the reader manual or vendor.
- Start with a multimeter or vendor-approved low-current test load.
- Do not connect a siren, lamp, relay coil, or mains circuit directly unless its electrical requirements are within the reader output rating.
- Use an external interposing relay, suppression diode, and separate supply where required.
- Test the buzzer and output 1 individually after combined discovery, then choose the final installed channel.

The previously tested VM-6x V9.4 reader (`SW 1.1.31-GD`) returned SDK error `-3034` for output 0, meaning the relay was unsupported or not configured. Output 0 has been removed from middleware alarm modes. The built-in buzzer and output 1 must be verified on the actual unit.

## 5. Current hardware acceptance EPCs

Authorize exactly these two tags for the current acceptance test:

| Inventory ID | EPC |
|---|---|
| `HW-TAG-001` | `E28069950000400E45B4F53E` |
| `HW-TAG-002` | `E280689400005010BF75955B` |

The nearby EPC `E280689400004010BF78D96A` is intentionally unauthorized and can be used to prove alarm behavior. Do not add it to the acceptance manifest.

## 6. Prerequisites on Ubuntu 24.04

For repository development and direct execution:

```bash
sudo apt update
sudo apt install -y build-essential curl jq git patchelf postgresql postgresql-client \
  valgrind strace lsof ca-certificates
```

Install the .NET 10 SDK and the .NET/ASP.NET Core 8 runtime. Confirm:

```bash
dotnet --list-sdks
dotnet --list-runtimes
uname -m
```

`uname -m` must report `x86_64`. Docker deployment does not require a host .NET installation, but it does require Docker Engine with the Compose plugin:

```bash
docker --version
docker compose version
```

Clone and enter the repository:

```bash
git clone <repository-url> rrligthing-rfid-middleware
cd rrligthing-rfid-middleware
```

Never commit `.env`, `.env.production`, real tokens, or database passwords.

## 7. Complete CLI reference

Installed binary examples use `rfid-middleware`. From source, replace that prefix with:

```bash
dotnet run --project src/RfidMiddleware.Host -c Release --
```

All commands accept `--config <file>` where shown. Commands other than `run`, `version`, `help`, and `reader network ...` contact the already-running process through its Unix control socket.

### Service and health

```text
rfid-middleware help
rfid-middleware version
rfid-middleware run --config <file>
rfid-middleware status [--json] --config <file>
rfid-middleware healthcheck --config <file>
```

- `run` starts the service and blocks until stopped.
- `status` shows overall, reader, alarm, authorization, manifest, scan counter, and latency state.
- `healthcheck` calls `/healthz` through the local control socket and exits non-zero when health is not acceptable. Docker uses this command.
- `--json` is accepted by the status interface for compatibility; output is already formatted JSON.

### Reader observations

```text
rfid-middleware reader test --config <file>
rfid-middleware reader watch [--duration <seconds>] --config <file>
```

- `reader test` shows the running reader adapter health.
- `reader watch` waits and returns recent reads. Duration defaults to 5 seconds and is clamped to 1–300 seconds.

### Reader network commissioning

```text
rfid-middleware reader network show [--json] --config <file>
rfid-middleware reader network configure \
  --ip <reader-ip> \
  --mask <subnet-mask> \
  --gateway <gateway-ip> \
  [--port <1-65535>] \
  [--dns <dns-ip>] \
  [--apply] \
  --config <file>
rfid-middleware reader network verify \
  --address <reader-ip> \
  [--port <1-65535>] \
  --config <file>
```

- `show` reads the current reader network settings through serial.
- `configure` is preview-only unless `--apply` is present.
- Apply reads the old values, writes a timestamped JSON backup, writes the proposed values, reads them back, requires an exact match, and only then reboots the reader.
- `verify` opens the reader over LAN using the vendor TCP transport.
- Port defaults to `1969`.
- Commissioning validates IPv4, a contiguous subnet mask, gateway membership in the configured subnet, non-network/non-broadcast reader address, optional IPv4 DNS, and port range.
- Exit codes: `0` success, `1` general/SDK failure, `2` bad arguments or validation, `3` reader lock occupied, `4` network verification failure.

The middleware must be stopped before a commissioning command because both use the exclusive reader lock. Commissioning does not need PostgreSQL or backend connectivity.

### Manifest, audit, database, and diagnostics

```text
rfid-middleware manifest status --config <file>
rfid-middleware manifest sync --config <file>
rfid-middleware audit stats --config <file>
rfid-middleware db verify --config <file>
rfid-middleware diagnostics bundle [--output <file>] --config <file>
```

- `manifest status` shows local authorization state, IDs, versions, expiry, and last error.
- `manifest sync` requests an immediate synchronization; normal polling remains every five seconds.
- `audit stats` reports local audit totals.
- `db verify` performs a local persistence check.
- `diagnostics bundle` saves status and database verification JSON. Default output is `rfid-diagnostics.json`.

## 8. Configuration reference

Environment variables use the `RFID_` prefix. Nested JSON keys use double underscores. For example, JSON `Server.BaseUrl` becomes `RFID_Server__BaseUrl`.

| Setting/environment | Purpose | Typical value |
|---|---|---|
| `RFID_Id` / `RFID_MIDDLEWARE_ID` in Compose | Gate identity | `WH-A-GATE-01` |
| `RFID_API_TOKEN` | Middleware bearer token | secret issued by backend |
| `RFID_Server__BaseUrl` / `RFID_SERVER_BASE_URL` | Backend root URL | `https://backend.example` |
| `RFID_DatabaseConnectionString` | Local `rfid_edge` PostgreSQL | connection string |
| `RFID_ControlSocket` | Local-only command socket | `/run/rfid-middleware/control.sock` |
| `RFID_ReaderLockFile` | Exclusive device lock | `/run/rfid-middleware/reader.lock` |
| `RFID_CommissioningBackupDirectory` | Network backup directory | `/var/lib/rfid-middleware/commissioning` |
| `RFID_ReaderMode` | `ManagedFake` or `Vendor` | `Vendor` |
| `RFID_VendorReader__Transport` / `RFID_READER_TRANSPORT` | `NativeMock`, `Serial`, or `Tcp` | `Tcp` |
| `RFID_VendorReader__Address` / `RFID_READER_ADDRESS` | Reader IPv4 address | `192.168.10.20` |
| `RFID_VendorReader__Port` / `RFID_READER_PORT` | Reader TCP port | `1969` |
| `RFID_VendorReader__SerialDevice` / `RFID_SERIAL_DEVICE` | BAFO/reader device | `/dev/serial/by-id/...` |
| `RFID_VendorReader__Baud` / `RFID_READER_BAUD` | Serial speed | `115200` |
| `RFID_VendorReader__Rs485Address` / `RFID_READER_RS485_ADDRESS` | Reader bus address | `0` |
| `RFID_VendorReader__AntennaPower` / `RFID_READER_ANTENNA_POWER` | RF power applied to every detected antenna at reader connection; optional, `1-33` dBm | `10` for close-range bench testing |
| `RFID_VendorReader__TimeoutMilliseconds` | SDK operation timeout | `6000` |
| `RFID_VendorReader__AlarmMode` / `RFID_READER_ALARM_MODE` | Alarm channel | `BuzzerAndDigitalOutput1` |
| `RFID_VendorReader__AlarmPulseMilliseconds` | Pulse duration | `1000` |
| `RFID_NATIVE_DIR` | Vendor native library directory | `/app/native` or repository SDK path |
| `LD_LIBRARY_PATH` | Native loader directory | same as native directory |

The plain application environment names and the Compose convenience names are not interchangeable unless the Compose file maps them. Follow the examples for the selected deployment method.

### Reader settings that can be configured

| Function | Setting | Allowed values / notes | Restart needed |
|---|---|---|---|
| Connection type | `RFID_READER_TRANSPORT` | `Serial`, `Tcp`, or `NativeMock` | Yes |
| Serial device | `RFID_SERIAL_DEVICE` | Linux device such as `/dev/ttyUSB0` or preferably `/dev/serial/by-id/...` | Recreate container |
| Serial speed | `RFID_READER_BAUD` | Normally `115200` | Recreate container |
| Reader bus address | `RFID_READER_RS485_ADDRESS` | Normally `0` for direct RS-232 commissioning | Recreate container |
| Reader LAN address | `RFID_READER_ADDRESS` | Static private IPv4 assigned to the reader | Recreate container |
| Reader LAN port | `RFID_READER_PORT` | `1-65535`; normally `1969` | Recreate container |
| Antenna RF power | `RFID_READER_ANTENNA_POWER` | Optional integer `1-33` dBm; applied to all detected antennas and verified by read-back | Reconnect/recreate |
| Alarm output | `RFID_READER_ALARM_MODE` | `Buzzer`, `DigitalOutput1`, or `BuzzerAndDigitalOutput1` | Recreate container |
| Alarm pulse | `RFID_VendorReader__AlarmPulseMilliseconds` | Milliseconds; direct app setting unless added to a Compose override | Restart/recreate |
| Middleware identity | `RFID_MIDDLEWARE_ID` | Must match the backend assignment | Recreate container |
| Backend | `RFID_SERVER_BASE_URL` | Real HTTPS backend, or mock backend in development only | Recreate container |
| Backend token | `RFID_API_TOKEN` | Middleware-role bearer token | Recreate container |

The middleware does not currently expose a live command that changes antenna power while inventory is running. Changing power reconnects the reader so that the configured value can be applied and read back. If the power setting is omitted, middleware leaves the power already stored in the reader unchanged. Omitting it does **not** restore an earlier value after a power value has already been applied; set the desired normal value explicitly.

### Antenna power commands

PowerShell, from the repository root, for the current serial development stack:

```powershell
$env:RFID_SERIAL_DEVICE='/dev/ttyUSB0'
$env:RFID_DIALOUT_GID='20'
$env:RFID_READER_ANTENNA_POWER='10'
$env:RFID_READER_ALARM_MODE='BuzzerAndDigitalOutput1'

docker compose -p rfid-dev `
  --env-file docker/dev.env `
  -f docker-compose.yml `
  -f docker-compose.dev.yml `
  -f docker-compose.serial.yml `
  up -d --build --force-recreate middleware

docker compose -p rfid-dev `
  --env-file docker/dev.env `
  -f docker-compose.yml `
  -f docker-compose.dev.yml `
  -f docker-compose.serial.yml `
  logs --tail 100 middleware
```

Look for `Configured <count> reader antennas to 10 dBm`. A missing confirmation or read-back error means the requested value was not verified. Suggested bench values are `5-10` dBm for very short range and `15-20` dBm for a wider test area. Choose the production value only after a site RF test; the supported software range is `1-33` dBm.

Ubuntu uses the same Compose files with shell syntax:

```bash
export RFID_SERIAL_DEVICE=/dev/serial/by-id/<reader-adapter-id>
export RFID_DIALOUT_GID="$(getent group dialout | cut -d: -f3)"
export RFID_READER_ANTENNA_POWER=10
export RFID_READER_ALARM_MODE=BuzzerAndDigitalOutput1

docker compose -p rfid-dev \
  --env-file docker/dev.env \
  -f docker-compose.yml \
  -f docker-compose.dev.yml \
  -f docker-compose.serial.yml \
  up -d --build --force-recreate middleware

docker compose -p rfid-dev \
  --env-file docker/dev.env \
  -f docker-compose.yml \
  -f docker-compose.dev.yml \
  -f docker-compose.serial.yml \
  logs --tail 100 middleware
```

To apply a different value, replace `10` and recreate the middleware. To stop middleware from changing reader power on future connections, remove `RFID_READER_ANTENNA_POWER` from the shell/environment file and recreate it. Remember that this leaves the last applied hardware value in place.

### Continuous PowerShell scan monitor

The helper below runs until `Ctrl+C` and prints every reader observation, including repeated reads of the same EPC. Authorization status is shown in green or red using the acceptance-test EPC list configured in the script:

```powershell
.\scripts\watch-scans.ps1 `
  -ProjectName rfid-dev `
  -SerialDevice /dev/ttyUSB0 `
  -DialoutGid 20 `
  -PollSeconds 1
```

`PollSeconds` controls how frequently the helper retrieves accumulated observations; it is not an overall duration or stop time.

## 9. Fast development stack with Docker and mock backend

The development override creates PostgreSQL databases `rfid_edge` and `rfid_backend`, starts the mock backend, generates a manifest, and uses `ManagedFakeReader` by default.

Create `docker/dev.env` if it is not already present:

```text
RFID_DATABASE_PASSWORD=rfid-dev-password
RFID_API_TOKEN=dev-token
RFID_SERVER_BASE_URL=http://mock-backend:5080
```

Start:

```bash
docker compose -p rfid-dev \
  --env-file docker/dev.env \
  -f docker-compose.yml \
  -f docker-compose.dev.yml \
  up -d --build
```

Inspect:

```bash
docker compose -p rfid-dev --env-file docker/dev.env \
  -f docker-compose.yml -f docker-compose.dev.yml ps

docker compose -p rfid-dev --env-file docker/dev.env \
  -f docker-compose.yml -f docker-compose.dev.yml logs -f middleware

docker compose -p rfid-dev --env-file docker/dev.env \
  -f docker-compose.yml -f docker-compose.dev.yml \
  exec middleware /app/rfid-middleware status --config /app/appsettings.json

docker compose -p rfid-dev --env-file docker/dev.env \
  -f docker-compose.yml -f docker-compose.dev.yml \
  exec middleware /app/rfid-middleware manifest status --config /app/appsettings.json

docker compose -p rfid-dev --env-file docker/dev.env \
  -f docker-compose.yml -f docker-compose.dev.yml \
  exec middleware /app/rfid-middleware audit stats --config /app/appsettings.json
```

The development backend is bound only to `127.0.0.1:5080`. Set `RFID_DEV_SEED_COUNT` to control generated tag count. Stop without deleting data:

```bash
docker compose -p rfid-dev --env-file docker/dev.env \
  -f docker-compose.yml -f docker-compose.dev.yml down
```

Add `--volumes` only when the development PostgreSQL data should be permanently removed.

## 10. Direct source development without Docker

Copy and load the example:

```bash
cp .env.example .env
set -a
source .env
set +a
```

Create the two development databases and grant ownership as appropriate for the configured `rfid` user. Then start the mock backend:

```bash
dotnet run --project tools/RfidMiddleware.MockServer -c Release -- --reset --seed-count 100
```

In another terminal, load `.env` again and start middleware:

```bash
set -a
source .env
set +a
dotnet run --project src/RfidMiddleware.Host -c Release -- \
  run --config src/RfidMiddleware.Host/appsettings.json
```

In a third terminal:

```bash
set -a
source .env
set +a
dotnet run --project src/RfidMiddleware.Host -c Release -- \
  status --config src/RfidMiddleware.Host/appsettings.json
```

Use `ManagedFake` first. Change `RFID_ReaderMode=Vendor` only when ready to access the SDK or physical reader.

## 11. WSL USB/COM procedure

Windows COM numbers do not become Linux filenames directly. A device shown as COM11 or COM12 in Device Manager normally appears as `/dev/ttyUSB0` after USB passthrough. Always discover the current bus ID and Linux path instead of assuming either value is permanent.

In Administrator PowerShell:

```powershell
usbipd list
usbipd bind --busid <BUSID>
usbipd attach --wsl --busid <BUSID>
```

Approve the Windows UAC prompt. Then in WSL:

```bash
lsusb
dmesg | tail -n 50
ls -l /dev/ttyUSB* /dev/ttyACM* 2>/dev/null
ls -l /dev/serial/by-id/ 2>/dev/null
sudo usermod -aG dialout "$USER"
```

Log out and back in after adding `dialout`. Prefer `/dev/serial/by-id/...`; use `/dev/ttyUSB0` only as a temporary fallback. Check ownership conflicts:

```bash
lsof /dev/ttyUSB0
```

Configure direct serial operation:

```bash
export RFID_ReaderMode=Vendor
export RFID_VendorReader__Transport=Serial
export RFID_VendorReader__SerialDevice=/dev/ttyUSB0
export RFID_VendorReader__Baud=115200
export RFID_VendorReader__Rs485Address=0
export RFID_VendorReader__AlarmMode=BuzzerAndDigitalOutput1
export RFID_NATIVE_DIR="$PWD/linux/x86_64/release"
export LD_LIBRARY_PATH="$PWD/linux/x86_64/release"
```

For Docker serial operation, use the serial override described below. Detach after testing from PowerShell:

```powershell
usbipd detach --busid <BUSID>
```

## 12. BAFO commissioning: serial to permanent LAN

Obtain an approved fixed reader address outside the dynamic DHCP pool, or have the network administrator reserve it. Connect both Ethernet and BAFO to the reader. Stop middleware first.

### Direct host commands

Set serial values and native library paths as in the previous section, then read current settings:

```bash
dotnet run --project src/RfidMiddleware.Host -c Release -- \
  reader network show --json \
  --config src/RfidMiddleware.Host/appsettings.json
```

Preview a change—the following command does not write or reboot:

```bash
dotnet run --project src/RfidMiddleware.Host -c Release -- \
  reader network configure \
  --ip 192.168.10.20 \
  --mask 255.255.255.0 \
  --gateway 192.168.10.1 \
  --port 1969 \
  --dns 192.168.10.2 \
  --config src/RfidMiddleware.Host/appsettings.json
```

Review `current` and `proposed`. Apply only when correct:

```bash
dotnet run --project src/RfidMiddleware.Host -c Release -- \
  reader network configure \
  --ip 192.168.10.20 \
  --mask 255.255.255.0 \
  --gateway 192.168.10.1 \
  --port 1969 \
  --dns 192.168.10.2 \
  --apply \
  --config src/RfidMiddleware.Host/appsettings.json
```

Wait for reboot, switch to the LAN path, and verify:

```bash
dotnet run --project src/RfidMiddleware.Host -c Release -- \
  reader network verify --address 192.168.10.20 --port 1969 \
  --config src/RfidMiddleware.Host/appsettings.json
```

Power-cycle the reader and run `verify` again. Only then remove BAFO.

### Docker commissioning

Set the stable device and group ID:

```bash
export RFID_SERIAL_DEVICE=/dev/serial/by-id/REPLACE_WITH_BAFO_ID
export RFID_DIALOUT_GID="$(getent group dialout | cut -d: -f3)"
```

Stop the operating middleware and read the reader:

```bash
docker compose -p rfid-prod --env-file .env.production \
  -f docker-compose.yml -f docker-compose.serial.yml stop middleware

docker compose -p rfid-prod --env-file .env.production \
  -f docker-compose.yml -f docker-compose.serial.yml \
  run --rm --no-deps middleware \
  reader network show --json --config /app/appsettings.json
```

Run the same `reader network configure` arguments shown above, first without `--apply`, then with `--apply`. Backups persist in the `rfid-middleware-data` volume under `/var/lib/rfid-middleware/commissioning`.

Verify over TCP:

```bash
export RFID_READER_ADDRESS=192.168.10.20
docker compose -p rfid-prod --env-file .env.production \
  -f docker-compose.yml -f docker-compose.tcp.yml \
  run --rm --no-deps middleware \
  reader network verify --address "$RFID_READER_ADDRESS" --port 1969 \
  --config /app/appsettings.json
```

If verification fails, leave BAFO connected, inspect the newest backup, preview the old values with `configure`, apply them, and verify again. Never leave a site without proving persistence after a power cycle.

### Ubuntu VirtualBox USB attachment

Install the matching VirtualBox Extension Pack on the Windows host. Shut down the VM, enable its USB controller, and add a USB filter for the BAFO/Prolific adapter. Start Ubuntu and select the adapter from **Devices > USB** if it was not captured automatically. The USB device can belong to only one operating system at a time; close Windows serial tools first.

Inside Ubuntu:

```bash
lsusb
dmesg | tail -n 50
ls -l /dev/ttyUSB* /dev/ttyACM* 2>/dev/null
ls -l /dev/serial/by-id/ 2>/dev/null
sudo usermod -aG dialout "$USER"
```

Log out and back in after the group change. If no device appears, detach and reattach it from the VirtualBox USB menu, verify that the host driver is not holding it, and confirm the VM USB controller matches the adapter. Use the stable `/dev/serial/by-id/...` path in configuration.

### Direct hardware middleware run after commissioning

With the reader on LAN and the backend/PostgreSQL available:

```bash
export RFID_ReaderMode=Vendor
export RFID_VendorReader__Transport=Tcp
export RFID_VendorReader__Address=192.168.10.20
export RFID_VendorReader__Port=1969
export RFID_VendorReader__AlarmMode=BuzzerAndDigitalOutput1
export RFID_NATIVE_DIR="$PWD/linux/x86_64/release"
export LD_LIBRARY_PATH="$PWD/linux/x86_64/release"

dotnet run --project src/RfidMiddleware.Host -c Release -- \
  run --config src/RfidMiddleware.Host/appsettings.json
```

Keep this terminal open. Use a second terminal for `status`, `reader test`, `reader watch`, and `manifest status`. Stop cleanly with `Ctrl+C`.

## 13. Production Docker deployment

Copy and secure the production template:

```bash
cp .env.production.example .env.production
chmod 600 .env.production
```

Required values:

```text
RFID_MIDDLEWARE_ID=WH-A-GATE-01
RFID_API_TOKEN=<real-middleware-token>
RFID_SERVER_BASE_URL=https://real-backend.example
RFID_DATABASE_USER=rfid
RFID_DATABASE_PASSWORD=<strong-password>
RFID_READER_MODE=Vendor
RFID_READER_TRANSPORT=Tcp
RFID_READER_ADDRESS=192.168.10.20
RFID_READER_PORT=1969
RFID_READER_ALARM_MODE=BuzzerAndDigitalOutput1
```

Validate the rendered configuration and ensure it contains no `mock-backend` service:

```bash
docker compose -p rfid-prod --env-file .env.production \
  -f docker-compose.yml -f docker-compose.tcp.yml config
```

Start production:

```bash
docker compose -p rfid-prod --env-file .env.production \
  -f docker-compose.yml -f docker-compose.tcp.yml \
  up -d --build
```

Inspect:

```bash
docker compose -p rfid-prod --env-file .env.production \
  -f docker-compose.yml -f docker-compose.tcp.yml ps

docker compose -p rfid-prod --env-file .env.production \
  -f docker-compose.yml -f docker-compose.tcp.yml logs -f middleware

docker compose -p rfid-prod --env-file .env.production \
  -f docker-compose.yml -f docker-compose.tcp.yml \
  exec middleware /app/rfid-middleware status --config /app/appsettings.json
```

Never include `docker-compose.dev.yml` in production or shipment commands. Without it, the mock backend, development token, generated manifests, and `rfid_backend` database do not exist. Use distinct Compose project names (`rfid-dev` and `rfid-prod`) to prevent development volumes from being reused in production.

PostgreSQL and middleware state are stored in named volumes. A normal `down` retains them. Do not use `down --volumes` in production.

## 14. Docker hardware overrides

### Permanent TCP reader

```bash
export RFID_READER_ADDRESS=192.168.10.20
export RFID_READER_PORT=1969
docker compose -p rfid-prod --env-file .env.production \
  -f docker-compose.yml -f docker-compose.tcp.yml up -d --build
```

### Temporary serial reader/BAFO

```bash
export RFID_SERIAL_DEVICE=/dev/serial/by-id/REPLACE_WITH_DEVICE_ID
export RFID_DIALOUT_GID="$(getent group dialout | cut -d: -f3)"
export RFID_READER_BAUD=115200
export RFID_READER_RS485_ADDRESS=0
docker compose -p rfid-dev --env-file docker/dev.env \
  -f docker-compose.yml -f docker-compose.dev.yml -f docker-compose.serial.yml \
  up -d --build
```

The override maps the selected host device to `/dev/rfid-reader` and adds the host `dialout` group ID.

## 15. Hardware-team test procedure

Perform this sequence with the real antennas and alarm wiring. Keep people and unrelated tagged inventory away from the read zone.

1. Confirm the exact reader model, serial number, firmware, power supply, antenna ports, and cable condition.
2. Confirm the reader and Ubuntu host have private LAN reachability.
3. Verify TCP without exposing it publicly:

   ```bash
   ping -c 4 192.168.10.20
   timeout 3 bash -c '</dev/tcp/192.168.10.20/1969'
   ```

4. Start middleware in `BuzzerAndDigitalOutput1` setup mode.
5. Confirm `reader test` reports connected and `manifest status` shows the expected active version.
6. Present each of the two authorized EPCs separately. Each must be audited locally and must not activate any alarm channel.
7. Present `E280689400004010BF78D96A` or another known-unlisted test tag. The middleware must attempt the built-in buzzer and output 1. Output 0 must remain unused.
8. Observe the three-second cooldown by leaving the same unauthorized tag in the field; the output must not chatter continuously.
9. Check `status`, logs, and the physical channels. An unsupported channel may show critical health while other channels still operate.
10. Test `Buzzer` and `DigitalOutput1` separately to identify actual hardware capability and wiring.
11. Verify output polarity and pulse time with a meter/test load before attaching the final alarm appliance.
12. Disconnect backend access briefly. The cached unexpired manifest must continue working.
13. Do not wait for expiry on a live production batch; use a controlled test manifest to prove expired/no-manifest fail-closed behavior.
14. Restart middleware and confirm the manifest/authorization state survives.
15. Power-cycle reader and server, then repeat the authorized and unauthorized tests.
16. Run a sustained scan test across every intended antenna and document blind spots and cross-reads.

After acceptance, choose the final single alarm mode when possible. Leave combined mode only if all three channels are intentionally required and supported.

## 16. Publish the exact two-tag development manifest

This admin endpoint exists only on the development mock backend. Determine the current version first and choose a strictly higher version.

```bash
curl -fsS -X POST \
  -H 'Content-Type: application/json' \
  --data '{
    "manifestId":"HW-ACCEPTANCE",
    "transportId":"HW-ACCEPTANCE",
    "version":25,
    "count":2,
    "expiresMinutes":1440,
    "items":[
      {"inventoryId":"HW-TAG-001","epc":"E28069950000400E45B4F53E"},
      {"inventoryId":"HW-TAG-002","epc":"E280689400005010BF75955B"}
    ]
  }' \
  http://127.0.0.1:5080/admin/middleware/WH-A-GATE-01/manifest/activate | jq
```

Request immediate middleware sync:

```bash
docker compose -p rfid-dev --env-file docker/dev.env \
  -f docker-compose.yml -f docker-compose.dev.yml \
  exec middleware /app/rfid-middleware manifest sync --config /app/appsettings.json
```

Then confirm `manifest status` reports that version and the expected expiry.

## 17. Mobile/backend integration contract

Android calls the backend, never the reader or middleware. Middleware and mobile use different bearer-token roles. Development uses:

```text
Middleware token: dev-token
Mobile token:     dev-mobile-token
Middleware ID:    WH-A-GATE-01
```

Production must replace both tokens and use HTTPS. Mobile mutations keep the same `manifestId` and create the next immutable version. Every mutation is idempotent by client role, middleware ID, and `requestId`.

Common headers:

```http
Authorization: Bearer <mobile-access-token>
Content-Type: application/json
```

### Authorize an additional tag

```http
POST /api/v1/mobile/middleware/{middlewareId}/manifests/{manifestId}/items/authorize
```

```json
{
  "requestId": "550e8400-e29b-41d4-a716-446655440000",
  "expectedVersion": 17,
  "inventoryId": "INV-EXTRA-001",
  "epc": "E2801170AAAAAAAAAAAAAAAA",
  "note": "Approved additional item"
}
```

Register additional tags before they pass the gate. A backend success does not mean the gate has already synchronized; show a pending-sync state until the new version reaches the middleware.

### Revoke tags

```http
POST /api/v1/mobile/middleware/{middlewareId}/manifests/{manifestId}/revoke
```

```json
{
  "requestId": "8b698769-983d-4b99-b608-a52b753d51a8",
  "expectedVersion": 18,
  "epcs": ["E2801170AAAAAAAAAAAAAAAA"],
  "reason": "RETURNED_TO_STORAGE",
  "requestedBy": "operator-123",
  "occurredAt": "2026-10-19T10:30:00Z",
  "completeBatch": false
}
```

Every EPC must exist in the active manifest. Revoking the final EPC without completion returns `409`. Set `completeBatch: true` only when revoking every remaining EPC, or preferably use explicit completion.

### Complete a batch

```http
POST /api/v1/mobile/middleware/{middlewareId}/manifests/{manifestId}/complete
```

```json
{
  "requestId": "04fdd430-f662-4346-a596-49a24a3eb9c2",
  "expectedVersion": 19,
  "note": "Return reconciliation approved"
}
```

Completion publishes a higher `COMPLETED` version with no authorized EPCs. Middleware atomically clears active authorization. Android must disable further mutations for the completed batch.

### Idempotency and concurrency

- Generate and persist one request ID per user-confirmed logical action.
- On timeout or unknown outcome, retry the identical request body with the same ID.
- An identical retry returns the original result without creating a new version.
- Reusing an ID with different content returns `409 Conflict`.
- A stale `expectedVersion` returns `409` with the current version.
- On a stale version, refetch business state, reconcile, ask for confirmation if the action changed, and submit a new logical request with a new ID.

### EPC validation

Normalize by trimming and uppercasing. Accept hexadecimal `0-9A-F`, an even length, and 2–512 characters. Android should reject invalid input before calling the backend.

### Mobile status handling

| HTTP status | Mobile action |
|---|---|
| `200` | Save returned version/status/result |
| `400` | Show validation/business error; do not automatically retry |
| `401` | Refresh authentication or report access failure |
| `404` | Refresh batch state and verify IDs |
| `409` with current version | Refresh and reconcile before a new request |
| other `409` | Request-ID or business conflict; show error |
| timeout/`5xx` | Retry identical persisted body and request ID |

Missing-tag and return reconciliation remain mobile/backend business logic. Do not infer completion merely because currently seen tags have been processed.

## 18. Mock backend administration and fault testing

Mock backend health:

```bash
curl -fsS http://127.0.0.1:5080/healthz | jq
```

Reset generated data:

```bash
curl -fsS -X POST 'http://127.0.0.1:5080/admin/reset?count=100' | jq
```

Fault endpoint:

```http
PUT /admin/middleware/{middlewareId}/fault
Content-Type: application/json
```

The development fixture includes profiles such as `none`, `timeout`, `500`, `429`, `malformed`, `expired`, `wrong-count`, `duplicate-epc`, and `invalid-checksum`. These are development tools only and must not be deployed with production.

## 19. Debian package and systemd deployment

Build:

```bash
bash scripts/build-deb.sh 0.1.0
```

Install the generated package from `artifacts/`:

```bash
sudo dpkg -i artifacts/rfid-middleware_0.1.0_amd64.deb
```

Important paths:

| Purpose | Path |
|---|---|
| Application | `/usr/lib/rfid-middleware` |
| Main config | `/etc/rfid-middleware/appsettings.json` |
| Secrets/environment | `/etc/rfid-middleware/environment` |
| Runtime/control socket | `/run/rfid-middleware/control.sock` |
| Commissioning backups/data | `/var/lib/rfid-middleware` |

Copy values from `/etc/rfid-middleware/environment.example`, replace placeholders, then protect the file:

```bash
sudo chown root:rfid-middleware /etc/rfid-middleware/environment
sudo chmod 0640 /etc/rfid-middleware/environment
sudo systemctl daemon-reload
sudo systemctl enable --now rfid-middleware
```

Operate:

```bash
sudo systemctl status rfid-middleware --no-pager
sudo journalctl -u rfid-middleware -f
sudo systemctl restart rfid-middleware
sudo systemctl stop rfid-middleware
/usr/lib/rfid-middleware/rfid-middleware status \
  --config /etc/rfid-middleware/appsettings.json
```

The service runs as the restricted `rfid-middleware` user with `dialout` supplementary membership.

## 20. Build and test commands

Run the full build/test/native ABI/load sequence:

```bash
bash scripts/build.sh
```

Additional tests:

```bash
bash scripts/native-stability.sh 100
bash scripts/soak.sh 2
```

Use `72` instead of `2` for the release soak. PostgreSQL integration tests use `RFID_TEST_POSTGRES` and create isolated temporary schemas. Physical acceptance is still mandatory because native mocks cannot prove antenna RF coverage, firmware behavior, electrical output capability, or external alarm wiring.

### Local PostgreSQL inspection

The middleware initializes its own schema. Connect with the same values used by `RFID_DatabaseConnectionString`:

```bash
psql 'host=127.0.0.1 port=5432 dbname=rfid_edge user=rfid password=rfid-dev-password'
```

Inside `psql`, inspect the schema first so queries remain compatible with the installed version:

```sql
\dt
SELECT table_name FROM information_schema.tables
WHERE table_schema = 'public'
ORDER BY table_name;
```

Use `\d <table>` before querying a table. Operationally, prefer `audit stats` and a diagnostics bundle because they are stable public interfaces. Never give the backend direct access to local scan-event tables.

## 21. Twingate and dynamic server addressing

Twingate is suitable for the Ubuntu server's outbound production-backend connection and remote administration. It does not replace the local reader link.

- The middleware initiates HTTPS to the backend, so the warehouse's public IP can remain hidden and dynamic.
- The middleware initiates TCP to the reader, so the reader stores no server IP.
- The reader and Ubuntu server need private routed reachability on the warehouse LAN/VLAN.
- Give the reader a fixed/reserved private address; the Ubuntu host may use DHCP.
- External/mobile applications connect to the production backend, not directly to the reader or middleware.
- Do not create public NAT/port-forwarding for reader TCP 1969.

If remote administrators need the Ubuntu host, publish that resource through the organization's approved Twingate policy. Keep production backend DNS stable even when its underlying private address changes.

## 22. Troubleshooting

### Overall status is critical

```bash
rfid-middleware status --config <file>
rfid-middleware db verify --config <file>
rfid-middleware diagnostics bundle --output rfid-diagnostics.json --config <file>
```

Inspect `reader.error`, `alarm.error`, `lastSystemError`, `lastManifestError`, dropped reads, authorization state, and expiry. `Uninitialized` is intentionally critical. `Completed` is intentionally idle and can be healthy.

### Alarm error `-3034`

The selected relay/output is unsupported, disabled, or not configured by the reader. Combined mode still tries the other channel. Test the buzzer and output 1 individually, check the device configuration and firmware, and verify wiring with the vendor manual. Output 0 is intentionally unused.

### Native library fails to load

```bash
file linux/x86_64/release/librfid.so
ldd linux/x86_64/release/librfid.so
export RFID_NATIVE_DIR="$PWD/linux/x86_64/release"
export LD_LIBRARY_PATH="$PWD/linux/x86_64/release"
```

Use Linux x86-64 libraries on Ubuntu x86-64. Do not use the Windows DLL.

### Serial device missing or permission denied

```bash
lsusb
dmesg | tail -n 50
ls -l /dev/ttyUSB* /dev/ttyACM* 2>/dev/null
ls -l /dev/serial/by-id/ 2>/dev/null
id
lsof /dev/ttyUSB0
```

Reattach through `usbipd` for WSL, reconnect the VirtualBox USB device for an Ubuntu VM, add the operator/service to `dialout`, and stop other reader tools.

### Reader lock occupied

Stop middleware and vendor reader utilities before commissioning. Do not delete a lock blindly while a process is active. Identify the owner with `lsof` and stop it normally.

### LAN verification fails

- Keep BAFO attached.
- Confirm reader IP, subnet, gateway, and TCP port through `reader network show`.
- Confirm Ethernet link LEDs, switch VLAN, Ubuntu route, and firewall.
- Restore the timestamped backup through preview then `--apply` if necessary.
- Power-cycle and verify again.

### Backend unavailable

Check DNS, routing/Twingate, TLS certificate, URL, token, and backend health. The middleware keeps the last valid cached manifest until expiry. Do not delete the local PostgreSQL volume during an outage.

### Manifest changes do not take effect

Confirm Android received a higher version, then run `manifest sync` and inspect `manifest status`. An equal/older version is rejected. Check backend token role and middleware ID. Do not interpret a mobile `200` as proof the gate already synchronized.

### Docker container unhealthy

```bash
docker compose -p rfid-prod --env-file .env.production \
  -f docker-compose.yml -f docker-compose.tcp.yml ps
docker compose -p rfid-prod --env-file .env.production \
  -f docker-compose.yml -f docker-compose.tcp.yml logs --tail=200 middleware
```

The container health check uses the Unix socket internally; no middleware HTTP port needs publishing.

## 23. Final production acceptance checklist

- [ ] Ubuntu 24.04 x86-64 and reader firmware/model are recorded.
- [ ] Reader has an approved fixed/reserved private IPv4 address.
- [ ] Reader TCP 1969 is reachable only from approved warehouse systems.
- [ ] BAFO settings survive reader power-cycle; BAFO is removed after commissioning.
- [ ] Production uses the base Compose file plus TCP override, with no development override.
- [ ] Production backend uses HTTPS and separate middleware/mobile tokens.
- [ ] PostgreSQL and middleware volumes are persistent and backed up appropriately.
- [ ] Expected active manifest ID, version, item count, and expiry are visible locally.
- [ ] Both authorized acceptance EPCs pass silently.
- [ ] The unauthorized acceptance EPC activates the expected physical alarm.
- [ ] Buzzer and output 1 capability has been documented; output 0 remains unused.
- [ ] Final alarm mode, polarity, pulse duration, interposing relay, and load are approved.
- [ ] Technical faults show critical health without directly pulsing the physical alarm.
- [ ] Backend outage behavior and manifest expiry fail-closed behavior are verified safely.
- [ ] Completion clears authorization and survives middleware restart.
- [ ] Local audits remain local; outbound traffic contains manifest requests only.
- [ ] Antenna coverage, cross-read behavior, and sustained scanning are accepted.
- [ ] Release build, native stability test, and soak test pass.

## 24. Security and operating rules

- Never expose TCP 1969 publicly.
- Never ship `docker-compose.dev.yml`, `dev-token`, or `dev-mobile-token`.
- Never give the middleware bearer token to Android.
- Never commit environment files or secrets.
- Keep PostgreSQL private; scan data is local operational data.
- Restrict access to the Unix control socket and commissioning backups.
- Use HTTPS and validate certificates for the production backend.
- Stop middleware before serial commissioning.
- Do not erase production volumes to resolve an outage.
- Do not energize an external alarm load before its electrical design is approved.

This document describes the repository implementation as of October 19, 2026. When CLI help, Compose files, or production infrastructure changes, update this handbook in the same change.
