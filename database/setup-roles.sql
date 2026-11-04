DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'trustvault_migrator') THEN
        CREATE ROLE trustvault_migrator LOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'trustvault_runtime') THEN
        CREATE ROLE trustvault_runtime LOGIN;
    END IF;
END
$$;

ALTER ROLE trustvault_migrator
    WITH LOGIN PASSWORD 'migrator_local'
    NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
ALTER ROLE trustvault_runtime
    WITH LOGIN PASSWORD 'runtime_local'
    NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;

ALTER DATABASE trustvault OWNER TO trustvault_migrator;
ALTER SCHEMA public OWNER TO trustvault_migrator;

DO $$
DECLARE
    object record;
BEGIN
    FOR object IN
        SELECT relation.relname, relation.relkind
        FROM pg_class relation
        JOIN pg_namespace schema ON schema.oid = relation.relnamespace
        WHERE schema.nspname = 'public'
          AND relation.relowner = 'trustvault_app'::regrole
          AND relation.relkind IN ('r', 'p', 'v', 'm', 'f')
    LOOP
        EXECUTE format(
            'ALTER %s %I.%I OWNER TO trustvault_migrator',
            CASE WHEN object.relkind = 'f' THEN 'FOREIGN TABLE' ELSE 'TABLE' END,
            'public',
            object.relname);
    END LOOP;

    FOR object IN
        SELECT sequence.relname
        FROM pg_class sequence
        JOIN pg_namespace schema ON schema.oid = sequence.relnamespace
        WHERE schema.nspname = 'public'
          AND sequence.relowner = 'trustvault_app'::regrole
          AND sequence.relkind = 'S'
    LOOP
        EXECUTE format(
            'ALTER SEQUENCE %I.%I OWNER TO trustvault_migrator',
            'public',
            object.relname);
    END LOOP;

    IF to_regprocedure('public.prevent_audit_event_mutation()') IS NOT NULL THEN
        ALTER FUNCTION public.prevent_audit_event_mutation() OWNER TO trustvault_migrator;
    END IF;
END
$$;

GRANT CONNECT ON DATABASE trustvault TO trustvault_migrator, trustvault_runtime;
GRANT USAGE ON SCHEMA public TO trustvault_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE
    ON ALL TABLES IN SCHEMA public TO trustvault_runtime;
GRANT USAGE, SELECT, UPDATE
    ON ALL SEQUENCES IN SCHEMA public TO trustvault_runtime;

DO $$
BEGIN
    IF to_regclass('public.audit_events') IS NOT NULL THEN
        REVOKE ALL PRIVILEGES ON TABLE public.audit_events FROM trustvault_runtime;
        GRANT SELECT, INSERT ON TABLE public.audit_events TO trustvault_runtime;
    END IF;
END
$$;

ALTER DEFAULT PRIVILEGES FOR ROLE trustvault_migrator IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO trustvault_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE trustvault_migrator IN SCHEMA public
    GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO trustvault_runtime;
