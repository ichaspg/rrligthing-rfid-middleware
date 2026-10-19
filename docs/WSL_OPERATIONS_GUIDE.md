# WSL Operations and Test Guide

This guide covers a clean WSL setup, simulated operation, the mobile-controlled manifest lifecycle, and connection of a real RFID reader. Commands are written for this repository at:

```text
C:\Users\user\Documents\Work\rrligthing-rfid-middleware
```

Inside WSL that path is:

```text
/mnt/c/Users/user/Documents/Work/rrligthing-rfid-middleware
```

## 1. What the middleware does

```text
Android authorizes or revokes EPCs
        ↓
Backend publishes a higher immutable manifest version
        ↓
Middleware downloads the current authorization list
        ↓
Reader observes an EPC
  ├─ valid and listed in a non-expired ACTIVE manifest → no alarm
  └─ unlisted, malformed, expired, or no active manifest → alarm
```

Scans and their decisions stay in the local `rfid_edge` PostgreSQL database. The middleware sends only manifest requests to the backend. Database, reader, queue, and synchronization failures affect health/logs/metrics but do not pulse the physical alarm by themselves.

## 2. One-time Windows and WSL setup

Open PowerShell as Administrator:

```powershell
wsl --install -d Ubuntu-24.04
winget install --exact --id dorssel.usbipd-win
```

Restart Windows if either installer requests it. Open Ubuntu once and create your normal Linux username and password. Then run:

```bash
sudo apt-get update
sudo apt-get install -y dotnet-sdk-10.0 build-essential postgresql postgresql-client valgrind strace lsof patchelf curl jq
sudo systemctl enable --now postgresql
```

The service targets .NET 8, but the .NET 10 SDK is used to build the `.slnx` solution.

Create the local development databases. These credentials are for local testing only:

```bash
sudo -u postgres createuser --login rfid
sudo -u postgres psql -c "ALTER ROLE rfid WITH PASSWORD 'rfid-dev-password';"
sudo -u postgres createdb -O rfid rfid_backend
sudo -u postgres createdb -O rfid rfid_edge
```

If a role or database already exists, do not recreate it. Verify the connection:

```bash
PGPASSWORD=rfid-dev-password psql -h 127.0.0.1 -U rfid -d rfid_edge -c "select current_database(), current_user;"
```

## 3. Prepare the repository

Open Ubuntu and run:

```bash
cd /mnt/c/Users/user/Documents/Work/rrligthing-rfid-middleware
cp -n .env.example .env
set -a
source .env
set +a
```

Keep `.env` local. Never put production tokens or passwords in Git.

Run the full automated suite:

```bash
export RFID_TEST_POSTGRES='Host=127.0.0.1;Port=5432;Database=rfid_edge;Username=rfid;Password=rfid-dev-password'
bash scripts/build.sh
```

The build script restores and builds the solution, runs unit/integration/end-to-end tests, runs the native ABI probe, and runs a 100,000-EPC/250,000-observation load test.

Run the vendor SDK lifecycle test directly:

```bash
LD_LIBRARY_PATH="$PWD/tests/RfidMiddleware.NativeTests/bin/Release/net8.0/native" \
  dotnet tests/RfidMiddleware.NativeTests/bin/Release/net8.0/RfidMiddleware.NativeTests.dll 100
```

`scripts/native-stability.sh` adds Valgrind. On WSL, Valgrind can report CoreCLR address-space and runtime false positives before the RFID code runs. Use the direct 100-cycle command above for WSL lifecycle validation, and run the Valgrind wrapper on the target Ubuntu host for release acceptance.

## 4. Safe simulated operation

Use three Ubuntu terminals. In each one, first run:

```bash
cd /mnt/c/Users/user/Documents/Work/rrligthing-rfid-middleware
set -a
source .env
set +a
```

### Terminal 1: start the local backend

```bash
dotnet run --project tools/RfidMiddleware.MockServer -c Release -- --reset --seed-count 100
```

It listens on `http://127.0.0.1:5080`. The reset creates active manifest `TR-2026-00128`, version 17, with 100 EPCs.

### Terminal 2: start middleware with a fake reader

```bash
export RFID_ReaderMode=ManagedFake
dotnet run --project src/RfidMiddleware.Host -c Release -- run --config appsettings.json
```

The fake reader generates authorized EPCs plus an unauthorized EPC at a controlled interval. Unauthorized observations appear as `Unauthorized RFID observation alarm` in the log. Temporary real-reader setup mode attempts the built-in buzzer plus digital outputs 0 and 1.

### Terminal 3: operate and inspect

```bash
dotnet run --project src/RfidMiddleware.Host -- status --config appsettings.json
dotnet run --project src/RfidMiddleware.Host -- reader test --config appsettings.json
dotnet run --project src/RfidMiddleware.Host -- reader watch --duration 5 --config appsettings.json
dotnet run --project src/RfidMiddleware.Host -- manifest status --config appsettings.json
dotnet run --project src/RfidMiddleware.Host -- manifest sync --config appsettings.json
dotnet run --project src/RfidMiddleware.Host -- audit stats --config appsettings.json
dotnet run --project src/RfidMiddleware.Host -- db verify --config appsettings.json
dotnet run --project src/RfidMiddleware.Host -- diagnostics bundle --output artifacts/diagnostics.json --config appsettings.json
```

Healthy active operation should show a connected reader, `authorizationState: 1`, an active manifest version, zero dropped reads, and `scanDataPolicy: LOCAL_ONLY`. Completed/idle operation shows `authorizationState: 2`, the latest completed version, no active manifest, and remains healthy.

## 5. Test the Android/backend lifecycle

Development credentials are deliberately separated:

- Middleware manifest token: `dev-token`
- Android/mobile mutation token: `dev-mobile-token`

The following commands run in Ubuntu while the backend and middleware are running.

### Read the current backend manifest

```bash
curl -fsS \
  -H 'Authorization: Bearer dev-token' \
  http://127.0.0.1:5080/api/v1/middleware/WH-A-GATE-01/manifest | jq
```

### Authorize an additional EPC before it reaches the gate

Use the current version returned by the previous call as `expectedVersion`:

```bash
curl -fsS -X POST \
  -H 'Authorization: Bearer dev-mobile-token' \
  -H 'Content-Type: application/json' \
  --data '{
    "requestId":"android-authorize-001",
    "expectedVersion":17,
    "inventoryId":"INV-EXTRA-001",
    "epc":"E2801170AAAAAAAAAAAAAAAA",
    "note":"Added before gate"
  }' \
  http://127.0.0.1:5080/api/v1/mobile/middleware/WH-A-GATE-01/manifests/TR-2026-00128/items/authorize | jq
```

This publishes version 18. Repeating the identical request returns the original result with `alreadyApplied: true`. Reusing the request ID with different content returns `409`. A different request with stale `expectedVersion` also returns `409` and includes `currentVersion`.

Force immediate synchronization or wait for the five-second poll:

```bash
dotnet run --project src/RfidMiddleware.Host -- manifest sync --config appsettings.json
dotnet run --project src/RfidMiddleware.Host -- manifest status --config appsettings.json
```

Do not allow the newly added physical tag through the gate until the middleware reports version 18.

### Revoke an EPC

Both `reason` and `requestedBy` are required:

```bash
curl -fsS -X POST \
  -H 'Authorization: Bearer dev-mobile-token' \
  -H 'Content-Type: application/json' \
  --data '{
    "requestId":"android-revoke-001",
    "expectedVersion":18,
    "epcs":["E2801170AAAAAAAAAAAAAAAA"],
    "reason":"RETURNED_TO_STORAGE",
    "requestedBy":"operator-123"
  }' \
  http://127.0.0.1:5080/api/v1/mobile/middleware/WH-A-GATE-01/manifests/TR-2026-00128/revoke | jq
```

After version 19 is synchronized, that EPC is unauthorized and raises the alarm if scanned. Revoking the final remaining EPC requires `"completeBatch":true`; otherwise use the completion endpoint.

### Complete the batch

```bash
curl -fsS -X POST \
  -H 'Authorization: Bearer dev-mobile-token' \
  -H 'Content-Type: application/json' \
  --data '{
    "requestId":"android-complete-001",
    "expectedVersion":19,
    "note":"Return reconciliation complete"
  }' \
  http://127.0.0.1:5080/api/v1/mobile/middleware/WH-A-GATE-01/manifests/TR-2026-00128/complete | jq
```

After synchronization, the middleware has no active authorization list. It is intentionally idle and healthy, but every subsequent tag observation alarms until a newer active manifest is assigned. Restart the middleware and run `manifest status` to verify the completed version remains persisted.

Missing-tag reconciliation, return readiness, and decisions about which extra tags belong to the batch remain Android/backend responsibilities. The middleware only enforces the latest published EPC list.

## 6. Connect a real reader

Stop the simulated middleware with `Ctrl+C` before changing reader mode.

### Option A: Ethernet/TCP reader (preferred)

Connect the reader and computer to the same network. Confirm the configured address from Windows PowerShell:

```powershell
Test-NetConnection -ComputerName 192.168.1.223 -Port 1969
```

`TcpTestSucceeded` must be `True`. In Ubuntu:

```bash
cd /mnt/c/Users/user/Documents/Work/rrligthing-rfid-middleware
set -a
source .env
set +a
export RFID_ReaderMode=Vendor
export RFID_VendorReader__Transport=Tcp
export RFID_VendorReader__Address=192.168.1.223
export RFID_VendorReader__Port=1969
export RFID_NATIVE_DIR="$PWD/linux/x86_64/release"
export LD_LIBRARY_PATH="$PWD/linux/x86_64/release"
dotnet run --project src/RfidMiddleware.Host -c Release -- run --config appsettings.json
```

If TCP fails, check reader power, Ethernet link lights, reader IP/subnet, Windows Wi-Fi/Ethernet route, port 1969, and firewall rules. Do not change the reader IP until its current network configuration is confirmed.

### Option B: USB serial reader through WSL

Plug in the reader/USB-to-serial adapter. In Administrator PowerShell:

```powershell
usbipd list
```

Find the row matching the adapter, often `Prolific USB-to-Serial`, `FTDI`, `CH340`, or the reader vendor. Use the bus ID shown at that moment; it can change after moving USB ports.

```powershell
usbipd bind --busid <BUSID>
usbipd attach --wsl --busid <BUSID>
```

`bind` normally runs once per physical USB device and requires Administrator PowerShell. Keep an Ubuntu terminal open before running `attach`; usbipd 5.3 selects the running WSL distribution automatically. `attach` is needed again after unplugging the device or restarting Windows/WSL. Attaching removes the device from Windows while WSL owns it.

In Ubuntu:

```bash
lsusb
dmesg | tail -n 30
ls -l /dev/ttyUSB* /dev/ttyACM* 2>/dev/null
ls -l /dev/serial/by-id/ 2>/dev/null
```

Give your normal Linux user serial access, then close and reopen Ubuntu:

```bash
sudo usermod -aG dialout "$USER"
```

Start the middleware. Prefer the stable `/dev/serial/by-id/...` value when present:

```bash
cd /mnt/c/Users/user/Documents/Work/rrligthing-rfid-middleware
set -a
source .env
set +a
export RFID_ReaderMode=Vendor
export RFID_VendorReader__Transport=Serial
export RFID_VendorReader__SerialDevice=/dev/ttyUSB0
export RFID_VendorReader__Baud=115200
export RFID_VendorReader__Rs485Address=0
export RFID_NATIVE_DIR="$PWD/linux/x86_64/release"
export LD_LIBRARY_PATH="$PWD/linux/x86_64/release"
dotnet run --project src/RfidMiddleware.Host -c Release -- run --config appsettings.json
```

When finished, stop middleware and return the USB device to Windows:

```powershell
usbipd detach --busid <BUSID>
```

### Option C: vendor native mock

This validates P/Invoke loading and vendor connect/disconnect behavior without hardware:

```bash
export RFID_ReaderMode=Vendor
export RFID_VendorReader__Transport=NativeMock
export RFID_VendorReader__MockPort=26790
export RFID_NATIVE_DIR="$PWD/linux/x86_64/release"
export LD_LIBRARY_PATH="$PWD/linux/x86_64/release"
dotnet run --project src/RfidMiddleware.Host -c Release -- run --config appsettings.json
```

For repeated SDK lifecycle verification, use the direct native test command from section 3.

### Commission with BAFO, then operate permanently over LAN

BAFO USB-to-RS-232 is for initial commissioning and recovery only. The reader stores its own fixed private IPv4 configuration; it does not store the middleware server address. After commissioning, remove BAFO and let middleware initiate the normal TCP connection to the reader on port 1969.

With Ubuntu kept open, identify COM11 in Administrator PowerShell and attach only that adapter:

```powershell
usbipd list
usbipd bind --busid <COM11-BUSID> --force
usbipd attach --wsl --busid <COM11-BUSID>
```

Stop middleware before commissioning, then use the stable `/dev/serial/by-id/...` path:

```bash
export RFID_VendorReader__Transport=Serial
export RFID_VendorReader__SerialDevice='/dev/serial/by-id/REPLACE_WITH_BAFO_ID'
export RFID_VendorReader__Baud=115200
export RFID_VendorReader__Rs485Address=0

dotnet run --project src/RfidMiddleware.Host -c Release -- \
  reader network show --json --config src/RfidMiddleware.Host/appsettings.json

dotnet run --project src/RfidMiddleware.Host -c Release -- \
  reader network configure \
  --ip 192.168.10.20 --mask 255.255.255.0 --gateway 192.168.10.1 \
  --port 1969 --dns 192.168.10.2 \
  --config src/RfidMiddleware.Host/appsettings.json
```

The first configure command is preview-only. Review it, repeat it with `--apply`, wait for reboot, and verify over LAN:

```bash
dotnet run --project src/RfidMiddleware.Host -c Release -- \
  reader network verify --address 192.168.10.20 --port 1969 \
  --config src/RfidMiddleware.Host/appsettings.json
```

Power-cycle and verify again before removing BAFO. Backups are written to `CommissioningBackupDirectory`. If verification fails, reconnect over BAFO and reapply the saved previous values. Keep TCP 1969 private to the warehouse LAN/VLAN; Twingate is for backend and administration, not reader traffic. Use a switch or fiber/media converters when a copper Ethernet segment would exceed 100 metres.

## 7. Physical acceptance checklist

Do not enable warehouse operation until every item below passes with the actual reader, antennas, and configured alarm:

1. `reader test` reports connected and inventory running.
2. `manifest status` reports the exact backend version expected by Android.
3. An EPC present in the manifest is audited locally and produces no digital-output pulse.
4. An EPC absent from the manifest attempts the built-in buzzer, digital output 0, and digital output 1.
5. A malformed EPC, expired manifest, and completed/no-active state each alarm on observation.
6. Repeated reads of the same unauthorized EPC are limited by the three-second per-EPC cooldown.
7. Disconnecting PostgreSQL, backend networking, or the reader makes health critical but does not by itself pulse any alarm channel.
8. Backend loss continues using the cached unexpired manifest; after expiry, observed tags fail closed and alarm.
9. Revoke and authorize changes take effect only after the higher version is visible in `manifest status`.
10. Completion survives a middleware restart and no older manifest is reactivated.
11. Digital output 0 polarity and alarm duration are correct for the installed electrical wiring.
12. All intended antennas read tags across the real gate without unacceptable blind spots or cross-reads.

For the current two-tag acceptance manifest, authorize only:

```text
E28069950000400E45B4F53E
E280689400005010BF75955B
```

Authorize them through the mobile endpoint before performing the silent-pass test.

For local development only, the mock backend can publish the exact set in one higher immutable version:

```bash
curl -fsS -X POST \
  -H 'Content-Type: application/json' \
  --data '{
    "manifestId":"HW-COM11-ACCEPTANCE",
    "transportId":"HW-COM11-ACCEPTANCE",
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

Version `25` assumes the current version is `24`; always use a value higher than the latest locally applied/backend version.

The October 19, 2026 COM11 test also observed `E280689400004010BF78D96A`; it was correctly classified as unauthorized. Use `RFID_VendorReader__AlarmMode=BuzzerAndDigitalOutputs` for direct setup testing or `RFID_READER_ALARM_MODE=BuzzerAndDigitalOutputs` for Compose. After wiring acceptance, select `Buzzer`, `DigitalOutput0`, or `DigitalOutput1` as the final channel.

## 8. Troubleshooting

### Middleware says critical

```bash
dotnet run --project src/RfidMiddleware.Host -- status --config appsettings.json
dotnet run --project src/RfidMiddleware.Host -- db verify --config appsettings.json
dotnet run --project src/RfidMiddleware.Host -- diagnostics bundle --output artifacts/diagnostics.json --config appsettings.json
```

Check `reader.error`, `lastSystemError`, `lastManifestError`, dropped reads, manifest expiry, and reader connectivity. A fresh installation with no manifest is intentionally critical.

If any setup channel fails, the remaining channels are still attempted, authorization remains enforced, and scans continue, but health becomes critical. `alarm.error` lists each failed channel and SDK code. Error `-3034` can mean that an output is unsupported or not configured; the hardware team should verify reader I/O configuration and wiring.

### Native library does not load

```bash
file linux/x86_64/release/librfid.so
ldd linux/x86_64/release/librfid.so
export RFID_NATIVE_DIR="$PWD/linux/x86_64/release"
export LD_LIBRARY_PATH="$PWD/linux/x86_64/release"
```

Run on x86-64 Ubuntu/WSL. Do not use the Windows DLL with the Linux host.

### Serial device is missing

Run `usbipd list` again in Administrator PowerShell and attach the current bus ID. In Ubuntu, check `dmesg`, `/dev/ttyUSB*`, `/dev/ttyACM*`, membership in `dialout`, and whether another process owns the port:

```bash
lsof /dev/ttyUSB0
```

### Backend is unavailable

```bash
curl -fsS http://127.0.0.1:5080/healthz | jq
sudo systemctl status postgresql --no-pager
```

The middleware keeps using a cached valid manifest until it expires. Do not delete `rfid_edge` during an outage.

### Stop the development stack

Press `Ctrl+C` in the backend and middleware terminals. PostgreSQL can remain enabled. To stop it explicitly:

```bash
sudo systemctl stop postgresql
```

## 9. Production handoff

Build the Debian package:

```bash
bash scripts/build-deb.sh 0.1.0
```

Install and configure it on the Ubuntu edge host using the paths below:

- Application: `/usr/lib/rfid-middleware`
- Configuration: `/etc/rfid-middleware/appsettings.json`
- Secrets/environment: `/etc/rfid-middleware/environment`
- Control socket: `/run/rfid-middleware/control.sock`
- Logs: `journalctl -u rfid-middleware`

Use production database credentials, a production middleware bearer token, TLS for the backend, a stable serial path or fixed reader IP, and a least-privilege service account. Run the physical acceptance checklist and the intended soak duration before enabling the gate.
