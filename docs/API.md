# RFID Middleware API Reference

This document describes the HTTP contracts implemented by this repository. There is one shared backend deployment and database for the mobile app and middleware. The route groups below describe callers and permissions; they do not require separate backend services.

The implemented data flow is:

```text
mobile app -- authorize/revoke/complete --> shared backend
middleware -- polls current manifest ----> same shared backend
RFID reader -- raw TCP scans -----------> middleware
middleware -- local EPC validation ------> local PostgreSQL audit and alarm
```

The backend publishes each manifest version and the middleware downloads it. The backend does not push directly to the middleware. Scan observations are never uploaded to the backend.

The contract has three API surfaces:

1. **Backend integration API** — used by the middleware to download authorization manifests.
2. **Mobile integration API** — used by a mobile client to change a manifest on the backend.
3. **Local operations API** — exposed by the middleware over a local Unix domain socket.

The backend and mobile routes in this repository are implemented together by `RfidMiddleware.MockServer`. They are a development and integration-test reference for the real shared backend contract. The `/admin` routes and development credentials must never be deployed to production.

There is deliberately **no scan-upload endpoint**. Tag observations and audit records remain in the middleware's local PostgreSQL database.

## Conventions

### Base URLs

| Surface | Development location | Intended caller |
|---|---|---|
| Backend and mobile API | `http://127.0.0.1:5080` | Middleware or mobile app |
| Local operations API | Unix socket configured by `ControlSocket` | Local CLI and operators |

Production backend traffic should use HTTPS. The local operations API is not a TCP service and should not be exposed on a network.

`RFID_SERVER_BASE_URL` is the root URL of the same backend used by the mobile app. The middleware appends its manifest route to this value. Include the scheme and, when needed, the API port:

```dotenv
RFID_SERVER_BASE_URL=http://10.20.30.40:8080/
```

Plain HTTP is suitable only on an approved private network or temporary isolated test network because it does not encrypt the bearer token. Use HTTPS across public or untrusted networks.

TCP port `1969` is reserved for the RFID reader's vendor-protocol connection to the middleware. It is not an HTTP backend port and must never be used in `RFID_SERVER_BASE_URL`.

### Content type and field names

JSON requests use:

```http
Content-Type: application/json
```

JSON property names are camel-cased. Timestamps use ISO 8601/RFC 3339 with an offset, normally UTC (`Z`). Manifest versions are positive 64-bit integers.

### Authentication

Backend integration requests require a middleware-role bearer token:

```http
Authorization: Bearer <middleware-token>
```

Mobile integration requests require a separate mobile-role bearer token:

```http
Authorization: Bearer <mobile-token>
```

The seeded development credentials are:

| Role | Middleware ID | Token |
|---|---|---|
| Middleware | `WH-A-GATE-01` | `dev-token` |
| Mobile | `WH-A-GATE-01` | `dev-mobile-token` |

The mobile app and middleware use the same backend service but should use independently issued credentials. A mobile token must not authorize the middleware manifest route, and a middleware token must not authorize mobile mutation routes.

The local operations API has no HTTP authentication because access is controlled by Unix socket file permissions.

### Common error response

Mobile business and validation errors use this body:

```json
{
  "error": "Human-readable error message."
}
```

A version conflict also reports the current version:

```json
{
  "error": "Manifest version conflict.",
  "currentVersion": 18
}
```

Authentication failures have an empty response body. Framework-level malformed JSON errors may use ASP.NET Core's standard problem-details response.

## Backend integration API

### Get the current manifest

```http
GET /api/v1/middleware/{middlewareId}/manifest
Authorization: Bearer <middleware-token>
If-None-Match: "TR-2026-00128-17"
```

The middleware polls this endpoint every `ManifestPollSeconds` (five seconds by default) and caches the returned `ETag`. `If-None-Match` is absent on the first request. The backend should return the newest `ACTIVE` or `COMPLETED` version for this middleware identity.

#### Path parameters

| Parameter | Type | Description |
|---|---|---|
| `middlewareId` | string | Middleware/gate identity assigned by the backend. |

#### `200 OK` — active manifest

Response headers include an `ETag`.

```json
{
  "manifestId": "TR-2026-00128",
  "transportId": "TR-2026-00128",
  "version": 17,
  "status": "ACTIVE",
  "expiresAt": "2030-12-31T23:59:59Z",
  "checksum": "1C10A6EC667E1960C037B19808DD45B83502B7E6DA403C7B539E41363941C813",
  "itemCount": 2,
  "items": [
    {
      "inventoryId": "INV000001",
      "epc": "E28011700000000000000000"
    },
    {
      "inventoryId": "INV000002",
      "epc": "E28011700000000000000001"
    }
  ]
}
```

| Field | Type | Required | Rules |
|---|---|---:|---|
| `manifestId` | string | yes | Stable, non-empty identity for the logical manifest. |
| `transportId` | string | yes | Non-empty transport or shipment identity. |
| `version` | integer | yes | Positive and strictly increases whenever content or status changes. |
| `status` | string | yes | `ACTIVE` for a usable authorization manifest. |
| `expiresAt` | timestamp | yes | Future RFC 3339 timestamp. The middleware treats scans as stale at or after this time. |
| `checksum` | string or null | no | If non-empty, a 64-character hexadecimal SHA-256 checksum using the algorithm below. |
| `itemCount` | integer | yes | Must equal the number of elements in `items` and be at least one for `ACTIVE`. |
| `items` | array | yes | Non-empty for `ACTIVE`; EPCs must be valid and unique after normalization. |
| `items[].inventoryId` | string | yes | Non-empty inventory identity. |
| `items[].epc` | string | yes | Even-length hexadecimal EPC, normalized by trimming and converting to uppercase. |

The checksum input is the normalized EPC values sorted with ordinal comparison and joined by a single line-feed character (`\n`) with no final line feed. The checksum is the uppercase hexadecimal SHA-256 digest of those UTF-8 bytes. For the two EPCs in the example, it is:

```text
1C10A6EC667E1960C037B19808DD45B83502B7E6DA403C7B539E41363941C813
```

The middleware validates `itemCount`, EPC format and uniqueness, and a non-empty checksum before activation. It stores an expired manifest if one is returned, but health becomes critical and scans produce `StaleManifest`; the backend must therefore publish a future `expiresAt`.

The backend must persist version counters across restarts. The middleware rejects a lower version and does not reapply changed content with the same version, even if the `ETag` changes.

#### `200 OK` — completion directive

```json
{
  "manifestId": "TR-2026-00128",
  "transportId": "TR-2026-00128",
  "version": 20,
  "status": "COMPLETED",
  "expiresAt": "2030-12-31T23:59:59Z",
  "checksum": "",
  "itemCount": 0,
  "items": []
}
```

The middleware treats `COMPLETED` as a directive to atomically deactivate local authorization. For this response, `manifestId` must be non-empty, `version` must be positive and newer than the last applied version, `itemCount` must be `0`, and `items` must be an empty array. `expiresAt` is not used for a completion directive but should still be a valid timestamp for a consistent schema.

#### ETag behavior

Every `200 OK` manifest response should include a strong `ETag` that changes when the manifest version changes, for example:

```http
ETag: "TR-2026-00128-17"
```

When `If-None-Match` identifies the current version, return `304 Not Modified` with no response body. Do not return `304` after the manifest version or status changes.

#### Other responses

| Status | Meaning | Caller behavior |
|---|---|---|
| `304 Not Modified` | The supplied ETag is current. | Keep the currently active state. |
| `401 Unauthorized` | Token is missing, invalid, or has the wrong role. | Middleware records an error and tries again at the configured polling interval; operators must correct the credential. |
| `404 Not Found` | No active or completed manifest exists for the middleware. | Treat synchronization as failed. |
| `429 Too Many Requests` | Backend throttling. | Retain the last valid state and try again at the configured polling interval. |
| `5xx` | Backend failure. | Retain the last valid state and try again at the configured polling interval. |

The current middleware uses a fixed polling interval rather than exponential backoff. Backend rate limits must accommodate one request per middleware per polling interval. A valid, unexpired cached manifest remains usable during an outage; after its expiry, scans fail closed as `StaleManifest`.

Example:

```bash
curl -i \
  -H 'Authorization: Bearer dev-token' \
  http://127.0.0.1:5080/api/v1/middleware/WH-A-GATE-01/manifest
```

## Mobile integration API

All mutations keep the same `manifestId` and publish the next immutable version. The active version must equal `expectedVersion`.

### Initial manifest publication boundary

The mobile mutation endpoints below operate only after an initial `ACTIVE` manifest exists. Creating that first manifest is part of the real backend's existing transport, shipment, or loading workflow and is not an inbound middleware endpoint.

For a complete production integration, the backend team must ensure that workflow atomically creates:

- the middleware assignment;
- a stable `manifestId` and `transportId`;
- positive version `1` or another positive initial version;
- a future expiry;
- at least one valid item; and
- the checksum and item count returned by the middleware manifest endpoint.

`POST /admin/middleware/{middlewareId}/manifest/activate` provides this capability only in the development mock server. Production must not expose or copy the unauthenticated `/admin` route. If the real backend does not already have an initial-publication operation, that backend operation must be designed separately before end-to-end mobile testing can begin.

### Idempotency and concurrency

`requestId` identifies one user-confirmed logical mutation for a middleware. Clients should generate a UUID, persist it with the exact request body, and reuse both after a timeout or unknown outcome.

- Repeating the same operation and body with the same `requestId` returns the stored result with `alreadyApplied: true`.
- Reusing a `requestId` for different content or another operation returns `409 Conflict`.
- Sending a stale `expectedVersion` returns `409 Conflict` with `currentVersion`.
- After a version conflict, refetch current state, reconcile, and use a new request ID for any newly confirmed action.

### EPC format

EPCs are trimmed and converted to uppercase. A valid EPC contains only hexadecimal characters (`0-9`, `A-F`), has an even number of characters, and is between 2 and 512 characters long.

### Authorize an item

Adds one item to an active manifest and increments its version.

```http
POST /api/v1/mobile/middleware/{middlewareId}/manifests/{manifestId}/items/authorize
Authorization: Bearer <mobile-token>
Content-Type: application/json
```

```json
{
  "requestId": "550e8400-e29b-41d4-a716-446655440000",
  "expectedVersion": 17,
  "inventoryId": "INV-EXTRA-001",
  "epc": "e2801170aaaaaaaaaaaaaaaa",
  "note": "Approved additional item"
}
```

| Field | Type | Required | Rules |
|---|---|---:|---|
| `requestId` | string | yes | Non-empty; unique per logical action. |
| `expectedVersion` | integer | yes | Positive and equal to the active version. |
| `inventoryId` | string | yes | Non-empty; surrounding whitespace is removed. |
| `epc` | string | yes | Valid EPC; must not already be active. |
| `note` | string or null | no | Informational. |

`200 OK`:

```json
{
  "manifestId": "TR-2026-00128",
  "previousVersion": 17,
  "newVersion": 18,
  "inventoryId": "INV-EXTRA-001",
  "epc": "E2801170AAAAAAAAAAAAAAAA",
  "alreadyApplied": false
}
```

### Revoke items

Removes one or more active EPCs and increments the manifest version.

```http
POST /api/v1/mobile/middleware/{middlewareId}/manifests/{manifestId}/revoke
Authorization: Bearer <mobile-token>
Content-Type: application/json
```

```json
{
  "requestId": "8b698769-983d-4b99-b608-a52b753d51a8",
  "expectedVersion": 18,
  "epcs": ["E2801170AAAAAAAAAAAAAAAA"],
  "reason": "RETURNED_TO_STORAGE",
  "requestedBy": "operator-123",
  "occurredAt": "2026-10-20T10:30:00Z",
  "completeBatch": false
}
```

| Field | Type | Required | Rules |
|---|---|---:|---|
| `requestId` | string | yes | Non-empty; unique per logical action. |
| `expectedVersion` | integer | yes | Positive and equal to the active version. |
| `epcs` | string[] | yes | At least one EPC; every EPC must currently be active. |
| `reason` | string | yes | Non-empty audit reason. |
| `requestedBy` | string | yes | Non-empty operator identity. |
| `occurredAt` | timestamp or null | no | Business-event time; server time is used when omitted. |
| `completeBatch` | boolean | no | Defaults to `false`; may be `true` only when all remaining EPCs are revoked. |

`200 OK`:

```json
{
  "manifestId": "TR-2026-00128",
  "previousVersion": 18,
  "newVersion": 19,
  "revokedEpcs": ["E2801170AAAAAAAAAAAAAAAA"],
  "status": "ACTIVE",
  "alreadyApplied": false
}
```

Revoking the final EPC with `completeBatch: false` returns `409 Conflict`. Setting `completeBatch: true` while any EPC remains returns `400 Bad Request`. A successful complete-batch revocation returns `status: "COMPLETED"`.

### Complete a manifest

Publishes an empty `COMPLETED` version, causing middleware authorization to be cleared on its next synchronization.

```http
POST /api/v1/mobile/middleware/{middlewareId}/manifests/{manifestId}/complete
Authorization: Bearer <mobile-token>
Content-Type: application/json
```

```json
{
  "requestId": "04fdd430-f662-4346-a596-49a24a3eb9c2",
  "expectedVersion": 19,
  "note": "Return reconciliation approved"
}
```

`200 OK`:

```json
{
  "manifestId": "TR-2026-00128",
  "previousVersion": 19,
  "newVersion": 20,
  "status": "COMPLETED",
  "alreadyApplied": false
}
```

### Mobile mutation status codes

| Status | Meaning | Recommended action |
|---|---|---|
| `200 OK` | Mutation succeeded or an identical request was replayed. | Save the result and new version. |
| `400 Bad Request` | Invalid input or business validation failure. | Correct the request; do not blindly retry. |
| `401 Unauthorized` | Missing, invalid, or wrong-role token. | Refresh authentication or report access failure. |
| `404 Not Found` | Middleware, manifest, or active state was not found. | Refresh state and verify IDs. |
| `409 Conflict` with `currentVersion` | Optimistic concurrency conflict. | Refresh, reconcile, and submit a newly confirmed request. |
| Other `409 Conflict` | Idempotency or manifest-state conflict. | Show the error and reconcile state. |
| `5xx` or timeout | Outcome may be unknown. | Retry the identical persisted body and `requestId`. |

A backend success means a new manifest version exists; it does not guarantee that middleware has downloaded it yet. The current contract intentionally has no remote middleware acknowledgement or scan-upload endpoint. Therefore, the mobile app may report that a version was **published to the backend**, but it must not claim that the gate applied it.

An operator can confirm the applied version locally with `rfid-middleware manifest status`. If remote applied-version confirmation becomes a product requirement, it needs a separately designed authenticated heartbeat or acknowledgement contract; it is not implemented by this repository.

## Local operations API

The middleware binds these routes to the configured Unix domain socket. The supported operator interface is the `rfid-middleware` CLI, but direct calls can be useful for diagnostics:

```bash
curl --unix-socket /run/rfid-middleware/control.sock http://localhost/status
```

### `GET /status`

Returns an overall status and component details.

```json
{
  "status": "healthy",
  "startedAt": "2026-10-20T08:00:00Z",
  "reader": {
    "connected": true,
    "inventoryRunning": true,
    "error": null,
    "updatedAt": "2026-10-20T10:31:03Z"
  },
  "alarm": {
    "healthy": true,
    "active": false,
    "error": null,
    "updatedAt": "2026-10-20T10:31:03Z"
  },
  "authorization": {
    "authorizationState": "Active",
    "latestManifestVersion": 18
  },
  "manifest": {
    "activeManifestId": "TR-2026-00128",
    "activeManifestVersion": 18,
    "manifestExpiresAt": "2026-10-21T10:30:00Z",
    "lastManifestSync": "2026-10-20T10:31:00Z",
    "lastManifestError": null
  },
  "system": {
    "lastSystemError": null
  },
  "reads": {
    "received": 1420,
    "dropped": 0
  },
  "alarmLatency": {
    "samples": 12,
    "p99Milliseconds": 8.4
  },
  "scanDataPolicy": "LOCAL_ONLY"
}
```

`status` is:

- `critical` when the reader is disconnected, alarm is unhealthy, reads were dropped, a system/manifest error exists, or authorization is uninitialized/invalid/expired;
- `degraded` when the last successful manifest sync is absent or older than `ManifestWarningSeconds`;
- `healthy` otherwise.

This route returns `200 OK` for all three states; inspect the response body.

### `GET /healthz`

Returns an empty `200 OK` only when reader, alarm, system, queue, and authorization state are healthy. Returns an empty `503 Service Unavailable` otherwise. A completed authorization state is considered valid, as is an active non-expired manifest.

### `GET /manifest`

Returns current synchronization and authorization state:

```json
{
  "authorizationState": "Active",
  "latestManifestVersion": 18,
  "activeManifestId": "TR-2026-00128",
  "activeManifestVersion": 18,
  "manifestExpiresAt": "2026-10-21T10:30:00Z",
  "lastManifestSync": "2026-10-20T10:31:00Z",
  "lastManifestError": null
}
```

`authorizationState` is `Uninitialized`, `Active`, or `Completed`.

### `POST /manifest/sync`

Queues an immediate manifest synchronization and returns `202 Accepted` with an empty body. It does not wait for synchronization to finish. Poll `GET /manifest` to observe the result.

### `GET /audit`

Returns aggregate counts from the local event store:

```json
{
  "total": 120,
  "authorized": 110,
  "unauthorized": 8,
  "other": 2,
  "newestObservation": "2026-10-20T10:31:00Z"
}
```

### `GET /db/verify`

Runs local database verification:

```json
{
  "result": "ok"
}
```

The exact `result` text is diagnostic and should not be parsed as a stable enum.

### `GET /reader/test`

Returns the current reader health object:

```json
{
  "connected": true,
  "inventoryRunning": true,
  "error": null,
  "updatedAt": "2026-10-20T10:31:03Z"
}
```

### `GET /reader/recent`

Returns up to the latest 1,000 raw reads held in memory, oldest first:

```json
[
  {
    "epc": "E28011700000000000000000",
    "readerId": "reader-01",
    "antennaId": 1,
    "rssi": -47,
    "observedAt": "2026-10-20T10:31:03Z"
  }
]
```

This endpoint contains scan data and must remain protected by local socket permissions.

### `GET /metrics`

Returns Prometheus text exposition with `Content-Type: text/plain`:

```text
rfid_reads_total 1420
rfid_reads_dropped_total 0
rfid_scan_data_local_only 1
rfid_system_fault 0
rfid_manifest_sync_fault 0
rfid_alarm_latency_p99_milliseconds 8.4
```

| Metric | Meaning |
|---|---|
| `rfid_reads_total` | Raw tag reads received since startup. |
| `rfid_reads_dropped_total` | Reads dropped by the processing pipeline since startup. |
| `rfid_scan_data_local_only` | Constant `1`, documenting the data policy. |
| `rfid_system_fault` | `1` when a current system error exists. |
| `rfid_manifest_sync_fault` | `1` when a current manifest synchronization error exists. |
| `rfid_alarm_latency_p99_milliseconds` | In-memory p99 alarm invocation latency; `0` before samples exist. |

## Development-only administration API

These mock-server routes have no authentication and are unsafe for production.

### `GET /healthz`

Returns:

```json
{ "status": "healthy" }
```

### `POST /admin/reset`

Resets mock data and seeds a manifest.

```http
POST /admin/reset?count=100
```

`count` is optional, defaults to `100`, and is clamped to `1`–`1,000,000`. Returns an empty `200 OK`.

### `POST /admin/middleware/{middlewareId}/manifest/activate`

Supersedes the current active manifest and creates a new active manifest.

```json
{
  "manifestId": "TR-2026-00129",
  "transportId": "TR-2026-00129",
  "version": 18,
  "count": 2,
  "expiresMinutes": 30,
  "items": [
    { "inventoryId": "HW-TAG-001", "epc": "E28069950000400E45B4F53E" },
    { "inventoryId": "HW-TAG-002", "epc": "E280689400005010BF75955B" }
  ]
}
```

When a non-empty `items` array is supplied, `count` is ignored. Otherwise, the server generates `count` items. `expiresMinutes` is clamped to `1`–`43,200`. Explicit items are normalized and duplicate EPCs are rejected. The response echoes the request as `200 OK`.

### `PUT /admin/middleware/{middlewareId}/fault`

Configures manifest-download fault injection:

```json
{
  "profile": "none",
  "latencyMs": 0
}
```

Supported fixture profiles are:

| Profile | Effect on manifest download |
|---|---|
| `none` | Normal response. |
| `timeout` | Delays for five minutes. |
| `500` | Returns `500`. |
| `429` | Returns `429`. |
| `malformed` | Returns invalid JSON with an `application/json` content type. |
| `empty` | Returns an empty item list. |
| `expired` | Returns an expiry in the past. |
| `wrong-count` | Makes `itemCount` disagree with `items`. |
| `invalid-checksum` | Returns a deliberately incorrect checksum. |
| `duplicate-epc` | Adds a duplicate EPC. |

`latencyMs` is applied before the selected profile. The response echoes the stored profile as `200 OK`.

## Security and data-boundary requirements

- Host the mobile and middleware routes in the same production backend deployment unless the owning backend architecture requires otherwise; route separation is for authorization, not service separation.
- Use HTTPS and independently issued middleware/mobile credentials in production.
- Never configure the HTTP backend base URL with the RFID listener port `1969`.
- Do not expose the Unix control socket to remote clients; restrict its filesystem permissions.
- Do not deploy the mock server or `/admin` routes in production.
- Treat `/reader/recent` and local database data as sensitive scan data.
- Do not add scan data to backend synchronization payloads. Only authorization manifests flow from backend to middleware.

## Backend handoff checklist

Before VPS acceptance testing, the backend team must provide:

- the shared backend root URL and API port reachable from the VPS;
- middleware identity `WH-A-GATE-01` or its agreed replacement;
- a middleware-role bearer token distinct from the mobile credential;
- a production implementation of `GET /api/v1/middleware/{middlewareId}/manifest`;
- a durable initial-manifest publication workflow;
- an active test manifest containing at least one known physical EPC and excluding one known unauthorized EPC;
- monotonically increasing versions that survive backend restarts;
- correct `ETag` and `304 Not Modified` behavior;
- mobile operations for authorize, revoke, and complete; and
- a test window for `200`, `304`, `401`, `404`, `5xx`, expiry, revocation, completion, and backend-outage behavior.

The backend team does not need to implement an RFID-reader endpoint or scan-result upload. Reader TCP traffic terminates at the middleware on port `1969`, and scan audit data remains local.
