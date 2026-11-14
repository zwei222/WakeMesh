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
| `ConnectionStrings:DefaultConnection` | SQL Server, PostgreSQL, or SQLite connection string |
| `WakeMesh:Database:Provider` | `SqlServer` (default), `Postgres`, or `Sqlite`; any other value fails closed at startup |
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

Two independent lineages hang off the same `WakeMeshDbContext`:

| Lineage | Project | Migrations assembly | Provider | `dotnet ef` startup project |
|---------|---------|---------------------|----------|-----------------------------|
| SQL Server (historical, frozen) | `src/WakeMesh.Api/Migrations` | `WakeMesh.Api` | `SqlServer` | `src/WakeMesh.Api` |
| PostgreSQL | `src/WakeMesh.Api.PostgresMigrations/Migrations` | `WakeMesh.Api.PostgresMigrations` | `Postgres` | `src/WakeMesh.Api.PostgresMigrations` |

The API project deliberately does **not** reference the PostgreSQL migrations project, so the
running API never carries that assembly and never auto-migrates. Migrations are an explicit
deploy step. SQL Server migration IDs, history rows, and Designer files are frozen — do not
rename or regenerate them.

Tooling split: the SDK is .NET 11 (`global.json`, 11.0.100) while the EF packages and the pinned
`dotnet-ef` tool are 10.0.15 (`dotnet-tools.json`). Run `dotnet tool restore` once, then invoke
`dotnet ef` from the repository root.

### SQL Server lineage (frozen)

```bash
# SQL Server lineage (frozen history)
export WAKEMESH_DESIGN_CONNECTION='Server=...;Database=WakeMesh;...'   # from your secret store
# optional: export WAKEMESH_DESIGN_PROVIDER=SqlServer
dotnet ef migrations add Name -p src/WakeMesh.Api
dotnet ef migrations script -p src/WakeMesh.Api -o ./script.sql
```

### PostgreSQL lineage

The PostgreSQL migrations project is an executable project (`OutputType=Exe`) that references
`WakeMesh.Api`, so it can be used as **both** `--project` and `--startup-project`. That keeps the
migrations assembly in the startup project's own build output, so no manual DLL copy or staging
into the API output is needed.

```bash
dotnet tool restore
export WAKEMESH_DESIGN_PROVIDER=Postgres
export WAKEMESH_DESIGN_CONNECTION='Host=...;Database=...;Username=...'  # from your secret store

# From a clean tree: rebuild the startup project so its output holds the migrations assembly.
# (No manual DLL copy; verified as clean -> restore -> build.)
dotnet clean   src/WakeMesh.Api.PostgresMigrations/WakeMesh.Api.PostgresMigrations.csproj
dotnet restore src/WakeMesh.Api.PostgresMigrations/WakeMesh.Api.PostgresMigrations.csproj
dotnet build --no-restore src/WakeMesh.Api.PostgresMigrations/WakeMesh.Api.PostgresMigrations.csproj

dotnet ef migrations list                      -p src/WakeMesh.Api.PostgresMigrations -s src/WakeMesh.Api.PostgresMigrations
dotnet ef migrations has-pending-model-changes -p src/WakeMesh.Api.PostgresMigrations -s src/WakeMesh.Api.PostgresMigrations
dotnet ef database update                      -p src/WakeMesh.Api.PostgresMigrations -s src/WakeMesh.Api.PostgresMigrations
dotnet ef migrations script --idempotent       -p src/WakeMesh.Api.PostgresMigrations -s src/WakeMesh.Api.PostgresMigrations -o ./pg-schema.sql

# New migration
dotnet ef migrations add Name -p src/WakeMesh.Api.PostgresMigrations -s src/WakeMesh.Api.PostgresMigrations
```

`-s src/WakeMesh.Api` is **not** the supported startup project for the PostgreSQL lineage: the API
project does not reference the migrations assembly, so `dotnet ef` fails with
`WakeMesh.Api.PostgresMigrations.dll not found` unless the assembly is manually staged into the API
build output. Do not stage it by hand.

If a build reports `NETSDK1064` for a package that is present in the local cache, the assets file was
restored with a relative `DOTNET_CLI_HOME`; re-run `dotnet restore --force` using the absolute
`DOTNET_CLI_HOME` shown under [Run tests](#run-tests).

The design-time factory refuses to run without `WAKEMESH_DESIGN_CONNECTION` and never embeds passwords.

#### Production schema and RLS

PostgreSQL keeps every WakeMesh object in the private `wakemesh` schema — never `public`, and never
listed in a Supabase "Exposed schemas" entry. Deployment order is fixed:

1. **Bootstrap once** as the project's `postgres` role (a CREATEROLE/BYPASSRLS admin, not a superuser):
   `psql ... -f deploy/postgres/00-bootstrap.sql`. It creates `wakemesh_migrator` (owns the schema and
   every object) and `wakemesh_api` (runtime; non-owner, NOINHERIT, NOBYPASSRLS), then sets the schema
   ACL. It is deliberately **not idempotent**: a rerun fails on "role already exists" instead of
   silently adopting or recreating an existing role/schema. Set passwords out of band with
   `\password`; never in Git, migrations, or scripts.
2. **Migrate as `wakemesh_migrator`** over a direct or session-mode connection. `InitialCreate` fails
   closed unless `current_user = 'wakemesh_migrator'` and both roles already exist.
3. **Verify** as `postgres`: `psql ... -f deploy/postgres/90-verify.sql` (read-only, raises on any
   violation). Require exit 0 before starting the app.

`InitialCreate` enables row level security (ENABLE, not FORCE) on all seven business tables, grants
`SELECT, INSERT, UPDATE, DELETE` to `wakemesh_api`, and creates exactly one permissive `ALL` policy
for it. `__EFMigrationsHistory` lives in `wakemesh` as well, with RLS on and **no** grant and **no**
policy, so the runtime role gets `42501`. `anon`, `authenticated`, `service_role`, and `PUBLIC` get
nothing on the schema.

The local opt-in tests mirror that shape: `postgres` is a non-superuser
(CREATEROLE + CREATEDB + BYPASSRLS + `pg_read_all_data`), the real bootstrap script runs once in a
template database, migrations run as the migrator, and the runtime/HTTP/production-dispatch tests
connect as `wakemesh_api`. A superuser is never used as evidence that RLS works.

## PostgreSQL root CA (TLS `SSL Mode=VerifyFull`)

The hosted database presents a certificate chain that terminates in the Supabase project CA
(`Supabase Root 2021 CA`), which no OS trust store contains. `SSL Mode=VerifyFull` therefore needs
an explicit `Root Certificate`. The CA file is **not** in the repository — it is passed in at build
time from an external path:

```bash
dotnet publish src/WakeMesh.Api/WakeMesh.Api.csproj \
  -c Release -r win-x86 --self-contained true \
  -p:WakeMeshDatabaseCaFile=/path/to/prod-ca-2021.crt \
  -o <publish-dir>
```

| Situation | Result |
|-----------|--------|
| `WakeMeshDatabaseCaFile` unset (default) | Nothing is packaged; builds/tests behave exactly as before (the disposable socket-PG tests need no TLS) |
| Set to an existing file | Linked into build output and publish output as `certs/supabase-ca.crt` (`PreserveNewest`) |
| Set to a missing path | Build **and** publish fail with `WAKEMESH_CA_FILE_MISSING` — an app that cannot verify the server is never produced |

The connection string then carries the **relative** value, so nothing depends on the App Service
content directory (never hardcode `D:\home\site\wwwroot`, and never rely on the process CWD):

```text
Root Certificate=certs/supabase-ca.crt;SSL Mode=VerifyFull
```

`PostgresConfiguration.ResolveConnectionString` (shared by the host and both design-time factories)
anchors a relative `Root Certificate` on `AppContext.BaseDirectory`, preserves an absolute one
verbatim, and fails closed with a path-only exception if the file is missing. It never injects a CA
when the keyword is absent, never relaxes `SSL Mode`, never re-signs the connection string beyond
Npgsql's own builder output, and never touches any other keyword (password, user, pooling stay as
configured). There is no custom certificate or hostname-verification callback: Npgsql performs the
verification.

A `win-x86` self-contained publish produces a 32-bit apphost, so the Windows App Service must run the
site as 32-bit (`use32BitWorkerProcess=true`); `web.config` is generated as
`processPath=".\WakeMesh.Api.exe"` + `hostingModel="inprocess"`.

## Run tests

```bash
export DOTNET_ROOT="$(pwd)/.tools/dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_HOME="$(pwd)/.tools/cli-home"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet test tests/WakeMesh.Api.Tests/WakeMesh.Api.Tests.csproj
```

Real PostgreSQL checks are opt-in and skipped unless `WAKEMESH_TEST_POSTGRES` is set. Use only a
disposable instance (socket-only, 0700 dirs, trust auth, fictional fixture user) — never a system
or shared PostgreSQL service:

```bash
# One-shot: private mktemp root, runs the tests, always stops and removes it.
./scripts/pg-test-local.sh run tests/WakeMesh.Api.Tests/WakeMesh.Api.Tests.csproj

# Or manage one instance in a dedicated directory you own (no shared default root):
export WAKEMESH_PG_RUN_ROOT="$(mktemp -d)/pg17"
./scripts/pg-test-local.sh start
eval "$(./scripts/pg-test-local.sh env)"   # exports the connection string + run id
dotnet test tests/WakeMesh.Api.Tests/WakeMesh.Api.Tests.csproj
./scripts/pg-test-local.sh stop
```

`start` / `stop` / `env` / `status` require an explicit `WAKEMESH_PG_RUN_ROOT`. Ownership is
proven from a parsed (never sourced) state file plus the live postmaster's pid, argv, and datadir;
a non-empty root whose ownership cannot be proven is left untouched and the command fails.
Repeated `start` on a running root requires `WAKEMESH_PG_RUN_ID` (printed by `env`) to confirm you
own it, so two runs cannot silently share one root, and `stop` never deletes state it does not own.

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

SQL Server migrations under `Migrations/` (frozen lineage). PostgreSQL migrations under
`src/WakeMesh.Api.PostgresMigrations/Migrations/` (`InitialCreate` only). SQLite tests use
`EnsureCreated` only — do not treat that as SQL Server or PostgreSQL verification.

Real PostgreSQL checks are opt-in: see [Run tests](#run-tests) for the disposable runner
(`./scripts/pg-test-local.sh run ...`) and the explicit `WAKEMESH_PG_RUN_ROOT` flow.
