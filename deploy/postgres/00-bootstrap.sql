-- WakeMesh PostgreSQL bootstrap: one-time role + schema setup before the first migration.
--
-- Run ONCE per Supabase project, as the project's `postgres` role (a CREATEROLE/BYPASSRLS admin,
-- not a superuser), over a direct or session-mode connection:
--
--   psql "host=... port=5432 dbname=postgres user=postgres" -f deploy/postgres/00-bootstrap.sql
--
-- Deliberately NOT idempotent: a second run fails on "role ... already exists". This is
-- fail-closed on purpose -- it never drops, recreates, or silently adopts an existing role or
-- schema, so a botched or partial bootstrap cannot be papered over by re-running it.
--
-- No passwords here. Set them out of band in the same psql session:
--   \password wakemesh_migrator
--   \password wakemesh_api
-- psql hashes them client-side with SCRAM; never put them in Git, migrations, or scripts.
--
-- The ordered setup below handles the PG16+ SET self-grant requirement: a role
-- created by `postgres` only grants it ADMIN (INHERIT/SET off), so CREATE SCHEMA ... AUTHORIZATION
-- and owner-side GRANTs require first granting `postgres` the SET option on the migrator role.
\set ON_ERROR_STOP on

BEGIN;

CREATE ROLE wakemesh_migrator LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS CONNECTION LIMIT 3;
CREATE ROLE wakemesh_api      LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS CONNECTION LIMIT 20;

-- PG16+: the creator holds ADMIN only; AUTHORIZATION and owner-side GRANTs need SET
-- (otherwise: "must be able to SET ROLE wakemesh_migrator").
GRANT wakemesh_migrator TO postgres WITH INHERIT FALSE, SET TRUE;

CREATE SCHEMA wakemesh AUTHORIZATION wakemesh_migrator;

-- REQUIRED: a GRANT issued by postgres here only WARNs "no privileges were granted" and changes
-- nothing, because postgres does not own the schema. Run the grants as the owner.
SET LOCAL ROLE wakemesh_migrator;
REVOKE ALL ON SCHEMA wakemesh FROM PUBLIC;
GRANT USAGE ON SCHEMA wakemesh TO wakemesh_api;
-- Hygiene: new functions in this schema are not executable by PUBLIC.
ALTER DEFAULT PRIVILEGES FOR ROLE wakemesh_migrator REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
RESET ROLE;

-- Drop only the SET grant; the ADMIN membership granted by the platform remains.
REVOKE wakemesh_migrator FROM postgres;

COMMIT;
