# Postman Test Backend Guide

This guide runs the repository's shared reference backend and tests the complete manifest flow with Postman. The same backend application serves both mobile mutation routes and the middleware manifest route.

```text
Postman/mobile -- authorize/revoke/complete --> reference backend
middleware ----- polls current manifest ----> same reference backend
reader ---------- raw TCP 1969 -------------> middleware
```

The reference backend and its `/admin` routes are development tools. They must not be deployed as a production backend or exposed directly to the internet.

## Included files

- `postman/RFID-Shared-Backend.postman_collection.json`
- `postman/RFID-Shared-Backend.postman_environment.json`

The collection covers backend health, reset, initial publication, middleware download, ETag/304 behavior, mobile authorization, revocation, completion, and invalid authentication.

## 1. Backend-only Postman test

This starts PostgreSQL and the reference backend without starting middleware or claiming reader port 1969:

```bash
cd /opt/rrligthing-rfid-middleware

sudo docker compose -p rfid-postman \
  --env-file docker/dev.env \
  -f docker-compose.yml \
  -f docker-compose.dev.yml \
  up -d --build postgres mock-backend
```

Confirm that the backend is healthy:

```bash
sudo docker compose -p rfid-postman \
  --env-file docker/dev.env \
  -f docker-compose.yml \
  -f docker-compose.dev.yml \
  ps
```

The backend is intentionally published only on VPS loopback at `127.0.0.1:5080`.

## 2. Open a secure tunnel from the Postman computer

On the laptop running Postman, keep this SSH session open:

```bash
ssh -L 5080:127.0.0.1:5080 <ADMIN_USER>@<VPS_PUBLIC_IP>
```

Postman can now reach the VPS backend at `http://127.0.0.1:5080` without exposing development administration routes publicly.

If local port 5080 is already used, choose another local port:

```bash
ssh -L 15080:127.0.0.1:5080 <ADMIN_USER>@<VPS_PUBLIC_IP>
```

Then change the Postman environment `baseUrl` to `http://127.0.0.1:15080`.

## 3. Import into Postman

Import both JSON files, then select the environment named **RFID Shared Backend - Local SSH Tunnel**.

Review these environment values:

| Variable | Purpose |
|---|---|
| `baseUrl` | Local end of the SSH tunnel; normally `http://127.0.0.1:5080` |
| `middlewareId` | Test middleware identity, default `WH-A-GATE-01` |
| `middlewareToken` | Development middleware token |
| `mobileToken` | Development mobile token |
| `authorizedEpc` | Physical tag expected to pass before revocation |
| `secondAuthorizedEpc` | Second authorized EPC; it may be a controlled synthetic test value |
| `extraEpc` | EPC added through the mobile authorization route |

Replace `authorizedEpc` with the exact normalized EPC printed by the physical reader. EPC values must contain an even number of hexadecimal characters.

The collection generates a timestamp-based initial version and new request IDs for every run. This keeps new manifest versions ahead of previously applied middleware state and preserves idempotency semantics within a run.

## 4. Run the backend contract test

Run the collection in order from request 01 through request 12. A successful run has all tests passing.

Running the full collection quickly validates the backend API contract, but middleware may observe only the final version. Use the staged procedure below for physical reader acceptance.

## 5. Full VPS and physical-reader test

Ensure no other Compose project is using TCP port 1969. Start the shared backend, middleware, PostgreSQL, and reader listener together:

```bash
sudo docker compose -p rfid-postman \
  --env-file docker/dev.env \
  -f docker-compose.yml \
  -f docker-compose.dev.yml \
  -f docker-compose.listener.yml \
  up -d --build
```

The listener override must be last so that `Vendor` plus `TcpListener` replaces the managed fake reader configured by the development override.

Confirm the services:

```bash
sudo docker compose -p rfid-postman \
  --env-file docker/dev.env \
  -f docker-compose.yml \
  -f docker-compose.dev.yml \
  -f docker-compose.listener.yml \
  ps
```

Keep the VPS cloud firewall and Docker `DOCKER-USER` rule restricting TCP 1969 to the reader site's approved public source IP. Port 5080 remains loopback-only and is accessed through SSH.

## 6. Staged physical acceptance

Send Postman requests manually instead of running the entire collection at once:

1. Send requests 01–04.
2. Wait at least one polling interval, five seconds by default.
3. Confirm the applied version:

   ```bash
   sudo docker compose -p rfid-postman \
     --env-file docker/dev.env \
     -f docker-compose.yml \
     -f docker-compose.dev.yml \
     -f docker-compose.listener.yml \
     exec middleware /app/rfid-middleware manifest status \
     --config /app/appsettings.json
   ```

4. Scan `authorizedEpc`; it must pass without an alarm.
5. Scan an EPC absent from the manifest; it must activate the configured alarm.
6. Send requests 05–07, wait five seconds, and confirm the next version.
7. Send requests 08–09, wait five seconds, and confirm the revoked version.
8. Scan `authorizedEpc` again; it must now activate the alarm.
9. Send requests 10–11, wait five seconds, and confirm authorization state `Completed`.
10. Send request 12 and confirm the wrong token is rejected with `401`.

Watch middleware logs during the sequence:

```bash
sudo docker compose -p rfid-postman \
  --env-file docker/dev.env \
  -f docker-compose.yml \
  -f docker-compose.dev.yml \
  -f docker-compose.listener.yml \
  logs -f middleware
```

## 7. Repeat or clean up

The reset request clears only backend data. The middleware retains its latest applied version in its own database. The collection's timestamp-based version normally permits another run without clearing middleware data.

Stop the test stack while retaining its data:

```bash
sudo docker compose -p rfid-postman \
  --env-file docker/dev.env \
  -f docker-compose.yml \
  -f docker-compose.dev.yml \
  -f docker-compose.listener.yml \
  down
```

To deliberately erase only the isolated `rfid-postman` test databases and start completely fresh, first verify that the project name is exactly `rfid-postman`, then run:

```bash
sudo docker compose -p rfid-postman \
  --env-file docker/dev.env \
  -f docker-compose.yml \
  -f docker-compose.dev.yml \
  -f docker-compose.listener.yml \
  down --volumes
```

Never run that command with the production project name.

## Production boundary

The following are intentionally test-only:

- `dev-token` and `dev-mobile-token`;
- unauthenticated `/admin` routes;
- automatic reset and seeded data;
- HTTP access through a local SSH tunnel; and
- the `RfidMiddleware.MockServer` application.

The real shared backend should implement the production contract in `docs/API.md`, issue independent mobile and middleware credentials, persist monotonically increasing manifest versions, and use HTTPS or an approved private network.
