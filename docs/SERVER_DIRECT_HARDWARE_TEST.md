# Direct Ubuntu Server Hardware Test

This procedure tests the middleware directly on an Ubuntu 24.04 x86-64 server, without Docker. It covers BAFO serial access, the exact two-tag manifest, live authorization, the temporary buzzer/output hardware-setup mode, local audit checks, and permanent LAN operation.

## 1. Test scope and expected behavior

The active acceptance manifest contains only:

| Inventory ID | EPC | Expected result |
|---|---|---|
| `HW-TAG-001` | `E28069950000400E45B4F53E` | Authorized, silent |
| `HW-TAG-002` | `E280689400005010BF75955B` | Authorized, silent |

Every other EPC is unauthorized and should cause one-second activation attempts on the reader's built-in buzzer, digital output 0, and digital output 1. Repeated observations of the same unauthorized EPC alarm at most once every three seconds. All observations remain in the server's local `rfid_edge` PostgreSQL database.

`BuzzerAndDigitalOutputs` is a temporary hardware-setup mode. Each channel is attempted independently, so an unconfigured output does not block the buzzer or other output. Any failed channel keeps alarm health critical and appears in `alarm.error`. After the hardware team accepts the wiring, configure only the final required channel: `Buzzer`, `DigitalOutput0`, or `DigitalOutput1`.

## 2. Install prerequisites

```bash
sudo apt-get update
sudo apt-get install -y dotnet-sdk-10.0 aspnetcore-runtime-8.0 \
  postgresql postgresql-client curl jq lsof usbutils
sudo systemctl enable --now postgresql
```

From the repository root, build and test:

```bash
dotnet restore RfidMiddleware.slnx
dotnet build RfidMiddleware.slnx --configuration Release --no-restore
dotnet test RfidMiddleware.slnx --configuration Release --no-build
```

## 3. Prepare the local development databases

These credentials are for an isolated server test only:

```bash
sudo -u postgres createuser --login rfid
sudo -u postgres psql -c "ALTER ROLE rfid WITH PASSWORD 'rfid-dev-password';"
sudo -u postgres createdb -O rfid rfid_backend
sudo -u postgres createdb -O rfid rfid_edge
```

Skip any object that already exists. Confirm both connections:

```bash
PGPASSWORD=rfid-dev-password psql -h 127.0.0.1 -U rfid -d rfid_backend -c 'select now();'
PGPASSWORD=rfid-dev-password psql -h 127.0.0.1 -U rfid -d rfid_edge -c 'select now();'
```

## 4. Connect and inspect BAFO

Pass the USB adapter into the Ubuntu VM, then find its stable name:

```bash
lsusb
sudo dmesg | tail -n 30
ls -l /dev/serial/by-id/ /dev/ttyUSB* 2>/dev/null
sudo usermod -aG dialout "$USER"
```

Log out and back in after adding `dialout`. Set the exact path returned by the server:

```bash
export RFID_SERIAL_DEVICE='/dev/serial/by-id/REPLACE_WITH_BAFO_ID'
export RFID_NATIVE_DIR="$PWD/linux/x86_64/release"
export LD_LIBRARY_PATH="$PWD/linux/x86_64/release"
export RFID_VendorReader__Transport=Serial
export RFID_VendorReader__SerialDevice="$RFID_SERIAL_DEVICE"
export RFID_VendorReader__Baud=115200
export RFID_VendorReader__Rs485Address=0
export RFID_VendorReader__AlarmMode=BuzzerAndDigitalOutputs
```

Ensure no other process owns the port, then read the reader configuration without changing it:

```bash
lsof "$RFID_SERIAL_DEVICE"
dotnet src/RfidMiddleware.Host/bin/Release/net8.0/rfid-middleware.dll \
  reader network show --json --config src/RfidMiddleware.Host/appsettings.json
```

## 5. Optional LAN commissioning

Obtain a dedicated private IPv4 address from the network administrator. Keep Ethernet and BAFO connected. Preview first:

```bash
dotnet src/RfidMiddleware.Host/bin/Release/net8.0/rfid-middleware.dll \
  reader network configure \
  --ip 192.168.10.20 \
  --mask 255.255.255.0 \
  --gateway 192.168.10.1 \
  --port 1969 \
  --dns 192.168.10.2 \
  --config src/RfidMiddleware.Host/appsettings.json
```

Review `current` and `proposed`. Repeat with `--apply` to create a backup, write, verify by serial read-back, and reboot. Then verify over Ethernet:

```bash
dotnet src/RfidMiddleware.Host/bin/Release/net8.0/rfid-middleware.dll \
  reader network verify --address 192.168.10.20 --port 1969 \
  --config src/RfidMiddleware.Host/appsettings.json
```

Power-cycle and verify again. Remove BAFO only after persistence is proven.

## 6. Start the mock backend

Open terminal 1:

```bash
cd ~/rrligthing-rfid-middleware
export ConnectionStrings__MockServer='Host=127.0.0.1;Port=5432;Database=rfid_backend;Username=rfid;Password=rfid-dev-password'
export URLS=http://127.0.0.1:5080
dotnet tools/RfidMiddleware.MockServer/bin/Release/net8.0/RfidMiddleware.MockServer.dll
```

Confirm it is ready:

```bash
curl -fsS http://127.0.0.1:5080/healthz | jq
```

## 7. Publish exactly two authorized tags

Read the current version:

```bash
curl -fsS -H 'Authorization: Bearer dev-token' \
  http://127.0.0.1:5080/api/v1/middleware/WH-A-GATE-01/manifest | jq
```

Choose a `version` greater than both the backend result and the latest local middleware version. The example uses version 25:

```bash
curl -fsS -X POST \
  -H 'Content-Type: application/json' \
  --data '{
    "manifestId":"HW-SERVER-ACCEPTANCE",
    "transportId":"HW-SERVER-ACCEPTANCE",
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

The `/admin` endpoint is development-only. Production manifests must be created through the authenticated backend/mobile workflow.

## 8. Start middleware directly over BAFO

Open terminal 2:

```bash
cd ~/rrligthing-rfid-middleware
export RFID_NATIVE_DIR="$PWD/linux/x86_64/release"
export LD_LIBRARY_PATH="$PWD/linux/x86_64/release"
export RFID_API_TOKEN=dev-token
export RFID_ReaderMode=Vendor
export RFID_VendorReader__Transport=Serial
export RFID_VendorReader__SerialDevice='/dev/serial/by-id/REPLACE_WITH_BAFO_ID'
export RFID_VendorReader__Baud=115200
export RFID_VendorReader__Rs485Address=0
export RFID_VendorReader__AlarmMode=BuzzerAndDigitalOutputs
export RFID_VendorReader__AlarmPulseMilliseconds=1000
dotnet src/RfidMiddleware.Host/bin/Release/net8.0/rfid-middleware.dll \
  run --config src/RfidMiddleware.Host/appsettings.json
```

## 9. Inspect readiness and perform scans

Open terminal 3:

```bash
cd ~/rrligthing-rfid-middleware
APP='src/RfidMiddleware.Host/bin/Release/net8.0/rfid-middleware.dll'
CONFIG='src/RfidMiddleware.Host/appsettings.json'

dotnet "$APP" status --config "$CONFIG"
dotnet "$APP" manifest status --config "$CONFIG"
dotnet "$APP" reader watch --duration 15 --config "$CONFIG"
```

Confirm:

1. `reader.connected` and `reader.inventoryRunning` are `true`.
2. The active manifest is the expected higher version with two items.
3. Scan each authorized EPC: both remain silent.
4. Scan a third EPC: the built-in buzzer sounds and outputs 0 and 1 are commanded active for about one second.
5. Leave the third EPC in range: alarm activation is limited by the three-second cooldown.
6. `reads.dropped` remains `0`.

For each output, connect a meter or vendor-approved test load according to the manufacturer wiring diagram. Do not apply external voltage or a load beyond the reader's ratings. If an output is not configured, the other alarm channels must still be attempted and inventory must remain running; `status` will remain critical until every configured channel succeeds.

Inspect the local audit directly:

```bash
export PGPASSWORD=rfid-dev-password
psql -h 127.0.0.1 -U rfid -d rfid_edge -P pager=off -c \
  'select created_at,epc,result,manifest_version,read_count from scan_event order by created_at desc limit 25;'
```

The two listed EPCs must show `Authorized`; every other EPC must show `Unauthorized` or `InvalidEpc`.

## 10. Switch to permanent LAN operation

Stop middleware with `Ctrl+C`, remove BAFO, and set:

```bash
export RFID_ReaderMode=Vendor
export RFID_VendorReader__Transport=Tcp
export RFID_VendorReader__Address=192.168.10.20
export RFID_VendorReader__Port=1969
export RFID_VendorReader__AlarmMode=BuzzerAndDigitalOutputs
```

Check the private endpoint and start middleware again:

```bash
timeout 3 bash -c '</dev/tcp/192.168.10.20/1969' && echo 'reader reachable'
dotnet src/RfidMiddleware.Host/bin/Release/net8.0/rfid-middleware.dll \
  reader network verify --address 192.168.10.20 --port 1969 \
  --config src/RfidMiddleware.Host/appsettings.json
dotnet src/RfidMiddleware.Host/bin/Release/net8.0/rfid-middleware.dll \
  run --config src/RfidMiddleware.Host/appsettings.json
```

Repeat the authorized, unauthorized, cooldown, health, and audit checks over TCP.

## 11. Installed service test

For a packaged server, configure `/etc/rfid-middleware/appsettings.json`:

```json
{
  "ReaderMode": "Vendor",
  "VendorReader": {
    "Transport": "Tcp",
    "Address": "192.168.10.20",
    "Port": 1969,
    "AlarmMode": "BuzzerAndDigitalOutputs",
    "AlarmPulseMilliseconds": 1000
  }
}
```

Then:

```bash
sudo systemctl restart rfid-middleware
sudo systemctl status rfid-middleware --no-pager
sudo journalctl -u rfid-middleware -f
sudo -u rfid-middleware /usr/lib/rfid-middleware/rfid-middleware \
  status --config /etc/rfid-middleware/appsettings.json
```

## 12. Docker server test

Set `.env.production`:

```text
RFID_READER_MODE=Vendor
RFID_READER_TRANSPORT=Tcp
RFID_READER_ADDRESS=192.168.10.20
RFID_READER_PORT=1969
RFID_READER_ALARM_MODE=BuzzerAndDigitalOutputs
```

Start only the production base plus TCP override:

```bash
docker compose -p rfid-prod --env-file .env.production \
  -f docker-compose.yml -f docker-compose.tcp.yml up -d --build
docker compose -p rfid-prod --env-file .env.production \
  -f docker-compose.yml -f docker-compose.tcp.yml ps
docker compose -p rfid-prod exec middleware \
  /app/rfid-middleware status --config /app/appsettings.json
```

Port 1969 is outbound from middleware to the reader. Never publish it through Docker or a public firewall.

## 13. Select the final alarm channel

After hardware setup and wiring acceptance, choose the channel required for production:

```text
Built-in buzzer only:         VendorReader.AlarmMode = Buzzer
Digital output 0 only:       VendorReader.AlarmMode = DigitalOutput0
Digital output 1 only:       VendorReader.AlarmMode = DigitalOutput1
Docker equivalent:           RFID_READER_ALARM_MODE=<selected value>
```

Restart middleware and verify sound/output polarity, wiring, pulse duration, cooldown, and critical health behavior. Do not switch production merely because the SDK call returns success; physically confirm the selected alarm actuates.

## 14. Stop and clean up

Stop middleware and mock backend with `Ctrl+C`. Leave PostgreSQL running if further tests are planned. For serial tests, confirm the port is released:

```bash
lsof "$RFID_SERIAL_DEVICE"
```

Do not delete `rfid_edge` when its audit evidence or cached manifest is needed.
