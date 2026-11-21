\set ON_ERROR_STOP on

BEGIN;
CREATE ROLE darim_test LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
CREATE SCHEMA darim_test AUTHORIZATION darim_test;
ALTER ROLE darim_test IN DATABASE wishlist SET search_path TO darim_test;
GRANT CONNECT ON DATABASE wishlist TO darim_test;
-- Preserve access for the production application and backup role.
GRANT USAGE ON SCHEMA public TO wishlist;
REVOKE USAGE, CREATE ON SCHEMA public FROM PUBLIC;
COMMIT;

\password darim_test
