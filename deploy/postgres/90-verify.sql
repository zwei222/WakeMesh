-- WakeMesh PostgreSQL security verification (read-only).
--
-- Run as the project's `postgres` role after `dotnet ef database update` (or the idempotent
-- migration script) succeeded:
--
--   psql "host=... port=5432 dbname=postgres user=postgres" -f deploy/postgres/90-verify.sql
--
-- Every check reads catalogs only and RAISEs on the first violation (psql exits non-zero with
-- ON_ERROR_STOP). No DDL and no data changes -- it can be run at any time, including in
-- production, without altering anything.
--
-- Expected shape after migration:
--   * schema `wakemesh` owned by wakemesh_migrator, USAGE only for wakemesh_api, nothing for PUBLIC;
--   * 7 business tables + __EFMigrationsHistory, all owned by the migrator, RLS enabled, not forced;
--   * business tables: S/I/U/D for wakemesh_api + exactly one permissive ALL policy for it;
--   * history table: RLS on, no grants at all and no policy;
--   * no Data API role (anon/authenticated/service_role/authenticator) can reach the schema.
\set ON_ERROR_STOP on

-- V1: both roles exist and carry exactly the intended attributes.
DO $wm_verify$
DECLARE
    r record;
BEGIN
    IF (SELECT count(*) FROM pg_catalog.pg_roles
        WHERE rolname IN ('wakemesh_migrator', 'wakemesh_api')) <> 2 THEN
        RAISE EXCEPTION 'WakeMesh verify V1: wakemesh_migrator and wakemesh_api must both exist';
    END IF;

    FOR r IN
        SELECT rolname, rolcanlogin, rolsuper, rolbypassrls, rolcreaterole, rolcreatedb,
               rolreplication, rolinherit
        FROM pg_catalog.pg_roles
        WHERE rolname IN ('wakemesh_migrator', 'wakemesh_api')
    LOOP
        IF NOT r.rolcanlogin THEN
            RAISE EXCEPTION 'WakeMesh verify V1: role % must be LOGIN', r.rolname;
        END IF;
        IF r.rolsuper OR r.rolbypassrls OR r.rolcreaterole OR r.rolcreatedb
           OR r.rolreplication OR r.rolinherit THEN
            RAISE EXCEPTION
                'WakeMesh verify V1: role % has a forbidden attribute (super=%, bypassrls=%, createrole=%, createdb=%, replication=%, inherit=%)',
                r.rolname, r.rolsuper, r.rolbypassrls, r.rolcreaterole, r.rolcreatedb,
                r.rolreplication, r.rolinherit;
        END IF;
    END LOOP;
END
$wm_verify$;

-- V2: the wakemesh roles are members of nothing, and nobody holds INHERIT or SET on them
-- (an ADMIN-only membership left by the platform is fine).
DO $wm_verify$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_auth_members m
        JOIN pg_catalog.pg_roles r ON r.oid = m.member
        WHERE r.rolname IN ('wakemesh_migrator', 'wakemesh_api')
    ) THEN
        RAISE EXCEPTION 'WakeMesh verify V2: a wakemesh role is a member of another role';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_auth_members m
        JOIN pg_catalog.pg_roles r ON r.oid = m.roleid
        WHERE r.rolname IN ('wakemesh_migrator', 'wakemesh_api')
          AND (m.inherit_option OR m.set_option)
    ) THEN
        RAISE EXCEPTION 'WakeMesh verify V2: a role holds INHERIT or SET on a wakemesh role';
    END IF;
END
$wm_verify$;

-- V3: schema `wakemesh` exists and is owned by wakemesh_migrator.
DO $wm_verify$
DECLARE
    v_owner text;
BEGIN
    SELECT pg_catalog.pg_get_userbyid(n.nspowner) INTO v_owner
    FROM pg_catalog.pg_namespace n
    WHERE n.nspname = 'wakemesh';

    IF v_owner IS NULL THEN
        RAISE EXCEPTION 'WakeMesh verify V3: schema wakemesh does not exist';
    END IF;
    IF v_owner <> 'wakemesh_migrator' THEN
        RAISE EXCEPTION 'WakeMesh verify V3: schema wakemesh is owned by %, expected wakemesh_migrator', v_owner;
    END IF;
END
$wm_verify$;

-- V4: schema ACL is exactly owner + wakemesh_api USAGE, with no PUBLIC entry.
DO $wm_verify$
BEGIN
    IF NOT pg_catalog.has_schema_privilege('wakemesh_api', 'wakemesh', 'USAGE') THEN
        RAISE EXCEPTION 'WakeMesh verify V4: wakemesh_api lacks USAGE on schema wakemesh';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_namespace n,
             pg_catalog.aclexplode(coalesce(n.nspacl, pg_catalog.acldefault('n', n.nspowner))) a
        WHERE n.nspname = 'wakemesh'
          AND (
                a.grantee = 0
             OR (a.grantee <> n.nspowner
                 AND a.grantee <> (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = 'wakemesh_api'))
             OR (a.grantee = (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = 'wakemesh_api')
                 AND a.privilege_type <> 'USAGE')
          )
    ) THEN
        RAISE EXCEPTION
            'WakeMesh verify V4: schema wakemesh ACL has an unexpected grantee or privilege (only owner + wakemesh_api USAGE allowed)';
    END IF;
END
$wm_verify$;

-- V5: every relation in `wakemesh` is a migrator-owned table with RLS enabled and NOT forced.
DO $wm_verify$
DECLARE
    v_tables int;
BEGIN
    SELECT count(*) INTO v_tables
    FROM pg_catalog.pg_class c
    JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'wakemesh' AND c.relkind = 'r';

    IF v_tables < 8 THEN
        RAISE EXCEPTION 'WakeMesh verify V5: expected at least 8 tables in wakemesh, found %', v_tables;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'wakemesh'
          AND c.relkind IN ('r', 'p', 'v', 'm', 'f', 'S')
          AND (
                c.relkind <> 'r'
             OR NOT c.relrowsecurity
             OR c.relforcerowsecurity
             OR c.relowner <> (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = 'wakemesh_migrator')
          )
    ) THEN
        RAISE EXCEPTION
            'WakeMesh verify V5: a relation in wakemesh is not a migrator-owned, RLS-enabled, non-FORCE table';
    END IF;
END
$wm_verify$;

-- V6: table ACLs are owner + wakemesh_api S/I/U/D on business tables, and nothing on history.
DO $wm_verify$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace,
             LATERAL pg_catalog.aclexplode(coalesce(c.relacl, pg_catalog.acldefault('r', c.relowner))) a
        WHERE n.nspname = 'wakemesh'
          AND c.relkind = 'r'
          AND (
                a.grantee = 0
             OR (c.relname = '__EFMigrationsHistory' AND a.grantee <> c.relowner)
             OR (c.relname <> '__EFMigrationsHistory'
                 AND a.grantee <> c.relowner
                 AND (a.grantee <> (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = 'wakemesh_api')
                      OR a.privilege_type NOT IN ('SELECT', 'INSERT', 'UPDATE', 'DELETE')))
          )
    ) THEN
        RAISE EXCEPTION
            'WakeMesh verify V6: a table ACL in wakemesh has an unexpected grantee or privilege';
    END IF;

    -- The api really holds all four DML privileges on every business table...
    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'wakemesh' AND c.relkind = 'r' AND c.relname <> '__EFMigrationsHistory'
          AND NOT (
                pg_catalog.has_table_privilege('wakemesh_api', c.oid, 'SELECT')
            AND pg_catalog.has_table_privilege('wakemesh_api', c.oid, 'INSERT')
            AND pg_catalog.has_table_privilege('wakemesh_api', c.oid, 'UPDATE')
            AND pg_catalog.has_table_privilege('wakemesh_api', c.oid, 'DELETE')
          )
    ) THEN
        RAISE EXCEPTION 'WakeMesh verify V6: wakemesh_api is missing S/I/U/D on a business table';
    END IF;

    -- ...and no privilege at all on the migration history table.
    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'wakemesh' AND c.relkind = 'r' AND c.relname = '__EFMigrationsHistory'
          AND (
                pg_catalog.has_table_privilege('wakemesh_api', c.oid, 'SELECT')
             OR pg_catalog.has_table_privilege('wakemesh_api', c.oid, 'INSERT')
             OR pg_catalog.has_table_privilege('wakemesh_api', c.oid, 'UPDATE')
             OR pg_catalog.has_table_privilege('wakemesh_api', c.oid, 'DELETE')
             OR pg_catalog.has_table_privilege('wakemesh_api', c.oid, 'TRUNCATE')
          )
    ) THEN
        RAISE EXCEPTION 'WakeMesh verify V6: wakemesh_api must have no privileges on __EFMigrationsHistory';
    END IF;
END
$wm_verify$;

-- V7: exactly one permissive ALL policy for wakemesh_api on each business table, none elsewhere.
DO $wm_verify$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_policy p
        JOIN pg_catalog.pg_class c ON c.oid = p.polrelid
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'wakemesh' AND c.relname = '__EFMigrationsHistory'
    ) THEN
        RAISE EXCEPTION 'WakeMesh verify V7: __EFMigrationsHistory must have no policy';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_class c
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'wakemesh' AND c.relkind = 'r' AND c.relname <> '__EFMigrationsHistory'
          AND (SELECT count(*) FROM pg_catalog.pg_policy p WHERE p.polrelid = c.oid) <> 1
    ) THEN
        RAISE EXCEPTION 'WakeMesh verify V7: every business table must have exactly one policy';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_policy p
        JOIN pg_catalog.pg_class c ON c.oid = p.polrelid
        JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'wakemesh' AND c.relkind = 'r'
          AND (
                NOT p.polpermissive
             OR p.polcmd <> '*'
             OR p.polroles <> ARRAY[(SELECT oid FROM pg_catalog.pg_roles WHERE rolname = 'wakemesh_api')]
          )
    ) THEN
        RAISE EXCEPTION
            'WakeMesh verify V7: a policy is not a single permissive ALL policy for wakemesh_api';
    END IF;
END
$wm_verify$;

-- V8: Data API roles cannot reach wakemesh, only the owner may CREATE there, and the api has no
-- CREATE on public.
DO $wm_verify$
DECLARE
    r record;
BEGIN
    FOR r IN
        SELECT rolname FROM pg_catalog.pg_roles
        WHERE rolname IN ('anon', 'authenticated', 'service_role', 'authenticator')
    LOOP
        IF pg_catalog.has_schema_privilege(r.rolname, 'wakemesh', 'USAGE')
           OR pg_catalog.has_schema_privilege(r.rolname, 'wakemesh', 'CREATE') THEN
            RAISE EXCEPTION 'WakeMesh verify V8: Data API role % has privileges on schema wakemesh', r.rolname;
        END IF;
    END LOOP;

    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_namespace n,
             pg_catalog.aclexplode(coalesce(n.nspacl, pg_catalog.acldefault('n', n.nspowner))) a
        WHERE n.nspname = 'wakemesh'
          AND a.privilege_type = 'CREATE'
          AND a.grantee <> n.nspowner
    ) THEN
        RAISE EXCEPTION 'WakeMesh verify V8: a non-owner role has CREATE on schema wakemesh';
    END IF;

    IF pg_catalog.has_schema_privilege('wakemesh_api', 'public', 'CREATE') THEN
        RAISE EXCEPTION 'WakeMesh verify V8: wakemesh_api must not have CREATE on schema public';
    END IF;
END
$wm_verify$;

-- V9: no routines (functions/procedures) in wakemesh.
DO $wm_verify$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM pg_catalog.pg_proc p
        JOIN pg_catalog.pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = 'wakemesh'
    ) THEN
        RAISE EXCEPTION 'WakeMesh verify V9: schema wakemesh must contain no routines';
    END IF;
END
$wm_verify$;
