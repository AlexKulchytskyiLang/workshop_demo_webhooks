// Shows which organisation the user picked in the portal, so the identity provider's page is not anonymous.
//
// DEMO TECHNIQUE: the portal writes a short-lived cookie "ih_tenant" = slug|name|portalOrigin. Browsers share cookies between
// ports of the same host, so the Keycloak page on localhost:8085 can read what the portal on localhost:5173 wrote.
// This is display-only; it grants nothing (the API trusts only the signed token).
// Production options: Keycloak Organizations (native, one realm), one realm or hostname per tenant, or a custom authenticator
// that reads the tenant from an authorization request parameter.
function showTenantBanner() {
  try {
    var match = document.cookie.match(/(?:^|;\s*)ih_tenant=([^;]*)/);
    if (!match) return;
    var parts = decodeURIComponent(match[1]).split('|');
    var name = parts[1] || parts[0];
    var portal = parts[2];
    if (!name) return;

    var banner = document.createElement('div');
    banner.className = 'ih-tenant-banner';
    banner.setAttribute('role', 'status');

    var line = document.createElement('span');
    line.appendChild(document.createTextNode('Signing in to '));
    var bold = document.createElement('b');
    bold.textContent = name; // textContent, never innerHTML: the cookie is user-controlled
    line.appendChild(bold);
    banner.appendChild(line);

    if (portal && /^https?:\/\/(localhost|127\.0\.0\.1)(:\d+)?$/.test(portal)) {
      var sub = document.createElement('span');
      sub.className = 'ih-sub';
      sub.appendChild(document.createTextNode('Not your organisation? '));
      var link = document.createElement('a');
      link.href = portal + '/select-tenant';
      link.textContent = 'Choose again';
      sub.appendChild(link);
      banner.appendChild(sub);
    }

    var anchor = document.querySelector('.pf-v5-c-login__main-header') || document.querySelector('#kc-header');
    if (anchor && anchor.parentNode) anchor.parentNode.insertBefore(banner, anchor.nextSibling);
  } catch (e) { /* the page works without the banner */ }
}

// Keycloak loads this script in <head>, before the page body exists: wait until the document is parsed.
if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', showTenantBanner);
else showTenantBanner();
