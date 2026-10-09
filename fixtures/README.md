# Deterministic fixtures

`baseline.json` documents the stable identifiers used by the mock server and tests. The mock database is generated from code so schema migrations and large manifests remain reproducible.

- Authorized EPC: `E28011700000000000000001`
- Unauthorized EPC: `E2801170FFFFFFFFFFFFFFFF`
- Middleware token for local development: `dev-token`
- `--seed-count 100000` creates the performance manifest.

`scenarios.json` is the readable source of deterministic manifest, read-stream, queue-state, and load fixtures.

Fault profiles accepted by the mock server are `none`, `latency`, `timeout`, `connection-loss`, `lost-response`, `401`, `409`, `429`, `500`, `malformed`, `partial`, `empty`, `expired`, `wrong-count`, `duplicate-epc`, and `invalid-checksum`.

Activate a deterministic next manifest with `POST /admin/middleware/{id}/manifest/activate`; the optional JSON fields are `manifestId`, `transportId`, `version`, `count`, `expiresMinutes`, and an explicit `items` array. `real-tags.json` contains the two COM4 hardware-test EPCs.

After a tag has returned and is physically stored, simulate the mobile application with
`POST /api/v1/mobile/middleware/{middlewareId}/manifests/{manifestId}/revoke`. The request is idempotent by `requestId`; it atomically supersedes the active version and creates the next version without the revoked EPC.
