-- Local email + password accounts, for deployments that sign staff in without
-- Entra ID. A Local login is resolved exactly like an Entra one (issuer
-- 'spms-local', subject = the lower-cased email), so tenant, roles and
-- properties still come from SpMS; only the credential check is new.
ALTER TABLE core.principal_login
    ADD COLUMN password_hash text,
    ADD COLUMN must_change_password boolean NOT NULL DEFAULT false,
    ADD COLUMN failed_attempts integer NOT NULL DEFAULT 0,
    ADD COLUMN locked_until timestamptz;

ALTER TABLE core.principal_login DROP CONSTRAINT principal_login_login_type_ck;
ALTER TABLE core.principal_login ADD CONSTRAINT principal_login_login_type_ck
    CHECK (login_type IN ('EntraUser', 'EntraApplication', 'Local'));
ALTER TABLE core.principal_login ADD CONSTRAINT principal_login_failed_attempts_ck CHECK (failed_attempts >= 0);
ALTER TABLE core.principal_login ADD CONSTRAINT principal_login_local_has_password
    CHECK ((login_type = 'Local') = (password_hash IS NOT NULL));

COMMENT ON TABLE core.principal_login IS 'An identity that signs in as a principal: Entra user (oid), client-credentials app, or a local email + password account. [§24 OIDC/SSO, MFA, service accounts; §Security and audit; local email + password sign-in (no Entra)]';
COMMENT ON COLUMN core.principal_login.idp_subject IS 'Entra object id (oid), application object id, or the lower-cased email of a local account';
COMMENT ON COLUMN core.principal_login.password_hash IS 'Local accounts only: PBKDF2-SHA256, salted, iteration count in the value; never the password';
COMMENT ON COLUMN core.principal_login.must_change_password IS 'a temporary password set by an administrator must be replaced at next sign-in';
COMMENT ON COLUMN core.principal_login.locked_until IS 'set after repeated wrong passwords; sign-in refused until then';
