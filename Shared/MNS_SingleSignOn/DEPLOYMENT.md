# Single sign-on for MNS applications

Global (Hyperlink), Tenders, Budgets, Fleet, Account Manager, Phones (`FancilPhones`) and Ticket System (`BIDevHub`) now support one encrypted ASP.NET Identity application cookie. Sign in once in Global, then open another participating application in the same browser. Existing application permissions still apply; SSO does not grant roles or bypass two-factor sign-in. An authenticated visit to an application's login page enters its normal workspace using a validated local return URL.

Signing out of any participating application clears the shared cookie and revokes that browser session in ASP_AUTH. Subsequent HTTP requests with a copied or stale cookie are rejected. Connected Blazor circuits revalidate every 30 seconds by default and become signed out when the session is revoked. Separate browser sessions remain independent. Inactivity expires the cookie after three hours; active sessions have a twelve-hour absolute limit. These limits are configurable. Expired sessions cannot be revived by a concurrent principal refresh.

The four PHP applications (Attendance, Performance, Stock and E-Learning) retain their existing authentication. They do not understand ASP.NET Identity cookies. Integrating them requires a supported identity adapter or federation at those applications; no passwords are forwarded by Global.

## Source and rollout

All changes are on `Ruhan-Project` in these three independent repositories:

| Repository | Applications |
| --- | --- |
| `D:\MNS-Project` | Global, Tenders, Budgets, Fleet, Account Manager |
| `C:\Users\ruhan\source\repos\BI-MNS\FancilPhones` | Phones |
| `C:\Users\ruhan\source\repos\BI-MNS\BIDevHub` | Ticket System |

`MNS_SingleSignOn` in the main repository is the canonical shared project. The external repositories contain identical vendored copies in `Shared/MNS_SingleSignOn`, referenced relatively so each repository builds independently. When changing the shared project, synchronize its `.cs`, `.csproj` and `Directory.Packages.props` files to both copies and run the integration tests. BIDevHub's Microsoft .NET 10 packages are aligned to 10.0.13 to match the shared EF dependency.

SSO defaults to **disabled** until server configuration is supplied. This preserves standalone login on developer machines and during preparation. Publish all seven applications before enabling it everywhere. Existing standalone cookies do not become shared cookies; users sign in once again after rollout.

## IIS server configuration

1. Configure valid HTTPS bindings and HTTP-to-HTTPS redirects for every participating host, including `phones.multinetsystems.co.za` and `tickets.multinetsystems.co.za`. All applications already use HTTPS redirection middleware; IIS must also have the correct certificate and binding. If TLS terminates at a trusted reverse proxy, configure that deployment's trusted forwarded headers so ASP.NET sees HTTPS. Do not enable insecure shared cookies as a workaround.
2. Verify each application's Identity connection resolves to the **same ASP_AUTH instance and database**, with compatible `AspNetUsers`, `AspNetRoles` and `AspNetUserTokens` tables. The connection setting names already used by each application stay unchanged. Do not confuse the business/ticket/phone databases with the authentication database.
3. Create a persistent shared Data Protection key directory **outside every publish directory**, for example `D:\MNS-Shared\IdentityKeys`. On separate servers use one shared UNC directory. Grant read/write access only to participating IIS application-pool identities and deployment administrators. Retain and back up these keys across deployments; do not clear the directory during publish.
4. Install the same key-protection certificate, including its private key, on every participating server. Grant the application-pool identities private-key read permission. This is a Data Protection encryption certificate; it can be distinct from the website's TLS certificate. All applications must be able to read old keys after certificate rotation, so retain access to the previous certificates while their keys remain in use.
5. Apply the following section identically to each application's **server configuration**, replacing the example path and thumbprint, then recycle the application pools. Keep private keys, certificate passwords and connection secrets out of source control.

```json
{
  "SingleSignOn": {
    "Enabled": true,
    "CookieDomain": ".multinetsystems.co.za",
    "KeyRingPath": "D:\\MNS-Shared\\IdentityKeys",
    "CertificateThumbprint": "REPLACE_WITH_INSTALLED_CERTIFICATE_THUMBPRINT",
    "IdleMinutes": 180,
    "MaximumSessionHours": 12,
    "CircuitRevalidationSeconds": 30
  }
}
```

Environment-variable equivalents use double underscores, for example `SingleSignOn__Enabled`, `SingleSignOn__KeyRingPath` and `SingleSignOn__CertificateThumbprint`. The thumbprint certificate lookup uses the current user's or local machine's Personal certificate store. Alternatively set an absolute `SingleSignOn__CertificatePath` and a secret `SingleSignOn__CertificatePassword` for a PFX, and leave `CertificateThumbprint` unset. Configure exactly one certificate source. The PFX must contain a private key and be readable only by the intended service identities.

The shared cookie name (`__Secure-MNS.SingleSignOn`), application encryption purpose (`MNS.SharedIdentity.v1`), authentication scheme (`Identity.Application`) and cookie path (`/`) are fixed in the shared project. Every participant must use the same deployed implementation and key ring. External-login, two-factor and antiforgery cookies remain application-specific. Do not configure a per-application `ITicketStore` for this shared cookie.

Domain cookies establish a trust boundary across the parent domain: all HTTPS subdomains, including legacy sites, must be trusted to receive the cookie. Restrict shared-key access to the seven participating .NET applications. For independently administered or untrusted subdomains, use a central OpenID Connect provider with separate host cookies instead of this design.

6. In Global's application management screen, update Phones and Ticket System launch URLs to `https://phones.multinetsystems.co.za/` and `https://tickets.multinetsystems.co.za/` after verifying the HTTPS bindings. The catalogue is database-backed; changing source seed URLs would not update existing entries. Other login-page launch URLs can remain because authenticated users now skip those pages.

## Database behavior

This SSO feature requires **no new SQL migration**. It uses the existing `AspNetUserTokens` composite key `(UserId, LoginProvider, Name)`. Records with `LoginProvider = 'MNS.SharedSso.v1'` store a random session identifier in `Name` and an absolute Unix expiry in `Value`. They contain no passwords or raw authentication cookies. Other Identity token providers and other users' rows are untouched. Sign-in prunes expired rows for that account; sign-out deletes only the matching session row. Database availability is required to establish and validate sessions; errors do not silently allow access.

The application identities need SELECT, INSERT and DELETE permission on those token rows in addition to their existing Identity permissions. Do not run a new Identity `EnsureCreated` or baseline migration against ASP_AUTH for SSO. Phones/Tickets' existing business-database migration behavior is independent of this change.

## Verify after deployment

1. Sign out of old sessions, sign in to Global over HTTPS, and open Tenders, Budgets, Fleet, Account Manager, Phones and Ticket System in the same browser. Authorized pages should open without another sign-in. An account without a required role must still receive the application's normal access-denied behavior.
2. Open a second browser session and sign in there. Sign out in the first browser from any participating application. Refresh the other applications in that first browser: authentication is required again. Already-open interactive pages become signed out within the configured circuit interval. The second browser stays signed in.
3. Recycle one application pool and repeat navigation. Persistent keys and the server-side session record must survive the restart.
4. Verify secure cookie attributes and absence of repeated login redirects. Check certificate private-key permissions, shared key paths, ASP_AUTH targets and server clocks if cookies are rejected. Never paste cookie values, PFX passwords or connection secrets into logs or support tickets.

For rollback, disable `SingleSignOn:Enabled` across all applications and recycle them. Applications resume standalone cookies. Require users to sign out and sign in after rollback or later re-enablement; do not treat old shared sessions as a permanent credential.

## Automated verification

The main test suite exercises real ASP.NET Identity cookie middleware across independent test hosts, a temporary certificate/key ring and isolated SQLite Identity stores. It checks custom user-type compatibility, role preservation, protected cookie attributes, session revocation, separate browser sessions, stamp refresh, absolute expiry, tampering, unregistered sessions, isolated encryption keys, local redirect validation and disabled configuration. These tests do not contact production databases or install certificates.

Reference: [Microsoft's shared authentication cookie guidance](https://learn.microsoft.com/en-us/aspnet/core/security/cookie-sharing?view=aspnetcore-10.0) and [Data Protection configuration](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0).
