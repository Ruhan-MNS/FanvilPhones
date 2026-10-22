# FancilPhones

ASP.NET Core 10 / Blazor Server application for managing Fanvil handsets, a shared phonebook, and synchronization history.

## Interface

The voice workspace uses Radzen icons and Radzen dialogs with a custom warm graphite and amber theme. Every container, control, status badge, and modal has square corners. Typography and icon fonts are served locally through the Radzen package. MudBlazor is not a dependency.

The responsive shell includes a collapsible desktop sidebar and mobile navigation. Fleet, phonebook, and activity tables include search, filters, pagination, and contained horizontal scrolling on smaller screens. Editors use validated Blazor forms; text fields update validation while typing. Imports, deletions, SIP pushes, and fleet synchronization show explicit confirmation dialogs. Informational notifications dismiss after seven seconds; warnings and errors remain until dismissed.

The Switchboard keys page uses the same shared dropdowns, buttons, typography, and theme tokens. Its scoped stylesheet provides a responsive fleet selector, five-page key editor, phone-specific saved drafts and paged before/after sync comparisons.

Shared UI components live in `FancilPhones/Components/Shared`, circuit-scoped feedback lives in `FancilPhones/UI`, and visual tokens and layout rules live in `FancilPhones/wwwroot/app.css`. `AppDialog` adapts controlled component state to Radzen's dialog lifecycle; it does not implement a separate modal system.

## Architecture

The solution follows BIDevHub's layered approach, with dependencies directed toward application contracts and domain models:

```mermaid
flowchart TD
    Web[Blazor pages and HTTP endpoints] --> Application[Application contracts and import/export formats]
    Infrastructure[SQL Server persistence and Fanvil integrations] --> Application
    Application --> Domain[Phone, Contact, SyncRun]
    Infrastructure --> Phones[MNSGlobalDev / FancilPhones schema]
    Infrastructure --> Identity[ASP_AUTH / standard Identity tables]
```

- `src/FancilPhones.Domain`: phone, contact, and activity models.
- `src/FancilPhones.Application`: use-case contracts, authorization boundary, network range parsing, and CSV/XLSX formats.
- `src/FancilPhones.Infrastructure`: separate EF contexts, SQL migrations, transactional data operations, legacy import, network discovery, and handset communication.
- `FancilPhones`: Blazor UI, authentication endpoints, request/circuit user access, and dependency injection composition.
- `tests/FancilPhones.Tests`: isolated persistence and HTTP authentication tests, plus explicit read-only live checks.

Blazor components use application interfaces, not EF contexts. Services enforce authorization before database mutations or handset configuration changes. Each database operation uses its own context, avoiding circuit-long EF tracking and concurrent context use during fleet syncs.

## Database configuration

The two required connection names are `ASP_Auth` and `MNSGlobalDev`. The supplied MNS-SQL connections are configured in `FancilPhones/appsettings.json` and are included in the publish output. Both local runs and IIS use these settings. Optional development overrides use .NET user secrets:

```powershell
dotnet user-secrets set "ConnectionStrings:ASP_Auth" "<ASP_AUTH SQL Server connection string>" --project FancilPhones
dotnet user-secrets set "ConnectionStrings:MNSGlobalDev" "<MNSGlobalDev SQL Server connection string>" --project FancilPhones
dotnet run --project FancilPhones --launch-profile https
```

For IIS, deploy the complete publish folder, including `appsettings.json` and the generated `web.config`. The app pool reads both configured connections from `appsettings.json`; development user secrets are not required on the server. Restart the app pool after replacing the published files. `appsettings.Production.json` and environment variables `ConnectionStrings__ASP_Auth` / `ConnectionStrings__MNSGlobalDev` take precedence if present, so remove stale empty overrides. The IIS server must be able to reach MNS-SQL. There is no SQLite fallback.

`AppDbContext` owns the phone, contact, activity, and switchboard tables in the `FancilPhones` schema in MNSGlobalDev. Its migration history is `[FancilPhones].[__EFMigrationsHistory]`, separate from other applications in that database. UTC timestamps retain their UTC kind when read from SQL Server.

`AuthDbContext` uses the same standard `IdentityUser`, roles, and Identity schema version 3 as BIDevHub. This application never creates or migrates authentication tables, seeds users or roles, or changes the shared account schema. Normal Identity sign-in can update lockout counters, authenticator tokens, and security metadata in ASP_AUTH.

By default the app applies only its phone migrations at startup, matching BIDevHub's migration lifecycle. Deployment environments can set `Database__ApplyMigrationsOnStartup=false` and apply them separately with an account that has schema permissions:

```powershell
dotnet tool restore
dotnet run --project FancilPhones --launch-profile https -- --migrate-database
```

## Authentication and access

Sign in with the same employee ID and password used for BIDevHub. The app uses Identity password verification, shared lockout settings, authenticator/recovery-code verification, antiforgery-protected form posts, authentication rate limiting, local return URLs, and security-stamp revalidation for cookies and connected Blazor circuits.

Sessions use FancilPhones-specific cookies. Sharing the identity database does not enable automatic single sign-on across applications.

| ASP_AUTH role | Access |
| --- | --- |
| `BI-ADMIN` or `IT_ADMIN` | Manage phones and contacts; import, export fleet credentials, discover handsets, push SIP settings, and run syncs |
| Other authenticated accounts | View dashboard, phones, contacts, logs; export the phonebook; probe an existing handset |
| Anonymous | Sign-in and verification pages only; phone data and exports require authentication |

Accounts, password changes/resets, authenticator setup, and role assignments are managed centrally. The old local `admin/admin` account is not provisioned or imported. `/users` and `/account/change-password` explain central account management.

## Import an existing SQLite database

The initial migration for this workspace imported **51 phones, 61 contacts, and 409 activity records** into MNSGlobalDev. Do not rerun the import against that populated store.

For a fresh environment, stop writers to the legacy store and retain a SQLite backup. Run:

```powershell
dotnet run --project FancilPhones --launch-profile https -- --import-sqlite "C:\path\to\fancilphones.db"
```

This explicit operation applies phone migrations, reads the legacy database in read-only mode, and imports only the three phone tables. It preserves IDs, relationships, SIP configuration, handset credentials, and timestamps. The destination must be empty; the import refuses populated tables and uses a transaction so a failed import rolls back all imported rows. It never imports SQLite users, passwords, or roles into ASP_AUTH. Retain the legacy snapshot until verification is complete.

## Async work and cancellation

Data pages and editors inherit `AsyncComponentBase`, which cancels owned work on navigation, disposal, request abort, and application shutdown. Tokens flow through authorization, EF queries and transactions, file reads, CSV/XLSX parsing checkpoints, and handset socket/TLS operations. Expected cancellation does not show an error notification. Dialog confirmations release their pending state when cancelled. Refreshes and mutations cannot overlap, and independent dashboard reads use separate DbContexts concurrently.

Synchronization has a **Stop sync** action. Cancelling stops queued work and interrupts active calls, waits for workers to settle, and records started handsets as `Cancelled` with a finish time. Timeouts are recorded as `TimedOut`. Handset changes already applied cannot be rolled back; review history and verify affected phones before retrying. Logout and final history persistence each receive a separate, bounded cleanup token. Shutdown cancels active sync and discovery operations too.

`PhoneOperations` in `appsettings.json` controls `MaxConcurrentSyncs` (8), `TimeoutSeconds` (60 per handset operation), and `CleanupTimeoutSeconds` (3 per cleanup phase). These settings are validated at startup. A fleet run has at most the configured number of active workers. Discovery limits ranges to 4,096 addresses, bounds both workers and buffered results, cancels producers when enumeration ends early, and throttles UI progress renders to roughly ten per second. Socket responses are bounded to 4 MB; incomplete replies fail, and mutating POSTs are never automatically replayed.

Read-only queries avoid EF tracking. Delivery history filters in SQL before limiting results and projects only handset identity fields, excluding credentials. Bulk replacements use set-based deletes within transactions, so failure or cancellation rolls back deletions and cascaded history. Sync completion updates only delivery metadata, preserving concurrent edits to handset configuration.

CSV parsing and row processing check cancellation cooperatively. ClosedXML workbook loading/saving has synchronous APIs; cancellation is checked before and after those calls and between rows, rather than presenting those calls as cancellable asynchronous I/O.

## Loading and interaction feedback

`AsyncButton`, `LoadingState`, and the circuit-scoped `UiActivity` service provide shared pending states. Page loads stream a status panel and show a thin workspace progress indicator. Refreshes, saves, handset commands, imports, and exports show progress text or a spinner; controls reject duplicate actions while work is pending. Activity leases clear independently on completion, failure, cancellation, or navigation, so overlapping work cannot hide another operation's indicator. Motion follows the operating system's reduced-motion preference.

Fleet synchronization displays actual completed/total and success/failure counts as handset operations finish. The confirmation stage, active delivery, stopping, and completion have separate text. Stop sync remains available during delivery. Cancellation keeps completed results visible and retains handset activity history.

Synchronization also lists each fleet phone's latest phonebook delivery, with search and a **Needs retry** filter. Administrators can **Sync phone** or **Retry sync** on an individual row, retry directly from the latest results, or choose **Retry failed phones**. Bulk retries include enabled phones whose latest phonebook attempt is Failed, Error, TimedOut or Cancelled; they exclude successful, unattempted, running, disabled and removed phones. Eligibility uses the complete persisted phonebook history, including after reload, rather than the latest 50 displayed history rows or SIP/probe results. Each retry sends the current master directory, creates a new audited attempt, and uses the same bounded workers, progress and cancellation handling as fleet synchronization. Individual selections are revalidated before delivery if a phone was removed or disabled.

Exports use a same-origin cancellable request, keep their button pending until the file is ready, and report failures without pretending the download succeeded. Conventional sign-in, verification, and sign-out form submissions show pending text and prevent duplicate submissions; returning through browser history restores the original controls. Immediate local filters and dialog-opening actions do not introduce artificial delays.

## Operational logging

Application components and infrastructure services use the built-in `ILogger<T>` pipeline. Authentication outcomes, authorization denials, completed data changes, imports, exports, migrations, discovery, probes, SIP pushes, and phonebook deliveries emit structured events. UI handlers log caught exceptions before showing a friendly error; unhandled request and circuit exceptions retain the ASP.NET Core diagnostics.

Console output uses UTC timestamps and scopes. Each handset operation includes `PhoneId`, `SyncRunId`, and `Operation`; fleet deliveries and discovery runs include a shared correlation ID. Information records operation outcomes, Warning records expected rejection/device failures, Error records unexpected handled failures, and Critical records startup database initialization failure. Detailed per-probe/protocol metadata is Debug. Ordinary EF SQL command output is filtered to Warning.

Logs include IDs, counts, statuses, and diagnostic exceptions. They do not intentionally include passwords, connection strings, authentication codes, cookie values, contact details, or raw handset request/response bodies. Device nonce failures also omit raw response headers. Change category levels through `Logging:LogLevel` in appsettings or host configuration; application categories start with `FancilPhones`.

IIS receives the built-in Windows Event Log provider for Warning and higher by default (Application log). Console output is visible when running the executable directly or under a host that collects stdout. For temporary IIS startup diagnostics, enable `stdoutLogEnabled` on the generated `aspNetCore` element in the deployed `web.config`, create its log directory, and grant the app pool write permission there. Disable stdout capture after troubleshooting; IIS stdout files do not rotate automatically. No separate file logging provider is installed.

## Switchboard function keys

Administrators open **Switchboard keys** (`/switchboards`), choose an existing enabled handset from **Phone fleet**, and click **Add phone**. Its saved fleet credentials are used to read and verify all five pages of twelve keys before a profile is created. Compatibility is checked against the handset response rather than an IP allowlist; standard two-key phones such as `.136` fail enrollment without any key writes. Existing captured profiles remain available automatically. This workflow is independent of phonebook and SIP synchronization.

1. Click a phone's name to open its layout. Checkboxes select sync targets independently of the phone being edited. Disabled fleet phones cannot be edited or selected for sync.
2. Edit Name, Value and Value2 directly on Pages 1–5. **Add key** uses the first empty editable position. All 60 positions, including Page 1 F1 and F2, are editable. The 12 verified types are None, Memory Key, Line, Key Event, DTMF, URL, BLF List Key, MCAST Paging, Action URL, XML Browser, MCAST Listening and PTT. **All key settings** groups Type/Name, the key action, and calling/appearance controls in one dialog. Value, Value2, Subtype, SIP line, Media, Pickup number and Icon color remain visible, with unavailable controls disabled for the selected type. Media offers Default/Audio/Video; icon colors include Green, Blue, Yellow, Red, Purple and Custom (using the icon already configured on the phone). Type-specific controls include multicast codec, PTT mode, HTTP method and nested contact groups. **Reset changes** returns to the key as it was when opened; **Cancel** discards dialog edits. **Use key** validates the fields and updates the local layout, with no handset write. Dialog and saved-layout validation share the same field limits and option checks. URL types allow 127-byte values instead of the normal number limit. Unknown captured types can be retained unchanged.
3. **Save layout** stores a draft in the application without writing to the phone. Alphabetical sorting is enabled by default; it moves whole Memory Key rows across all five pages, keeping Value2 and other settings together and keeping F1/F2 in their current positions during sorting, along with Line and special keys; their fields remain editable. Turn it off for manual placement. **Sort A–Z** is also available explicitly. Clear a row to remove it. Drafts are separate per phone and survive reloads. Concurrent edits are rejected rather than silently overwritten; unsaved edits are protected on navigation. **Discard edits** restores the last saved draft. **Read from phone** explicitly replaces the saved layout with a fresh capture.
4. **Preview this phone** saves pending edits and prepares a preview for that phone alone. Or select target phones and choose **Review selected phones**. All 60 source positions are considered, including empty positions and Page 5. F1 and F2 are copied like every other key. Primary-line assignments adapt to the target phone's primary line; explicit SIP line/Auto/Mobile selections are retained. Global handset settings remain unchanged. **Keep target phone numbers**, enabled by default, retains existing target Value/Value2 pairs matched by normalized contact name. New contacts use the source numbers; switch it off to copy the source numbers too. Names, positions, type and other editable settings follow the source layout. Previewing does not apply keys. If any target cannot be previewed, batch sync remains disabled until it is resolved or deselected and reviewed again.
5. Inspect before/after keys on all five pages, then **Sync reviewed phones**. Source-draft changes, target-profile changes, expiry after 30 minutes, and physical handset drift invalidate previews. A database lease prevents overlapping operations. Pages are applied sequentially; each changed page and the final five-page layout must read back correctly before success. The batch stops on the first failure, cancellation or timeout. Deploying to the source first does not invalidate the remaining reviewed targets.

F1 no longer has to remain Type=Line, and F2 no longer has to match a fixed Reception or Day/Night template. Saves, previews, delivery and restoration use this editable-key policy, including existing profiles and backups. No phone IP address imposes a key template or correction warning. Addresses and credentials come from Phone fleet; key names, numbers and subtypes come from each captured phone and its saved edits. Network discovery starts with an empty range and scans only the range entered by the operator. The legacy shared-directory generator preserves the two existing header keys without enforcing their contents; it is not used by the current editor. Old automatic F2 correction previews must be replaced with a new layout preview; their historical restoration backups remain available. Incomplete captured layouts and actual delivery problems still report actionable errors.

**Delivery history and backups** retains complete immutable before/after snapshots. **Preview restoration** reads the phone afresh and prepares a reviewed restoration, including changes to F1/F2. A stopped sync may have updated earlier pages; cancellation is not rollback. **Remove** unregisters a switchboard and its saved draft, retaining the phone in Phone fleet, its physical keys and audited history. Existing shared-directory membership/override records remain intact for compatibility; the new editor uses per-phone layouts directly and does not modify shared Phonebook contacts.

The `SwitchboardDraftEditor` migration adds saved drafts and source revision metadata in the application-owned schema. It is applied by the normal startup/migration workflow. No migration or phone-key write is needed to render an isolated preview with synthetic devices. Deployment and restoration remain explicit operator actions.

## Verification

```powershell
dotnet build FancilPhones.sln --configuration Release
dotnet test FancilPhones.sln
node --test tests/loading-feedback.test.mjs
```

Normal tests use isolated SQLite databases and synthetic employee accounts, so they do not contact shared SQL Server databases or real handsets. They cover role enforcement, transaction rollback, UTC handling, authentication/lockout, two-factor recovery, safe redirects, antiforgery protection, export access, cancellation/timeout history, bounded concurrency, worker disposal, socket/TLS cancellation, transaction rollback on cancellation, and preserving concurrent phone edits.

To opt into read-only verification of the configured live databases and compare all imported fields with the original snapshot:

```powershell
$env:FANCILPHONES_TEST_LIVE_DATABASES = "1"
$env:FANCILPHONES_LEGACY_DATABASE = "C:\path\to\fancilphones.db"
dotnet test tests/FancilPhones.Tests --filter FullyQualifiedName~LiveDatabaseTests
Remove-Item Env:FANCILPHONES_TEST_LIVE_DATABASES
Remove-Item Env:FANCILPHONES_LEGACY_DATABASE
```

The comparison reports digests rather than record contents so handset credentials do not appear in test diagnostics.

## Git

Build output, IDE state, user settings, local database files, and local secret configuration are ignored. Previously tracked copies of those files have been removed from the index; their working copies are preserved. Source, migrations, tests, documentation, and the local EF tool manifest belong in Git. Ignore rules do not remove files from earlier commits.
