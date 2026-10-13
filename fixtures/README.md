# Deterministic fixtures

`baseline.json` documents the stable identifiers used by the mock server and tests. The mock database is generated from code so schema migrations and large manifests remain reproducible.

- Authorized EPC: `E28011700000000000000001`
- Unauthorized EPC: `E2801170FFFFFFFFFFFFFFFF`
- Middleware token for local development: `dev-token`
- Mobile token for local development: `dev-mobile-token`
- `--seed-count 100000` creates the performance manifest.

`scenarios.json` is the readable source of deterministic manifest, read-stream, queue-state, and load fixtures.

Fault profiles accepted by the mock server are `none`, `latency`, `timeout`, `connection-loss`, `lost-response`, `401`, `409`, `429`, `500`, `malformed`, `partial`, `empty`, `expired`, `wrong-count`, `duplicate-epc`, and `invalid-checksum`.

Activate a deterministic next manifest with `POST /admin/middleware/{id}/manifest/activate`; the optional JSON fields are `manifestId`, `transportId`, `version`, `count`, `expiresMinutes`, and an explicit `items` array. `real-tags.json` contains the two COM4 hardware-test EPCs.

Mobile authorization mutations use the `dev-mobile-token` bearer token and an `expectedVersion` for optimistic concurrency. Use `items/authorize` to add a tag, `revoke` to remove tags, and `complete` to close the batch. Requests are idempotent by `requestId`; each accepted mutation atomically publishes the next immutable version.
