# RFID Transport Validation Middleware

For the complete HTTP contract, request and response schemas, status codes, idempotency rules, and local Unix-socket endpoints, use [docs/API.md](docs/API.md). For a single end-to-end handover covering installation, every CLI command, Docker and systemd deployment, WSL/VirtualBox USB, BAFO commissioning, permanent LAN operation, alarm outputs, hardware acceptance, mobile integration, and troubleshooting, use [docs/COMPLETE_OPERATIONS_AND_INTEGRATION_GUIDE.md](docs/COMPLETE_OPERATIONS_AND_INTEGRATION_GUIDE.md).

Reader-initiated TCP has two standalone deployment runbooks:

- [Fresh Ubuntu server setup](docs/FRESH_SERVER_READER_INITIATED_SETUP.md)
- [Temporary public VPS setup](docs/VPS_READER_INITIATED_SETUP.md)
- [Permanent local production server setup](docs/LOCAL_PRODUCTION_READER_INITIATED_SETUP.md)

.NET 8 LTS command-line service for Ubuntu 24.04 x86-64. It validates RFID EPC observations against a durable mobile-controlled manifest, operates offline, and raises the configured reader alarm only for tags that cannot be authorized. Scan data remains in the middleware's local PostgreSQL database and is never uploaded to the server.

## Purpose

The middleware sits between an RFID reader and a transport-management backend. It downloads a versioned authorization manifest, validates tags locally with low latency, and operates during backend outages. Temporary hardware-setup mode attempts the built-in buzzer plus digital output 1 for every unauthorized observation; technical faults are exposed through health, metrics, and logs.

The supported operational flow is:

```text
mobile authorizes EPC → backend publishes manifest version → middleware synchronizes
                     → listed tag scan passes silently
mobile revokes EPC   → backend publishes next version → middleware synchronizes
                     → later tag scan raises the unauthorized alarm
mobile completes batch → middleware clears active authorization
```

## Important data policy

- The middleware downloads manifests from the backend over HTTP/HTTPS.
- Mobile clients send revocations to the backend, not directly to the reader middleware.
- EPC scan observations are stored only in the middleware's local `rfid_edge` PostgreSQL database.
- The backend has no scan-event upload endpoint or scan-event table.
- Development status and reader-watch commands use a permission-restricted local Unix socket.

## Main capabilities

- Vendor SDK integration for TCP, RS-232/RS-485 serial, and the SDK native mock.
- Managed fake reader for deterministic development and load testing.
- EPC normalization, multi-antenna aggregation, and configurable duplicate window.
- Immutable, monotonically increasing manifest versions with atomic activation.
- Fail-closed validation for missing or expired manifests and persistence failures.
- The configured alarm pulses only for tags that are unauthorized, invalid, or observed without a valid manifest. `BuzzerAndDigitalOutput1` is the hardware-setup default and attempts the buzzer and output 1 independently. Output 0 is intentionally unused.
- Critical health, metrics, and structured logs for reader, database, queue, and synchronization faults.
- PostgreSQL persistence for both the local middleware and test backend.
- Idempotent mobile revocation requests with a durable backend audit.
- Unix-socket operational CLI, health endpoint, metrics, diagnostics, DEB packaging, and systemd unit.
- Native ABI probe, unit/integration tests, load tests, and stability scripts.

## Architecture

```text
Mobile app ── authorize/revoke/complete ──► backend API ──► rfid_backend PostgreSQL
                                           │
                                     active manifest
                                           │ HTTP GET + ETag
                                           ▼
RFID reader ── native SDK ──► middleware ──► rfid_edge PostgreSQL
                                  │             local scans only
                                  └──► buzzer + digital output 1
```

Development uses two databases in one PostgreSQL container:

| Database | Purpose |
|---|---|
| `rfid_edge` | Middleware manifest cache, recovery state, and local scan audit |
| `rfid_backend` | Published manifests, middleware identity, faults, and revocation audit |

## Development environment

The supported development environment is Ubuntu 24.04 or WSL2 Ubuntu 24.04 with the .NET 10 SDK plus the .NET/ASP.NET Core 8 runtime. The application itself targets .NET 8 LTS; the newer SDK is required for the `.slnx` solution format. Install `build-essential`, `postgresql-client`, `valgrind`, `strace`, `lsof`, and `patchelf`. The vendor libraries live in `linux/x86_64/release` and are copied to `native/` during build and publish. WSL supports managed tests, the SDK mock, TCP readers, and USB serial readers passed through with `usbipd`; final relay and antenna acceptance must still be performed with the physical hardware.

Copy and load the development environment, then start PostgreSQL:

```bash
cp .env.example .env
set -a; source .env; set +a
docker compose up -d postgres
```

Start the test backend:

```bash
dotnet run --project tools/RfidMiddleware.MockServer -c Release -- --reset --seed-count 100
```

Start the middleware in another terminal after loading `.env` again:

```bash
set -a; source .env; set +a
dotnet run --project src/RfidMiddleware.Host -c Release -- run --config appsettings.json
```

In another terminal:

```bash
dotnet run --project src/RfidMiddleware.Host -- status --config appsettings.json
dotnet run --project src/RfidMiddleware.Host -- reader watch --duration 5 --config appsettings.json
```

Set `ReaderMode` to `Vendor` and select `NativeMock`, `Tcp`, `TcpListener`, or `Serial` under `VendorReader`. `Tcp` makes the middleware connect to the reader; `TcpListener` waits for one reader to connect to the middleware. Use stable `/dev/serial/by-id/...` paths for serial readers.

The development control socket is under `/tmp` because Unix sockets cannot be created on a Windows-mounted WSL path. The production unit uses `/run/rfid-middleware/control.sock` with restricted permissions.

The mock backend supports manifest activation and fault injection. See `fixtures/README.md` and `fixtures/scenarios.json` for deterministic payloads, fault names, and the 100,000-EPC reset command.
Both persistence layers use PostgreSQL. `rfid_edge` is private to the middleware and stores its cached manifest plus local-only scan audit. `rfid_backend` stores server manifests and revocation audit. Only manifest requests are sent from middleware to server; EPC scan observations are not uploaded.

## Docker

The image requires a Linux x86-64 Docker host. It contains the Ubuntu 24.04-compatible vendor libraries and runs as a non-root user.

### Development stack with mock backend

The development override adds the mock backend, creates both PostgreSQL databases, generates an active manifest, and uses the managed fake reader:

```bash
docker compose -p rfid-dev \
  --env-file docker/dev.env \
  -f docker-compose.yml \
  -f docker-compose.dev.yml \
  up -d --build
```

Inspect it with:

```bash
docker compose -p rfid-dev --env-file docker/dev.env -f docker-compose.yml -f docker-compose.dev.yml ps
docker compose -p rfid-dev --env-file docker/dev.env -f docker-compose.yml -f docker-compose.dev.yml logs -f middleware
docker compose -p rfid-dev --env-file docker/dev.env -f docker-compose.yml -f docker-compose.dev.yml \
  exec middleware /app/rfid-middleware status --config /app/appsettings.json
docker compose -p rfid-dev --env-file docker/dev.env -f docker-compose.yml -f docker-compose.dev.yml \
  exec middleware /app/rfid-middleware manifest status --config /app/appsettings.json
docker compose -p rfid-dev --env-file docker/dev.env -f docker-compose.yml -f docker-compose.dev.yml \
  exec middleware /app/rfid-middleware audit stats --config /app/appsettings.json
```

The mock mobile API is available only on `http://127.0.0.1:5080`. Change the generated manifest size with `RFID_DEV_SEED_COUNT`. Stop the development stack without deleting its database:

```bash
docker compose -p rfid-dev --env-file docker/dev.env -f docker-compose.yml -f docker-compose.dev.yml down
```

Add `--volumes` only when the development data should be permanently deleted.

### Real reader in Docker

For a TCP reader, append the TCP override after the development override:

```bash
export RFID_READER_ADDRESS=192.168.1.223
docker compose -p rfid-dev \
  --env-file docker/dev.env \
  -f docker-compose.yml -f docker-compose.dev.yml -f docker-compose.tcp.yml \
  up -d --build
```

For reader-initiated TCP, append the listener override. `RFID_READER_PORT` is the middleware listening port in this mode:

```bash
export RFID_READER_PORT=1969
docker compose -p rfid-prod \
  --env-file .env.production \
  -f docker-compose.yml -f docker-compose.listener.yml \
  up -d --build
```

The listener accepts one reader, reports critical health while none is connected, rejects additional simultaneous readers, and resumes inventory automatically after reconnection. The override publishes a raw, unauthenticated vendor-protocol port. On a VPS, allow it only from the test site's stable public IP in both the cloud and host firewalls.

For USB serial on Ubuntu, attach the device to the VM first, then use its stable path and the host `dialout` group ID:

```bash
export RFID_SERIAL_DEVICE=/dev/serial/by-id/REPLACE_WITH_READER_ID
export RFID_DIALOUT_GID="$(getent group dialout | cut -d: -f3)"
docker compose -p rfid-dev \
  --env-file docker/dev.env \
  -f docker-compose.yml -f docker-compose.dev.yml -f docker-compose.serial.yml \
  up -d --build
```

The serial override maps the selected device to `/dev/rfid-reader` in the container. Unauthorized observations pulse the configured alarm; setup mode attempts the built-in buzzer and digital output 1.

### Commission a reader over BAFO USB-to-RS-232

`reader network` commissioning configures the reader's own fixed LAN address. `reader client` commissioning separately configures the middleware address to which the reader connects in `TcpListener` mode. Neither setting is the backend API address.

Use a network-admin-approved static address outside the dynamic DHCP pool, or reserve that address for the reader. Connect Ethernet and the BAFO adapter at the same time. The BAFO connection is temporary and should be made beside the reader; do not use RS-232 for the permanent long-distance link.

Set the stable BAFO device path and Ubuntu `dialout` group, then stop middleware so the commissioning command can acquire the exclusive reader lock:

```bash
export RFID_SERIAL_DEVICE=/dev/serial/by-id/REPLACE_WITH_BAFO_ID
export RFID_DIALOUT_GID="$(getent group dialout | cut -d: -f3)"

docker compose -p rfid-prod --env-file .env.production \
  -f docker-compose.yml -f docker-compose.serial.yml \
  stop middleware

docker compose -p rfid-prod --env-file .env.production \
  -f docker-compose.yml -f docker-compose.serial.yml \
  run --rm --no-deps middleware \
  reader network show --json --config /app/appsettings.json
```

Preview the proposed network change first. Without `--apply`, the command reads the current configuration but does not write, back up, or reboot:

```bash
docker compose -p rfid-prod --env-file .env.production \
  -f docker-compose.yml -f docker-compose.serial.yml \
  run --rm --no-deps middleware \
  reader network configure \
  --ip 192.168.10.20 \
  --mask 255.255.255.0 \
  --gateway 192.168.10.1 \
  --port 1969 \
  --dns 192.168.10.2 \
  --config /app/appsettings.json
```

Review the `current` and `proposed` values, then repeat the same command with `--apply`. Apply mode writes a timestamped backup under `/var/lib/rfid-middleware/commissioning`, writes the reader, reads the settings back over serial, and reboots only after an exact match. Any validation, SDK, lock, or read-back error returns a non-zero process exit code.

To configure reader-initiated mode, first inspect and preview the stored TCP report target:

```bash
docker compose -p rfid-prod --env-file .env.production \
  -f docker-compose.yml -f docker-compose.serial.yml \
  run --rm --no-deps middleware \
  reader client show --json --config /app/appsettings.json

docker compose -p rfid-prod --env-file .env.production \
  -f docker-compose.yml -f docker-compose.serial.yml \
  run --rm --no-deps middleware \
  reader client configure --server-ip 203.0.113.20 --port 1969 \
  --config /app/appsettings.json
```

Review the preview, rerun it with `--apply`, and power-cycle the reader. The command preserves UDP reporting, backs up both report options, verifies exact read-back, and only then reboots. Use `reader client disable` with the same preview/`--apply` workflow to turn off TCP client reporting without erasing the stored target.

After reboot, verify the LAN endpoint while middleware remains stopped:

```bash
export RFID_READER_ADDRESS=192.168.10.20

docker compose -p rfid-prod --env-file .env.production \
  -f docker-compose.yml -f docker-compose.tcp.yml \
  run --rm --no-deps middleware \
  reader network verify \
  --address "$RFID_READER_ADDRESS" --port 1969 \
  --config /app/appsettings.json
```

Power-cycle the reader and repeat verification to prove the setting is persistent. Then remove BAFO, set `RFID_READER_ADDRESS` and `RFID_READER_PORT=1969` in `.env.production`, and start the production TCP stack:

```bash
docker compose -p rfid-prod --env-file .env.production \
  -f docker-compose.yml -f docker-compose.tcp.yml \
  up -d --build
```

If LAN verification fails, keep BAFO attached. Open the latest JSON file in `/var/lib/rfid-middleware/commissioning`, copy its `settings` values into `reader network configure`, preview the rollback without `--apply`, then repeat it with `--apply`. Run serial `show`, LAN `verify`, and the power-cycle check again before leaving the installation.

For outbound `Tcp`, the reader and middleware must have direct private routing on the warehouse LAN/VLAN and reader port 1969 must never be public. For `TcpListener`, keep the listener private in production. A temporary VPS test may publish it only with a source-IP allowlist; the protocol has no application authentication or encryption. Twingate remains on the middleware-to-backend and administrative paths. Copper Ethernet is limited to 100 metres per segment; use a switch or fiber/media converters for a longer path.

### Ship without the mock backend

Copy the production template and replace every placeholder:

```bash
cp .env.production.example .env.production
chmod 600 .env.production
```

Stop the development project, then start a separately named production project using **only** the base Compose file:

```bash
docker compose -p rfid-dev --env-file docker/dev.env -f docker-compose.yml -f docker-compose.dev.yml down
docker compose -p rfid-prod --env-file .env.production -f docker-compose.yml config
docker compose -p rfid-prod --env-file .env.production -f docker-compose.yml up -d --build
```

Never include `docker-compose.dev.yml` in a shipment. When that file is omitted, no mock-backend service, development token, generated backend manifest, or `rfid_backend` database is created. The middleware contacts only `RFID_SERVER_BASE_URL` using `RFID_API_TOKEN`. Verify the rendered `config` output contains the intended real URL, middleware ID, reader address, and no `mock-backend` service before starting.

For production serial mode, append `-f docker-compose.serial.yml`; for TCP mode, the base file already defaults to `Vendor`/`Tcp`, and `docker-compose.tcp.yml` can be appended to require an explicit reader address.

## Real hardware quick reference

For the complete non-Docker Ubuntu server procedure, including the exact two-tag manifest, BAFO commissioning, serial/TCP execution, buzzer testing, SQL audit checks, systemd, Docker verification, and mobile handover, use [docs/COMPLETE_OPERATIONS_AND_INTEGRATION_GUIDE.md](docs/COMPLETE_OPERATIONS_AND_INTEGRATION_GUIDE.md).

The committed native SDK is the Linux x86-64 build under `linux/x86_64/release`. For a USB serial reader, first obtain its current bus ID instead of assuming it is permanent. Windows exposes the adapter to WSL using `usbipd`, after which the middleware normally uses `/dev/ttyUSB0` or a stable `/dev/serial/by-id/...` path.

```powershell
usbipd list
usbipd bind --busid <BUSID>
usbipd attach --wsl --busid <BUSID>
```

The current physical acceptance tags are:

- `E28069950000400E45B4F53E`
- `E280689400005010BF75955B`

Nearby EPC `E280689400004010BF78D96A` was intentionally excluded and correctly detected as unauthorized during the October 19, 2026 COM11 test.

The tested VM-6x V9.4 reader (`SW 1.1.31-GD`) returned SDK error `-3034` (`The relay is not supported`) for digital output 0. Output 0 is therefore intentionally unused. `VendorReader.AlarmMode` defaults to `BuzzerAndDigitalOutput1`, which independently attempts the built-in buzzer and digital output 1. A failure on one channel does not prevent the other channel, scan auditing, or continued inventory; it does keep alarm health critical.

## CLI

```text
rfid-middleware run --config <file>
rfid-middleware status [--json]
rfid-middleware healthcheck
rfid-middleware version
rfid-middleware reader test
rfid-middleware reader watch --duration <seconds>
rfid-middleware reader network show --json --config <file>
rfid-middleware reader network configure --ip <ip> --mask <mask> --gateway <ip> [--port 1969] [--dns <ip>] [--apply] --config <file>
rfid-middleware reader network verify --address <ip> [--port 1969] --config <file>
rfid-middleware reader client show --json --config <file>
rfid-middleware reader client configure --server-ip <ip> [--port 1969] [--apply] --config <file>
rfid-middleware reader client disable [--apply] --config <file>
rfid-middleware manifest status
rfid-middleware manifest sync
rfid-middleware audit stats
rfid-middleware db verify
rfid-middleware diagnostics bundle --output <file>
```

The `reader network` and `reader client` commands are standalone commissioning commands and require the middleware to be stopped. Other commands except `run` communicate with the running process through its Unix control socket. Commissioning does not require PostgreSQL or backend connectivity.

## HTTP API used in development

- `GET /api/v1/middleware/{id}/manifest` — middleware manifest retrieval with ETag support.
- `POST /api/v1/mobile/middleware/{id}/manifests/{manifestId}/items/authorize` — idempotently authorize an EPC.
- `POST /api/v1/mobile/middleware/{id}/manifests/{manifestId}/revoke` — idempotently revoke EPCs.
- `POST /api/v1/mobile/middleware/{id}/manifests/{manifestId}/complete` — complete a batch and clear authorization.
- `POST /admin/middleware/{id}/manifest/activate` — development manifest activation.
- `PUT /admin/middleware/{id}/fault` — development fault injection.
- `GET /healthz` — backend health.

There is intentionally no middleware scan-upload API.

## Environment configuration

Use `.env.example` for WSL development and `packaging/environment.example` for a packaged service. Important values include:

- `RFID_API_TOKEN`
- `RFID_DatabaseConnectionString`
- `RFID_ReaderLockFile`
- `RFID_CommissioningBackupDirectory`
- `ConnectionStrings__MockServer`
- `RFID_ReaderMode`
- `RFID_VendorReader__Transport`
- `RFID_VendorReader__SerialDevice`
- `RFID_VendorReader__Baud`
- `RFID_VendorReader__Rs485Address`
- `RFID_VendorReader__AlarmMode` (`BuzzerAndDigitalOutput1` by default; individual `Buzzer` or `DigitalOutput1` modes are also supported; output 0 is unused)
- `RFID_VendorReader__AlarmPulseMilliseconds`
- `RFID_NATIVE_DIR`
- `LD_LIBRARY_PATH`

Never commit `.env`, production tokens, or production database passwords.

## Verification

```bash
bash scripts/build.sh
bash scripts/native-stability.sh 100
bash scripts/soak.sh 2
bash scripts/build-deb.sh 0.1.0
```

Run the release soak for 72 hours by passing `72` to `soak.sh`. Set `RFID_MIDDLEWARE_PID` to sample the live service's RSS, thread count, and file descriptors into `artifacts/soak/metrics.csv`. Serial commissioning, configured alarm timing, reader firmware, and antenna behavior must be verified on the production Ubuntu host before enabling transport operations.

PostgreSQL integration tests use `RFID_TEST_POSTGRES`. Each test creates an isolated schema and removes it afterward.

## Repository layout

```text
src/RfidMiddleware.Host             Service host, control socket, CLI and workers
src/RfidMiddleware.Core             Domain model, EPC validation and aggregation
src/RfidMiddleware.Persistence      PostgreSQL middleware persistence
src/RfidMiddleware.Reader           Reader/alarm contracts and managed fake
src/RfidMiddleware.Reader.Vendor    Native x86-64 SDK adapter
src/RfidMiddleware.Server           Manifest HTTP client
tools/RfidMiddleware.MockServer     PostgreSQL-backed development backend
tests/                              Unit, integration, E2E, native and packaging tests
fixtures/                           Deterministic manifests and scenarios
linux/x86_64/release                Vendor Linux x86-64 SDK files
packaging/                          DEB and systemd configuration
Dockerfile                         Middleware and development-backend image targets
docker-compose*.yml                Shipping, development, TCP, and serial definitions
```

## Production notes

- Ubuntu 24.04 x86-64 is the supported production platform.
- TCP is preferred when the reader network is available; serial remains supported.
- Use a local PostgreSQL service for `rfid_edge` so backend/network outages do not prevent local validation.
- Use a stable `/dev/serial/by-id/...` path instead of `/dev/ttyUSB0` in production.
- Set a strong PostgreSQL password and a production bearer token outside `appsettings.json`.
- Require TLS for communication with the production backend.
- Verify the selected alarm channels and pulse timing, enabled antennas, baud/address, and reader firmware on physical hardware. Setup mode is `BuzzerAndDigitalOutput1`; select the final single channel only after wiring acceptance.
- Run the release soak and native lifecycle tests before deployment.

## Operational paths

- Application: `/usr/lib/rfid-middleware`
- Configuration: `/etc/rfid-middleware/appsettings.json`
- Secrets/environment: `/etc/rfid-middleware/environment` (start from `environment.example`)
- Local data: PostgreSQL database configured by `RFID_DatabaseConnectionString`
- Control socket: `/run/rfid-middleware/control.sock`
- Logs: systemd journal

Tag validation fails closed when no active manifest exists or the manifest is expired. Persistence, reader, and queue faults make service health critical without activating the physical unauthorized-tag alarm.
