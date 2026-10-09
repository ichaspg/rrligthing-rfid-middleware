# Full WSL, PostgreSQL, and RFID Reader Test Handbook

## 1. What this test proves

This procedure verifies the complete physical workflow:

```text
backend publishes manifest v18 with Tag A and Tag B
                    ↓
middleware downloads v18 and caches it in local PostgreSQL
                    ↓
Tag A keluar → AUTHORIZED
                    ↓
Tag A balik → AUTHORIZED under the same v18
                    ↓
Tag A is placed back in storage
                    ↓
mobile app sends a revoke HTTP request to the backend
                    ↓
backend atomically creates manifest v19 without Tag A
                    ↓
middleware downloads and activates v19
                    ↓
Tag A scanned again → UNAUTHORIZED + alarm
Tag B scanned again → AUTHORIZED
```

The middleware does not upload scan data. EPC, antenna, RSSI, read count, and validation outcomes are written only to the local `rfid_edge` PostgreSQL database.

Two databases are used:

| Database | Owner | Contents |
|---|---|---|
| `rfid_edge` | Middleware | Cached manifests and local-only scan audit |
| `rfid_backend` | Backend | Published manifests, mobile revocation audit, identities and test faults |

They share one PostgreSQL container for development convenience. In production, `rfid_edge` should run on the middleware computer while `rfid_backend` runs on the central server.

## 2. Test identities and hardware

- Middleware ID: `WH-A-GATE-01`
- Manifest: `REAL-TAGS-2026-001`
- Initial version: `18`
- Windows serial port: COM4
- usbipd BUSID: `4-15`
- Expected WSL device: `/dev/ttyUSB0`
- Reader ID: `2609030071`
- Baud: `115200`
- RS-485 address: `0`
- Tag A: `E280689400004010BF743D5C`
- Tag B: `E2806915000060068C95C54B`

Tag A is revoked in this test. Tag B remains authorized.

## 3. Install and verify prerequisites

Inside Ubuntu WSL, the application requires the .NET 8 SDK:

```bash
sudo apt update
sudo apt install -y dotnet-sdk-8.0 curl jq postgresql-client
dotnet --list-sdks
dotnet --list-runtimes
```

The output should contain an 8.0 SDK and both `Microsoft.NETCore.App 8.0` and `Microsoft.AspNetCore.App 8.0`.

Install Docker Desktop on Windows and enable:

```text
Docker Desktop → Settings → Resources → WSL Integration → Ubuntu
```

Verify inside WSL:

```bash
docker compose version
```

Install `usbipd-win` on Windows if `usbipd` is not available.

## 4. Prepare environment variables

In WSL:

```bash
cd "/mnt/d/Essential/WIT/RR Lighting RFID Middleware"
cp .env.example .env
set -a
source .env
set +a
```

The repository ignores `.env`. Do not commit production passwords or tokens. Confirm the important values without printing passwords:

```bash
printf 'reader=%s transport=%s device=%s\n' \
  "$RFID_VendorReader__ReaderId" "$RFID_VendorReader__Transport" "$RFID_VendorReader__SerialDevice"
printf 'native=%s\n' "$RFID_NATIVE_DIR"
```

Every new WSL terminal used in this handbook must load the file again:

```bash
cd "/mnt/d/Essential/WIT/RR Lighting RFID Middleware"
set -a; source .env; set +a
```

## 5. Create the PostgreSQL databases

For a new test environment:

```bash
docker compose up -d postgres
docker compose ps
```

Wait until the container is `healthy`. Verify both databases:

```bash
docker compose exec postgres psql -U rfid -d postgres -c '\l'
```

You must see `rfid_backend` and `rfid_edge`.

If this project previously used the older single-database container, the existing Docker volume will prevent the initialization script from creating `rfid_edge`. For disposable development data only, recreate the volume:

```bash
docker compose down -v
docker compose up -d postgres
```

Warning: `down -v` permanently removes the PostgreSQL data in this project's Docker volume. Never run it against production data.

Check each connection:

```bash
psql "$RFID_DatabaseConnectionString" -c 'select current_database(), current_user;'
psql "$ConnectionStrings__MockServer" -c 'select current_database(), current_user;'
```

Expected databases are `rfid_edge` and `rfid_backend`, respectively.

## 6. Build and run automated tests

```bash
dotnet restore RfidMiddleware.slnx
dotnet build RfidMiddleware.slnx -c Release --no-restore
dotnet test RfidMiddleware.slnx -c Release --no-build
```

Because `.env` defines `RFID_TEST_POSTGRES`, the PostgreSQL persistence and end-to-end tests create isolated temporary schemas in `rfid_edge`, run their checks, and remove those schemas afterward.

## 7. Start the backend server

Open WSL terminal 1 and load `.env`:

```bash
cd "/mnt/d/Essential/WIT/RR Lighting RFID Middleware"
set -a; source .env; set +a

dotnet run --project ./tools/RfidMiddleware.MockServer/RfidMiddleware.MockServer.csproj \
  -c Release -- --reset --seed-count 100
```

Keep this terminal running. Verify it from another terminal:

```bash
curl -fsS http://127.0.0.1:5080/healthz | jq
```

## 8. Activate the real-tag manifest

In WSL terminal 2:

```bash
cd "/mnt/d/Essential/WIT/RR Lighting RFID Middleware"
set -a; source .env; set +a

curl -fsS -X POST \
  http://127.0.0.1:5080/admin/middleware/WH-A-GATE-01/manifest/activate \
  -H 'Content-Type: application/json' \
  --data-binary @fixtures/real-tags.json | jq
```

Verify the server manifest:

```bash
curl -fsS \
  http://127.0.0.1:5080/api/v1/middleware/WH-A-GATE-01/manifest \
  -H 'Authorization: Bearer dev-token' | jq
```

Expected: manifest `REAL-TAGS-2026-001`, version 18, with Tag A and Tag B.

## 9. Attach Windows COM4 to WSL

Open Windows PowerShell as Administrator:

```powershell
usbipd list
usbipd bind --busid 4-15
usbipd attach --wsl --busid 4-15
```

`bind` is required only the first time. If the BUSID changes, use the value displayed by `usbipd list`.

Back in WSL:

```bash
ls -l /dev/ttyUSB0
id
```

If your user is not in `dialout`:

```bash
sudo usermod -aG dialout "$USER"
```

Then run this in Windows PowerShell:

```powershell
wsl --shutdown
```

Reopen WSL, attach BUSID `4-15` again, and reload `.env`.

## 10. Check the vendor libraries and serial port

In WSL:

```bash
cd "/mnt/d/Essential/WIT/RR Lighting RFID Middleware"
set -a; source .env; set +a

ls -l "$RFID_NATIVE_DIR/librfid.so"
ls -l "$RFID_NATIVE_DIR/librfid_core.so"
ls -l /dev/ttyUSB0
file "$RFID_NATIVE_DIR/librfid.so"
```

The library must be Linux x86-64, and the serial device must exist. Stop any Windows program that has COM4 open because only one process can own the serial port.

## 11. Start the middleware and reader inventory

Open WSL terminal 3:

```bash
cd "/mnt/d/Essential/WIT/RR Lighting RFID Middleware"
set -a; source .env; set +a

dotnet run --project ./src/RfidMiddleware.Host/RfidMiddleware.Host.csproj \
  -c Release -- run --config appsettings.json
```

Keep this terminal running. Startup performs these actions:

1. Connect to local PostgreSQL database `rfid_edge`.
2. Create or migrate local tables.
3. Restore a cached active manifest if present.
4. Request the latest manifest from the backend.
5. Cache and atomically activate the manifest locally.
6. Load the native reader libraries.
7. Open `/dev/ttyUSB0`.
8. Connect to the reader and start inventory automatically.

There is no separate start-scanning command.

## 12. Verify middleware health

Open WSL terminal 4 and load `.env`:

```bash
cd "/mnt/d/Essential/WIT/RR Lighting RFID Middleware"
set -a; source .env; set +a

dotnet run --project ./src/RfidMiddleware.Host/RfidMiddleware.Host.csproj \
  -c Release -- status --config appsettings.json

dotnet run --project ./src/RfidMiddleware.Host/RfidMiddleware.Host.csproj \
  -c Release -- manifest status --config appsettings.json

dotnet run --project ./src/RfidMiddleware.Host/RfidMiddleware.Host.csproj \
  -c Release -- db verify --config appsettings.json
```

Expected:

- Reader connected and inventory running.
- Manifest version 18 active.
- Database verification result `ok`.
- Scan data policy `LOCAL_ONLY`.

## 13. Test authorize, keluar, and balik

Present Tag A to antenna 8:

```text
E280689400004010BF743D5C
```

This represents `keluar`. Expected result: `AUTHORIZED`.

Remove the tag from the read zone, wait at least three seconds, and present it again. This represents `balik`. Expected result: `AUTHORIZED` under the same manifest version 18.

Return Tag A physically to storage. Do not send the revoke until the physical storage action is complete.

Inspect recent reads:

```bash
dotnet run --project ./src/RfidMiddleware.Host/RfidMiddleware.Host.csproj \
  -c Release -- reader watch --duration 5 --config appsettings.json
```

Inspect local audit counts:

```bash
dotnet run --project ./src/RfidMiddleware.Host/RfidMiddleware.Host.csproj \
  -c Release -- audit stats --config appsettings.json
```

## 14. Simulate the mobile revoke request

After Tag A is stored:

```bash
curl -fsS -X POST \
  http://127.0.0.1:5080/api/v1/mobile/middleware/WH-A-GATE-01/manifests/REAL-TAGS-2026-001/revoke \
  -H 'Authorization: Bearer dev-token' \
  -H 'Content-Type: application/json' \
  -d '{
    "requestId": "return-2609030071-0001",
    "epcs": ["E280689400004010BF743D5C"],
    "reason": "RETURNED_TO_STORAGE",
    "requestedBy": "mobile-test-user",
    "occurredAt": "2026-10-09T03:30:00Z"
  }' | jq
```

Expected: previous version 18, new version 19, and `alreadyApplied: false`.

Send the same request again. Expected: version remains 19 and `alreadyApplied: true`.

## 15. Synchronize and verify revocation

The middleware polls every five seconds. Force an immediate request if desired:

```bash
dotnet run --project ./src/RfidMiddleware.Host/RfidMiddleware.Host.csproj \
  -c Release -- manifest sync --config appsettings.json
sleep 1
dotnet run --project ./src/RfidMiddleware.Host/RfidMiddleware.Host.csproj \
  -c Release -- manifest status --config appsettings.json
```

Expected: version 19.

Scan Tag A again. Expected: `UNAUTHORIZED` and relay/buzzer activation.

Scan Tag B:

```text
E2806915000060068C95C54B
```

Expected: `AUTHORIZED` and no unauthorized alarm.

## 16. Prove where data is stored

Inspect local middleware data:

```bash
psql "$RFID_DatabaseConnectionString" -c \
  "select event_id,manifest_version,epc,result,antenna_ids,storage_policy,last_seen_at from scan_event order by created_at desc limit 20;"
```

Every row must show `LocalOnly`.

Inspect locally cached manifests:

```bash
psql "$RFID_DatabaseConnectionString" -c \
  "select manifest_id,version,status,item_count,activated_at from manifest order by version;"
```

Inspect backend manifests and revoke audit:

```bash
psql "$ConnectionStrings__MockServer" -c \
  "select manifest_id,version,status,item_count from manifest order by version;"

psql "$ConnectionStrings__MockServer" -c \
  "select request_id,previous_version,new_version,revoked_epcs,reason from revocation_audit;"
```

Prove that the backend has no scan table:

```bash
psql "$ConnectionStrings__MockServer" -c \
  "select to_regclass('public.scan_event') as backend_scan_table, to_regclass('public.received_event') as backend_received_event_table;"
```

Both values must be null.

## 17. Restart and offline tests

### Middleware restart

Stop only the middleware with `Ctrl+C`, then start it again. It must restore version 19 from `rfid_edge`, connect to the reader, and keep Tag A unauthorized.

### Backend outage

Stop the backend with `Ctrl+C` but leave PostgreSQL and middleware running. Reader validation must continue from the locally cached manifest. No scan upload retries should appear because scan upload does not exist.

### Local PostgreSQL outage

Stop PostgreSQL:

```bash
docker compose stop postgres
```

The middleware must fail because it cannot durably store its manifest/audit. Restart PostgreSQL and then restart the middleware:

```bash
docker compose start postgres
```

### Expired manifest

Use the backend fault profile or an intentionally short expiry. Once the hard expiry passes, validation must fail closed and activate the system alarm.

## 18. Troubleshooting

### `librfid.so` path points to `/mnt/c/Users/PC`

Reload `.env`. The correct path begins with:

```text
/mnt/d/Essential/WIT/RR Lighting RFID Middleware/linux/x86_64/release
```

### `/dev/ttyUSB0` is missing

Run `usbipd list` and `usbipd attach --wsl --busid 4-15` again from Administrator PowerShell.

### Permission denied on `/dev/ttyUSB0`

Add the WSL user to `dialout`, shut down WSL, reopen it, and reattach the USB device.

### PostgreSQL database does not exist

For disposable development data, recreate the Compose volume as described in section 5. For retained data, create `rfid_edge` manually instead of deleting the volume.

### Password authentication failed

Confirm `.env` matches `docker-compose.yml`. If the Docker volume was created with a different password, changing Compose does not change the existing database password.

### Reader connects but no tags appear

Check reader baud, RS-485 address, antenna enablement, antenna cable, reader power, and whether another program owns COM4.

## 19. Safe shutdown

Stop the middleware first with `Ctrl+C`, then stop the backend. Stop PostgreSQL without deleting data:

```bash
docker compose stop postgres
```

Detach COM4 from Administrator PowerShell if required:

```powershell
usbipd detach --busid 4-15
```

Do not add `-v` to routine shutdown commands because that deletes the PostgreSQL test volume.
