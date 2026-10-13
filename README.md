# RFID Transport Validation Middleware

.NET 8 LTS command-line service for Ubuntu 24.04 x86-64. It validates RFID EPC observations against a durable mobile-controlled manifest, operates offline, and pulses reader digital output 0 only for tags that cannot be authorized. Scan data remains in the middleware's local PostgreSQL database and is never uploaded to the server.

## Purpose

The middleware sits between an RFID reader and a transport-management backend. It downloads a versioned authorization manifest, validates tags locally with low latency, and operates during backend outages. Reader digital output 0 is reserved for unauthorized tag observations; technical faults are exposed through health, metrics, and logs.

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
- Digital output 0 pulses only for tags that are unauthorized, invalid, or observed without a valid manifest.
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
                                  └──► digital output 0
```

Development uses two databases in one PostgreSQL container:

| Database | Purpose |
|---|---|
| `rfid_edge` | Middleware manifest cache, recovery state, and local scan audit |
| `rfid_backend` | Published manifests, middleware identity, faults, and revocation audit |

## Development environment

The supported development environment is Ubuntu 24.04 or WSL2 Ubuntu 24.04 with the .NET 10 SDK. The application itself targets .NET 8 LTS; the newer SDK is required for the `.slnx` solution format. Install `build-essential`, `postgresql-client`, `valgrind`, `strace`, `lsof`, and `patchelf`. The vendor libraries live in `linux/x86_64/release` and are copied to `native/` during build and publish. WSL supports managed tests, the SDK mock, TCP readers, and USB serial readers passed through with `usbipd`; final relay and antenna acceptance must still be performed with the physical hardware.

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

Set `ReaderMode` to `Vendor` and select `NativeMock`, `Tcp`, or `Serial` under `VendorReader`. Use stable `/dev/serial/by-id/...` paths for serial readers.

The development control socket is under `/tmp` because Unix sockets cannot be created on a Windows-mounted WSL path. The production unit uses `/run/rfid-middleware/control.sock` with restricted permissions.

The mock backend supports manifest activation and fault injection. See `fixtures/README.md` and `fixtures/scenarios.json` for deterministic payloads, fault names, and the 100,000-EPC reset command.
Both persistence layers use PostgreSQL. `rfid_edge` is private to the middleware and stores its cached manifest plus local-only scan audit. `rfid_backend` stores server manifests and revocation audit. Only manifest requests are sent from middleware to server; EPC scan observations are not uploaded.

## Real hardware quick reference

The committed native SDK is the Linux x86-64 build under `linux/x86_64/release`. For a USB serial reader, first obtain its current bus ID instead of assuming it is permanent. Windows exposes the adapter to WSL using `usbipd`, after which the middleware normally uses `/dev/ttyUSB0` or a stable `/dev/serial/by-id/...` path.

```powershell
usbipd list
usbipd bind --busid <BUSID>
usbipd attach --wsl --busid <BUSID>
```

The real test tags are:

- `E280689400004010BF743D5C`
- `E2806915000060068C95C54B`

## CLI

```text
rfid-middleware run --config <file>
rfid-middleware status [--json]
rfid-middleware version
rfid-middleware reader test
rfid-middleware reader watch --duration <seconds>
rfid-middleware manifest status
rfid-middleware manifest sync
rfid-middleware audit stats
rfid-middleware db verify
rfid-middleware diagnostics bundle --output <file>
```

All commands except `run` communicate with the running process through its Unix control socket. They do not open the live PostgreSQL database directly.

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
- `ConnectionStrings__MockServer`
- `RFID_ReaderMode`
- `RFID_VendorReader__Transport`
- `RFID_VendorReader__SerialDevice`
- `RFID_VendorReader__Baud`
- `RFID_VendorReader__Rs485Address`
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

Run the release soak for 72 hours by passing `72` to `soak.sh`. Set `RFID_MIDDLEWARE_PID` to sample the live service's RSS, thread count, and file descriptors into `artifacts/soak/metrics.csv`. Serial commissioning, digital output 0 polarity and timing, reader firmware, and antenna behavior must be verified on the production Ubuntu host before enabling transport operations.

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
```

## Production notes

- Ubuntu 24.04 x86-64 is the supported production platform.
- TCP is preferred when the reader network is available; serial remains supported.
- Use a local PostgreSQL service for `rfid_edge` so backend/network outages do not prevent local validation.
- Use a stable `/dev/serial/by-id/...` path instead of `/dev/ttyUSB0` in production.
- Set a strong PostgreSQL password and a production bearer token outside `appsettings.json`.
- Require TLS for communication with the production backend.
- Verify digital output 0 polarity and pulse timing, enabled antennas, baud/address, and reader firmware on physical hardware.
- Run the release soak and native lifecycle tests before deployment.

## Operational paths

- Application: `/usr/lib/rfid-middleware`
- Configuration: `/etc/rfid-middleware/appsettings.json`
- Secrets/environment: `/etc/rfid-middleware/environment` (start from `environment.example`)
- Local data: PostgreSQL database configured by `RFID_DatabaseConnectionString`
- Control socket: `/run/rfid-middleware/control.sock`
- Logs: systemd journal

Tag validation fails closed when no active manifest exists or the manifest is expired. Persistence, reader, and queue faults make service health critical without activating the physical unauthorized-tag alarm.
