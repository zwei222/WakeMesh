# WakeMesh.Api

ASP.NET Core (.NET 11) owner-scoped multi-device alarm API.

## Implemented (local)

- JwtBearer owner auth through the real handler: issuer, audience, lifetime, **RS256 signature required**, `ver=2.0`, `tid`, `scp=access_as_user`, `azp`, `oid` allowlist
- Substitute claims rejected: `appid` (for azp), long-form `objectidentifier` (for oid)
- `FallbackPolicy = OwnerOnly` (only `/health` is anonymous)
- Allowlist empty/unset → fail closed (startup incomplete config error does not echo oid/secret values)
- Signing keys: **OIDC MetadataAddress only** in production code (HTTPS required at startup). No static PEM config key. Tests inject the RSA public key + static OIDC fixture via `WebApplicationFactory` `PostConfigure<JwtBearerOptions>` without overriding production `ValidAlgorithms` / Issuer / Audience.
- Device register: server-generated id, 32-byte CSPRNG credential returned once, SHA-256 stored, `FixedTimeEquals` compare
- Desired snapshot + receipt (7-day TTL) built in a transaction with revision double-check; Pending→Active survives Reload
- ACK: `success` with non-empty `failures` → 400; AppliedRevision advanced with atomic `max` update; concurrent ACK/PUT does not 500
- Retire → `Retiring` + empty snapshot/tombstones; cancel success ACK completes only when `Status=Retiring` **and** `DesiredRevision == request.Revision` (stale pre-retire ACK cannot set `cancelConfirmed`)
- Immediate `POST .../revoke` → `Revoked` with `retirementState=revokedUnconfirmed`
- AlarmGroup create/list/replace/delete with `If-Match` (428/412); BumpDevices re-reads targets in-transaction and refuses non-live device status (Pending|Active only); concurrent same-id POST resolves 200/409
- Owner device list/detail exposes retirement state and recent ACK failures
- FCM: status flag only — never reported as sent

## Not verified

- Real Microsoft personal account / Entra app registration / production tokens
- Azure SQL connectivity and migration apply on a live server
- Real FCM delivery
- Notification/DB deadlock and SQL Server isolation under production locking (SQLite tests are not a substitute)
- Solution file (`WakeMesh.slnx`) was **not** modified for API-only verification; test the API project directly

## Configuration names (no real secrets)

| Key | Meaning |
|-----|---------|
| `ConnectionStrings:DefaultConnection` | SQL Server or SQLite connection string |
| `WakeMesh:Database:Provider` | `SqlServer` (default) or `Sqlite` |
| `WakeMesh:Auth:Issuer` | e.g. `https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0` (public consumers tenant GUID) |
| `WakeMesh:Auth:Audience` | API Application ID URI |
| `WakeMesh:Auth:RequiredScope` | `access_as_user` |
| `WakeMesh:Auth:AllowedAzp` | Mobile public client id (`azp` only) |
| `WakeMesh:Auth:AllowedOwnerOids` | Owner oid allowlist (required) |
| `WakeMesh:Auth:MetadataAddress` | OIDC metadata URL (required HTTPS; production signing keys) |
| `WakeMesh:Fcm:Configured` | Status flag only — sender not implemented |

### Fictional local example

```bash
export ConnectionStrings__DefaultConnection='Data Source=./wakemesh-dev.db'
export WakeMesh__Database__Provider=Sqlite
export WakeMesh__Auth__Issuer='https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0'
export WakeMesh__Auth__Audience='api://wakemesh-api-example'
export WakeMesh__Auth__AllowedAzp='00000000-0000-0000-0000-000000000001'
export WakeMesh__Auth__AllowedOwnerOids__0='00000000-0000-0000-0000-0000000000aa'
export WakeMesh__Auth__MetadataAddress='https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0/.well-known/openid-configuration'
export ASPNETCORE_URLS='http://127.0.0.1:5088'
```

Production must use HTTPS `MetadataAddress` (never a static PEM config key). Azure public endpoints assume HTTPS-only TLS termination.

## Design-time migrations

```bash
export WAKEMESH_DESIGN_CONNECTION='Server=...;Database=WakeMesh;...'   # from your secret store
# optional: export WAKEMESH_DESIGN_PROVIDER=SqlServer
dotnet ef migrations add Name -p src/WakeMesh.Api
dotnet ef migrations script -p src/WakeMesh.Api -o ./script.sql
```

The design-time factory refuses to run without `WAKEMESH_DESIGN_CONNECTION` and never embeds passwords.

## Run tests

```bash
export DOTNET_ROOT="$(pwd)/.tools/dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_HOME="$(pwd)/.tools/cli-home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet test tests/WakeMesh.Api.Tests/WakeMesh.Api.Tests.csproj
```

## HTTP surface

| Method | Path | Auth |
|--------|------|------|
| GET | `/health` | anonymous |
| GET/POST | `/api/v1/devices` | owner Bearer |
| GET | `/api/v1/devices/{id}` | owner Bearer (detail + recentFailures) |
| POST | `/api/v1/devices/{id}/retire` | owner Bearer (graceful) |
| POST | `/api/v1/devices/{id}/revoke` | owner Bearer (immediate, cancel unconfirmed) |
| GET | `/api/v1/devices/{id}/desired` | owner Bearer + `X-Device-Credential` |
| POST | `/api/v1/devices/{id}/acks` | owner Bearer + `X-Device-Credential` |
| GET/POST | `/api/v1/alarm-groups` | owner Bearer |
| PUT/DELETE | `/api/v1/alarm-groups/{id}` | owner Bearer + `If-Match: {version}` |

## Migrations

SQL Server migrations under `Migrations/`. SQLite tests use `EnsureCreated` only — do not treat that as SQL Server verification.
