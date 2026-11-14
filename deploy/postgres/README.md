# PostgreSQL deployment (Supabase-compatible)

Three files, run in this order, against the project database. Nothing here contains a password or a
project ref; host/ref come from the operator's environment or secret store.

| File | Run as | Purpose |
|------|--------|---------|
| `00-bootstrap.sql` | `postgres` | one-time roles + private schema + schema ACL |
| `migrations` (`dotnet ef database update`) | `wakemesh_migrator` | tables, indexes, RLS, grants, policies |
| `90-verify.sql` | `postgres` | read-only check; non-zero exit means do not deploy |

## 1. Bootstrap (once)

```bash
psql "host=... port=5432 dbname=postgres user=postgres" -f deploy/postgres/00-bootstrap.sql
# then, in the same psql session (passwords never touch Git):
#   \password wakemesh_migrator
#   \password wakemesh_api
```

It creates:

- `wakemesh_migrator` — LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION
  NOBYPASSRLS, CONNECTION LIMIT 3. Owns schema `wakemesh` and every object in it.
- `wakemesh_api` — same attributes, CONNECTION LIMIT 20. Runtime only: USAGE on `wakemesh` plus
  SELECT/INSERT/UPDATE/DELETE on the business tables. Owns nothing.

The script is **not idempotent by design**. A second run fails on `role ... already exists`; it never
drops, recreates, or adopts an existing role or schema. If bootstrap fails part-way, inspect and clean
up manually — do not just re-run it.

Why the SET self-grant: on PostgreSQL 16+ a role created by `postgres` gives the creator only ADMIN
(with INHERIT/SET off). `CREATE SCHEMA ... AUTHORIZATION wakemesh_migrator` and owner-side GRANTs
therefore need `GRANT wakemesh_migrator TO postgres WITH INHERIT FALSE, SET TRUE` first; the script
revokes that SET grant again at the end.

## 2. Migrate

```bash
export WAKEMESH_DESIGN_PROVIDER=Postgres
export WAKEMESH_DESIGN_CONNECTION='host=... port=5432 dbname=postgres user=wakemesh_migrator ...'
dotnet ef database update -p src/WakeMesh.Api.PostgresMigrations -s src/WakeMesh.Api.PostgresMigrations
```

`InitialCreate` aborts unless both roles exist and `current_user = 'wakemesh_migrator'`. It also
enables RLS on the seven business tables with one permissive ALL policy for `wakemesh_api`, and
enables RLS on `__EFMigrationsHistory` with no policy and no grant.

## 3. Verify (read-only)

```bash
psql "host=... port=5432 dbname=postgres user=postgres" -f deploy/postgres/90-verify.sql
echo $?   # must be 0
```

V1–V9 check role attributes, memberships, schema ownership and ACL, table ownership/RLS flags, table
ACLs, policies, Data API role reachability, and the absence of routines. Any violation raises and psql
exits non-zero. No DDL and no data changes — safe to run at any time.

## Supabase settings (outside Git)

Leave **Automatically expose new tables** off and **Enable Data API** off, and never add `wakemesh`
to Exposed schemas. Those are extra hardening only; the GRANT/RLS layer above is the primary barrier.
