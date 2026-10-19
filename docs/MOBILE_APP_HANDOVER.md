# Mobile App Handover: RFID Tag Authorization

Audience: Android and backend developers integrating the mobile-controlled RFID authorization flow.

## 1. Ownership and system boundary

The Android app controls which EPCs are authorized for a batch. It sends mutations to the backend. The middleware only downloads the newest manifest and checks reader observations locally.

```text
Android app ── authorize/revoke/complete ──► backend
                                                │
                                      immutable manifest versions
                                                │
                                                ▼
RFID middleware ◄── polls every 5 seconds ── backend
       │
       └── listed tag: silent
           unlisted/invalid/no valid manifest: configured reader alarm
```

The Android app must not:

- Connect directly to the RFID reader or middleware.
- Upload scan observations, RSSI, antenna data, or tag counts to the middleware.
- Contain the middleware bearer token.
- Decide that a mutation is active at the gate before the middleware has synchronized it.

Android/backend own departure, return, missing-tag reconciliation, additional-tag registration, and explicit batch completion.

## 2. Identifiers and state stored by Android

For each active batch, persist:

| Field | Meaning |
|---|---|
| `middlewareId` | Gate/middleware identity, for example `WH-A-GATE-01` |
| `manifestId` | Stable manifest identity for the entire batch |
| `currentVersion` | Latest backend-confirmed immutable version |
| `status` | `ACTIVE` or `COMPLETED` |
| `requestId` | Unique ID generated once for each user action |
| EPC records | Expected, present, missing, additional, and revoked tag state |

Keep the same `manifestId` throughout the batch. Every successful mutation creates exactly one higher version. Never create a new request ID when retrying the same logical action.

Recommended request ID format: a UUID generated on the device, such as `550e8400-e29b-41d4-a716-446655440000`.

## 3. Authentication

All mobile mutation requests require:

```http
Authorization: Bearer <mobile-access-token>
Content-Type: application/json
```

The mobile token must have the mobile role for the selected `middlewareId`. Middleware and mobile tokens are separate. The development token is `dev-mobile-token`; it must never be used in production.

Base path used below:

```text
/api/v1/mobile/middleware/{middlewareId}/manifests/{manifestId}
```

## 4. Register an additional tag

Call this endpoint before the physical tag passes the gate:

```http
POST /api/v1/mobile/middleware/{middlewareId}/manifests/{manifestId}/items/authorize
```

Request:

```json
{
  "requestId": "550e8400-e29b-41d4-a716-446655440000",
  "expectedVersion": 17,
  "inventoryId": "INV-EXTRA-001",
  "epc": "E2801170AAAAAAAAAAAAAAAA",
  "note": "Approved additional item"
}
```

Successful response (`200 OK`):

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

Android must replace `currentVersion` with `newVersion`. A successfully registered tag is backend-authorized, but the UI should show `Waiting for gate sync` until gate synchronization is confirmed by the operational backend. The middleware normally polls every five seconds.

The backend rejects an EPC already present in the active manifest with `409 Conflict`.

## 5. Revoke one or more tags

```http
POST /api/v1/mobile/middleware/{middlewareId}/manifests/{manifestId}/revoke
```

Request:

```json
{
  "requestId": "8b698769-983d-4b99-b608-a52b753d51a8",
  "expectedVersion": 18,
  "epcs": [
    "E2801170AAAAAAAAAAAAAAAA"
  ],
  "reason": "RETURNED_TO_STORAGE",
  "requestedBy": "operator-123",
  "occurredAt": "2026-10-13T10:30:00Z",
  "completeBatch": false
}
```

Required fields are `requestId`, `expectedVersion`, a non-empty `epcs` list, `reason`, and `requestedBy`. `occurredAt` is optional. `completeBatch` defaults to `false`.

Successful response (`200 OK`):

```json
{
  "manifestId": "TR-2026-00128",
  "previousVersion": 18,
  "newVersion": 19,
  "revokedEpcs": [
    "E2801170AAAAAAAAAAAAAAAA"
  ],
  "status": "ACTIVE",
  "alreadyApplied": false
}
```

Once the middleware synchronizes version 19, every revoked EPC triggers the configured reader alarm if scanned. During hardware setup, middleware attempts the built-in buzzer plus digital outputs 0 and 1.

Rules:

- Every requested EPC must currently exist in the active manifest.
- Revoking the final remaining EPC with `completeBatch: false` returns `409`.
- `completeBatch: true` is allowed only when the request revokes every remaining EPC.
- Prefer the explicit completion endpoint when reconciliation is a separate user action.

## 6. Complete a batch

Call completion only after the app has resolved its expected, returned, missing, and additional tags according to business policy.

```http
POST /api/v1/mobile/middleware/{middlewareId}/manifests/{manifestId}/complete
```

Request:

```json
{
  "requestId": "04fdd430-f662-4346-a596-49a24a3eb9c2",
  "expectedVersion": 19,
  "note": "Return reconciliation approved"
}
```

Successful response (`200 OK`):

```json
{
  "manifestId": "TR-2026-00128",
  "previousVersion": 19,
  "newVersion": 20,
  "status": "COMPLETED",
  "alreadyApplied": false
}
```

Android must store `status = COMPLETED` and `currentVersion = 20`, then make further tag mutations unavailable for that batch. When the middleware receives version 20, it clears active authorization atomically. It remains healthy and intentionally idle, but every later observed tag alarms until a new active manifest is assigned.

## 7. EPC input validation

Normalize before sending:

1. Trim leading and trailing whitespace.
2. Convert to uppercase.
3. Accept hexadecimal characters only (`0-9`, `A-F`).
4. Require an even number of characters.
5. Accepted length is 2 through 512 characters.

Example Kotlin validator:

```kotlin
private val epcPattern = Regex("^[0-9A-F]+$")

fun normalizeEpc(raw: String): String? {
    val value = raw.trim().uppercase()
    return value.takeIf {
        it.length in 2..512 && it.length % 2 == 0 && epcPattern.matches(it)
    }
}
```

Reject invalid EPCs in the UI before making the API call.

## 8. Idempotency and retries

Every mutation is idempotent by mobile role, `middlewareId`, and `requestId`.

- Generate the `requestId` once when the user confirms an action.
- Persist the request body and ID before sending.
- On timeout or connection loss, retry the exact same request body and request ID.
- Do not generate a new request ID merely because the response was lost.
- An identical retry returns the original result with `alreadyApplied: true` and does not create another version.
- Reusing the request ID with different content returns `409 Conflict`.

The content must remain byte-for-byte equivalent at the data-model level. In particular, do not change notes, timestamps, operator identity, EPC order/content, or `expectedVersion` during an idempotent retry.

## 9. Optimistic concurrency

Every mutation includes `expectedVersion`. If another device or operator changed the batch first, the backend returns:

```http
HTTP/1.1 409 Conflict
Content-Type: application/json
```

```json
{
  "error": "Manifest version conflict.",
  "currentVersion": 19
}
```

Android handling:

1. Do not silently resend the old mutation with version 19.
2. Fetch/refetch the batch from the mobile backend API.
3. Reconcile the server state with the pending user action.
4. Ask the operator to confirm again if the effective action changed.
5. Submit a new logical action with a new request ID and the refreshed version.

The middleware manifest download endpoint is not a mobile endpoint. The production backend must expose current batch/version state through its existing mobile batch API; do not give Android the middleware token as a workaround.

## 10. Status and error handling

| HTTP status | Meaning | Android action |
|---|---|---|
| `200` | Mutation applied or identical retry returned | Save `newVersion`, `status`, and result |
| `400` | Required data is absent or invalid | Show a field/business validation error; do not auto-retry |
| `401` | Token is absent, invalid, wrong role, or wrong middleware | Refresh login/token or stop and report access failure |
| `404` | Requested active manifest was not found | Refresh batch state; it may be completed or the IDs may be wrong |
| `409` with `currentVersion` | Stale `expectedVersion` | Refresh and reconcile before a new request |
| `409` without `currentVersion` | Request ID reused differently or business conflict | Do not auto-retry; show the returned error |
| `5xx` or network timeout | Outcome may be unknown | Retry the identical body with the same request ID |

Always parse and log the backend `error` value for support diagnostics, without logging bearer tokens.

Current reference-server note: required-field validation returns `400`, but a malformed EPC can currently escape the mock server's error mapping and produce `500`. Android must validate EPCs before submission. The production backend should map malformed EPC input to `400` before release; a `500` must still be handled as an unknown outcome using the same request ID.

## 11. Missing and additional tag workflow

Suggested return screen groups:

- `Expected and returned`: no authorization change needed unless business policy revokes returned stock.
- `Missing`: retain in reconciliation and require an operator decision. The middleware cannot infer that a tag is missing.
- `Additional`: require identification and approval, then call `authorize` before allowing it through the gate.
- `Revoked`: show the manifest version at which revocation took effect.
- `Pending sync`: backend mutation succeeded but the gate has not confirmed the new version.

Do not auto-complete a batch merely because all currently scanned tags were processed. Completion is an explicit operator action after missing/additional reconciliation.

## 12. Recommended Android data model

```kotlin
data class BatchAuthorizationState(
    val middlewareId: String,
    val manifestId: String,
    val currentVersion: Long,
    val status: ManifestStatus,
    val pendingMutations: List<PendingMutation>
)

enum class ManifestStatus { ACTIVE, COMPLETED }

data class PendingMutation(
    val requestId: String,
    val operation: String,
    val serializedBody: String,
    val createdAt: Instant
)
```

Store pending mutations durably (for example with Room), because the operator may lose connectivity after the backend commits but before the app receives the response.

## 13. Acceptance tests for Android

The mobile implementation is ready for handover when these pass:

1. Authorize an additional EPC and store the returned higher version.
2. Retry the identical authorize request and verify no second version is created.
3. Reuse its request ID with modified content and verify the app handles `409` without retrying.
4. Submit a stale `expectedVersion`, refresh, reconcile, and resubmit as a new logical action.
5. Revoke one EPC and confirm it alarms only after the middleware synchronizes the new version.
6. Reject blank `reason`, blank `requestedBy`, empty EPC lists, and malformed EPCs in the app.
7. Handle final-EPC revocation using explicit completion or `completeBatch: true`.
8. Complete the batch and disable all further mutations in the UI.
9. Lose the network after submission, restart the app, and retry the exact persisted request.
10. Verify Android never calls the middleware manifest endpoint or sends scan-derived data.

## 14. Development environment

Default development values:

```text
Backend:       http://127.0.0.1:5080
Middleware ID: WH-A-GATE-01
Manifest ID:   TR-2026-00128
Mobile token:  dev-mobile-token
Initial version after reset: 17
```

For an Android emulator, `127.0.0.1` is the emulator itself. Use the emulator-to-host address (commonly `10.0.2.2`) or a reachable LAN/VM address. Production must use HTTPS and an authenticated mobile-session token issued by the real backend.

The API in this repository's `RfidMiddleware.MockServer` is the executable reference contract for development and integration tests. The production backend must implement the same semantics, persistence, role separation, and idempotency guarantees.

## 15. Current physical acceptance batch

The current gate acceptance test authorizes exactly these EPCs:

| Inventory ID | EPC |
|---|---|
| `HW-TAG-001` | `E28069950000400E45B4F53E` |
| `HW-TAG-002` | `E280689400005010BF75955B` |

Do not bundle any other EPC into this test manifest. The October 19, 2026 COM11 test observed nearby EPC `E280689400004010BF78D96A`, which middleware correctly classified as unauthorized.

Reader commissioning is not a mobile responsibility. BAFO configures the reader's fixed LAN address once; normal traffic is middleware-to-reader TCP on private port 1969. Android continues to talk only to the authenticated backend. A reader output fault may leave gate health critical while authorization and local audit continue, so the operational backend should expose gate readiness separately from successful manifest mutation; Android must not interpret a successful mutation as proof that the physical alarm hardware is healthy.
