"""
Generates infra/keycloak/realms/invoicehub-realm.json from infra/seed/seed.json (tenants, users, demo password).
Run after editing the seed:   python infra/keycloak/generate_realm.py

Design (decided in M0, see demo/docs/SPIKE-0.md):
  * one realm, tenant carried as the user attribute `tenant_id` and mapped into the access token;
  * the user profile DECLARES tenant_id / tenant_slug (undeclared attributes are silently dropped by Keycloak);
  * client `portal` = public SPA client, authorization code + PKCE;
  * client `tenancy-admin` = confidential service account used by the API to create users on registration;
  * seeded users get fixed ids so the database profile rows match Keycloak without a lookup.
"""
import json
import pathlib

HERE = pathlib.Path(__file__).resolve().parent
SEED = json.loads((HERE.parent / "seed" / "seed.json").read_text(encoding="utf-8"))
tenants = {t["slug"]: t for t in SEED["tenants"]}

PORTAL_ORIGINS = ["http://localhost:5173"]
ADMIN_CLIENT_SECRET = "invoicehub-dev-admin-secret"  # demo only; in production it comes from a secret store


def mappers():
    attr = lambda name: {
        "name": name, "protocol": "openid-connect", "protocolMapper": "oidc-usermodel-attribute-mapper", "consentRequired": False,
        "config": {"user.attribute": name, "claim.name": name, "jsonType.label": "String", "id.token.claim": "true",
                   "access.token.claim": "true", "userinfo.token.claim": "true", "introspection.token.claim": "true"}}
    return [
        attr("tenant_id"),
        attr("tenant_slug"),
        {"name": "roles", "protocol": "openid-connect", "protocolMapper": "oidc-usermodel-realm-role-mapper", "consentRequired": False,
         "config": {"claim.name": "roles", "multivalued": "true", "jsonType.label": "String", "id.token.claim": "true",
                    "access.token.claim": "true", "userinfo.token.claim": "true", "introspection.token.claim": "true"}},
        {"name": "api-audience", "protocol": "openid-connect", "protocolMapper": "oidc-audience-mapper", "consentRequired": False,
         "config": {"included.custom.audience": "invoicehub-api", "access.token.claim": "true", "id.token.claim": "false",
                    "introspection.token.claim": "true"}},
    ]


permission = {"view": ["admin", "user"], "edit": ["admin", "user"]}
profile = {"attributes": [
    {"name": "username", "displayName": "${username}", "permissions": permission, "multivalued": False,
     "validations": {"length": {"min": 3, "max": 255}, "username-prohibited-characters": {}, "up-username-not-idn-homograph": {}}},
    {"name": "email", "displayName": "${email}", "permissions": permission, "multivalued": False, "required": {"roles": ["user"]},
     "validations": {"email": {}, "length": {"max": 255}}},
    {"name": "firstName", "displayName": "${firstName}", "permissions": permission, "multivalued": False, "required": {"roles": ["user"]},
     "validations": {"length": {"max": 255}, "person-name-prohibited-characters": {}}},
    {"name": "lastName", "displayName": "${lastName}", "permissions": permission, "multivalued": False, "required": {"roles": ["user"]},
     "validations": {"length": {"max": 255}, "person-name-prohibited-characters": {}}},
    # users can see their tenant but only the platform (admin) can change it
    {"name": "tenant_id", "displayName": "Tenant", "validations": {}, "permissions": {"view": ["admin", "user"], "edit": ["admin"]}, "multivalued": False},
    {"name": "tenant_slug", "displayName": "Tenant address", "validations": {}, "permissions": {"view": ["admin", "user"], "edit": ["admin"]}, "multivalued": False},
], "groups": []}

users = []
for u in SEED["users"]:
    t = tenants[u["tenant"]]
    users.append({
        "id": u["id"], "username": u["email"], "email": u["email"], "firstName": u["firstName"], "lastName": u["lastName"],
        "enabled": True, "emailVerified": True,
        "attributes": {"tenant_id": [t["id"]], "tenant_slug": [t["slug"]]},
        "realmRoles": [u["role"]],
        "credentials": [{"type": "password", "value": SEED["demoPassword"], "temporary": False}],
    })
users.append({
    "username": "service-account-tenancy-admin", "enabled": True, "serviceAccountClientId": "tenancy-admin",
    # view-realm is needed to read role representations; manage-users to create users and map roles (Spike 0)
    "clientRoles": {"realm-management": ["manage-users", "view-users", "query-users", "view-realm"]},
})

realm = {
    "realm": "invoicehub",
    "displayName": "InvoiceHub",
    "loginTheme": "invoicehub",             # infra/keycloak/themes/invoicehub (mounted by the AppHost)
    "enabled": True,
    "sslRequired": "external",
    "registrationAllowed": False,          # tenants register through the platform API, which also creates the tenant row
    "loginWithEmailAllowed": True,
    "duplicateEmailsAllowed": False,
    "resetPasswordAllowed": True,
    "bruteForceProtected": True,
    "failureFactor": 10,
    "passwordPolicy": "length(12) and notUsername",
    "accessTokenLifespan": 300,            # 5 minutes: the SPA renews with the refresh token
    "ssoSessionIdleTimeout": 1800,
    "ssoSessionMaxLifespan": 36000,
    "roles": {"realm": [{"name": n, "description": f"Tenant role: {n}"} for n in ["owner", "admin", "developer", "support", "viewer"]]},
    "clients": [
        {
            "clientId": "portal", "name": "InvoiceHub tenant portal", "enabled": True, "publicClient": True,
            "standardFlowEnabled": True, "directAccessGrantsEnabled": False, "implicitFlowEnabled": False, "serviceAccountsEnabled": False,
            "redirectUris": [o + "/*" for o in PORTAL_ORIGINS], "webOrigins": PORTAL_ORIGINS,
            "attributes": {"pkce.code.challenge.method": "S256", "post.logout.redirect.uris": "##".join(o + "/*" for o in PORTAL_ORIGINS)},
            "protocolMappers": mappers(),
        },
        {
            "clientId": "tenancy-admin", "name": "Tenancy admin (service account)", "enabled": True, "publicClient": False,
            "secret": ADMIN_CLIENT_SECRET, "serviceAccountsEnabled": True, "standardFlowEnabled": False, "directAccessGrantsEnabled": False,
        },
    ],
    "users": users,
    "components": {"org.keycloak.userprofile.UserProfileProvider": [{
        "providerId": "declarative-user-profile", "subComponents": {},
        "config": {"kc.user.profile.config": [json.dumps(profile)]}}]},
}

out = HERE / "realms" / "invoicehub-realm.json"
out.write_text(json.dumps(realm, indent=2), encoding="utf-8")
print(f"wrote {out} ({len(users) - 1} users, {len(tenants)} tenants)")

# Test variant: the same realm plus a client that allows the password grant, so integration tests can sign in without a browser.
# It is never mounted by the normal AppHost profile (Demo:Profile=test only).
test_realm = json.loads(json.dumps(realm))
test_realm["clients"].append({
    "clientId": "it-direct", "name": "Integration tests only", "enabled": True, "publicClient": True,
    "standardFlowEnabled": False, "directAccessGrantsEnabled": True, "protocolMappers": mappers(),
})
test_out = HERE / "realms-test" / "invoicehub-realm.json"
test_out.parent.mkdir(exist_ok=True)
test_out.write_text(json.dumps(test_realm, indent=2), encoding="utf-8")
print(f"wrote {test_out}")
