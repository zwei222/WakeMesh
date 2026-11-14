# WakeMesh.Client

Pure .NET 11 shared HTTP client for the WakeMesh owner/device API. Intended for MAUI and other managed callers. **Does not** depend on ASP.NET Core hosting, MAUI, or Android.

## Status / boundaries

| Item | Status |
|------|--------|
| Wire contracts (`/api/v1/devices`, `/api/v1/alarm-groups`, desired/ACK) | Implemented against live API DTOs |
| `IAccessTokenProvider` (MSAL / owner access token) | Interface only — **Mobile package next** |
| `IDeviceCredentialStore` (secure storage) | Interface only — **MAUI secure storage next** |
| `IAppliedRevisionStore` (durable applied high-water) | **Required** inject on orchestrator — **MAUI durable impl next** (no implicit memory fallback) |
| Real Android desired applier (AlarmManager) | **Not implemented** — inject `IDesiredApplier` later |
| Production Microsoft login / Azure host | **Not connected** |
| FCM push | Not part of this client |

Server **save** success is not the same as **target device applied**. Group create/replace return `GroupSaveOutcome` (group + notification status). Device apply is confirmed only after desired fetch → local apply → **commit applied revision** → ACK.

## Install / reference

```xml
<ProjectReference Include="..\WakeMesh.Client\WakeMesh.Client.csproj" />
```

No NuGet package yet. Target `net11.0`.

## Production client creation (HTTPS only — no raw HttpClient)

**Do not** construct `WakeMeshApiClient` with an arbitrary `HttpClient` or custom handler. That surface is internal (tests only) because a caller-supplied client can enable auto-redirect and leak `Authorization` / `X-Device-Credential`.

```csharp
using WakeMesh.Client;
using WakeMesh.Client.Http;

// Preferred: URL + token provider + credential store only.
using var api = WakeMeshApiClient.Create(
    "https://api.example.invalid/",
    tokens,   // IAccessTokenProvider (MSAL — Mobile)
    creds);   // IDeviceCredentialStore (secure storage — Mobile)

// Equivalent:
using var api2 = new WakeMeshApiClient(new Uri("https://api.example.invalid/"), tokens, creds);

// Low-level HTTPS HttpClient if you must own the instance (still no custom handler):
using var http = WakeMeshHttpClientFactory.Create("https://api.example.invalid/");
// http is already no-redirect + host-pinned; pair only via internal test hooks, not public API.
```

### Safety defaults

- Base URL must be **HTTPS** (production factory / public ctors).
- Innermost handler is always created with **`AllowAutoRedirect = false`** (`SocketsHttpHandler`).
- **No automatic redirects** (`NoRedirectHandler`). 3xx → `ApiErrorKind.RedirectRefused`.
- **Host pin**: `Authorization` and `X-Device-Credential` are stripped if the request host is not the configured API host.
- Access tokens and device credentials are **not** written into `ApiResult` messages or exception text from this library.
- Token provider / credential store exceptions map to fixed kinds (`TokenProviderFailed`, `CredentialStoreFailed`, `CredentialPersistFailed`) — **never** by parsing exception message host strings.
- 2xx malformed / null / truncated JSON → `ApiErrorKind.InvalidResponse` (not generic `Server`).
- Desired snapshot schema (revision ≥ 0, non-empty receiptId, deviceId match, non-null items/tombstones and elements) → `InvalidResponse`; no local apply/commit/ACK.

In-process tests may inject `WebApplicationFactory.CreateClient()` via **internal** `WakeMeshApiClient.CreateForTests` (`InternalsVisibleTo` the test assembly only).

## Owner flows

```csharp
// Register this device (credential returned once — store immediately under bounded independent CT
// after server headers are accepted, so caller cancel cannot drop the one-time secret post-success).
var reg = await api.RegisterDeviceAsync(displayName: "Pixel");
if (!reg.IsSuccess)
{
    // CredentialPersistFailed + RecoveryNeeded + ServerDeviceId → local save failed after server create.
    // Value may still carry the one-time credential for a retry of SaveCredentialAsync only.
    // UnknownOutcome + RecoveryNeeded + ServerDeviceId=null → cancel in-flight before headers;
    // server may or may not have created the device — do NOT treat as "no recovery needed".
    // Message/ErrorCode never embed the credential.
    if (reg.RecoveryNeeded) { /* recover; list devices / revoke orphans when sid unknown */ }
}
// reg.Value.Credential is available once; also saved via IDeviceCredentialStore when provided.

var devices = await api.ListDevicesAsync();
var detail = await api.GetDeviceAsync(reg.Value!.Id);

// Alarm group create = server save only (notification may be notConfigured).
var save = await api.CreateAlarmGroupAsync(new AlarmGroupWriteRequest(
    Id: "weekday",
    Label: "Weekday",
    Enabled: true,
    Repeat: "weekly",
    Days: new[] { 1, 2, 3, 4, 5 },
    Date: null,
    TimeZone: "Asia/Tokyo",
    ExcludeHolidays: true,
    ExcludedDates: Array.Empty<string>(),
    Targets: new[]
    {
        new AlarmTargetDto(null, reg.Value.Id, "06:30", "default", 80, vibrate: true, enabled: true),
    }));
// save.Value.Group.Version — use for If-Match on later replace/delete
// save.Value.Notification — FCM status; not device apply

var replaced = await api.ReplaceAlarmGroupAsync("weekday", writeRequest, ifMatchVersion: save.Value!.Group.Version);
// 412 → ApiErrorKind.PreconditionFailed (reload + retry)
// 428 → ApiErrorKind.PreconditionRequired

await api.DeleteAlarmGroupAsync("weekday", ifMatchVersion: version);

await api.RetireDeviceAsync(deviceId);  // graceful empty desired + cancel ACK path
await api.RevokeDeviceAsync(deviceId);  // immediate; local cancel still unconfirmed in UI terms
```

## Device desired / ACK

Requires owner access token **and** stored device credential for the path device id.

```csharp
var desired = await api.GetDesiredAsync(deviceId);
// desired.Kind == Unauthorized → wrong/missing credential or token (re-login / re-register)
// desired.Kind == Gone → device revoked or receipt expired
// desired.Kind == InvalidResponse → malformed body / schema (revision&lt;0, empty receipt, null items, …)

var ack = await api.PostAckAsync(deviceId, new AckRequest(
    desired.Value!.ReceiptId,
    desired.Value.Revision,
    Result: "success", // or "failure"
    Failures: Array.Empty<AckFailureDto>(),
    ClientReportedAt: null));
```

## UI error mapping (`ApiErrorKind`)

| Kind | Typical HTTP | UI hint |
|------|--------------|---------|
| `Unauthorized` | 401 | Re-login (owner) or fix device credential |
| `Forbidden` | 403 | Not permitted |
| `PreconditionFailed` | 412 | Version conflict — reload group and retry |
| `PreconditionRequired` | 428 | Missing If-Match |
| `Gone` | 410 | Device/receipt no longer valid |
| `Conflict` | 409 | Duplicate / receipt mismatch |
| `Canceled` / `Timeout` | — | Caller cancel or timeout |
| `UnknownOutcome` | — | Ambiguous server side-effect (e.g. register cancel in-flight) — `RecoveryNeeded` |
| `RedirectRefused` | 3xx | Misconfigured endpoint; secrets not followed |
| `InvalidResponse` | 2xx bad JSON/schema | Treat as protocol fault; do not assume apply |
| `TokenProviderFailed` | — | MSAL/token path failed (fixed message) |
| `CredentialPersistFailed` | — | Server register ok; local durable save failed — `RecoveryNeeded` |
| `CredentialStoreFailed` | — | Store read/commit failed (fixed message) |

## Read-only desired sync orchestrator

```csharp
using WakeMesh.Client.Sync;

// TODO(Mobile): real Android applier — persist schedules + AlarmManager register/cancel.
IDesiredApplier applier = new MyAndroidApplier(); // not shipped

// REQUIRED: durable IAppliedRevisionStore (secure preferences / DB). Must survive process restart.
// No implicit in-memory fallback — constructor throws if null.
IAppliedRevisionStore applied = new MyDurableAppliedRevisionStore();

var sync = new DesiredSyncOrchestrator(api, applier, applied);
var outcome = await sync.SyncDeviceAsync(deviceId, ct);

// Per-device process-wide async gate: multiple orchestrator instances serialize on the same deviceId.
// Order: GET desired → schema + DeviceId bind check →
//   revision < durable applied → StaleRevisionSkipped (no OS apply, no ACK)
//   revision == durable applied → success ACK with NEW receipt only (no re-apply, no re-commit)
//   revision > durable / never committed → ApplyAsync → CommitAppliedRevision → success ACK
// Revision 0 is valid (new device empty/cancel snapshot). GetApplied returns null until first commit
// so never-applied is distinct from applied-0.
// Succeeded=true with non-empty Failures is treated as failure (no applied commit; failure ACK).
// Applier exceptions → ApplyIncompleteNoAck / Unexpected / applierFailed (no raw message leak).
// Incomplete or cancel → no ACK (ApplyIncompleteNoAck / CanceledNoAck).
```

### Applied-revision durability contract

1. OS / local apply completes for revision R (snapshot `DeviceId` must equal sync device id; schema valid; R ≥ 0).
2. `IAppliedRevisionStore.CommitAppliedRevisionAsync(deviceId, R)` persists R (monotonic; no rollback on process recreate once returned).
3. Only then may the client send a **success** ACK for R.
4. If success ACK is lost (e.g. HTTP 503) after commit, a later sync of the **same** revision must **not** re-apply; it must success-ACK using the **new** receipt from GET desired so retire/cancel can complete.
5. Only **strictly lower** revisions are stale. Equal is recovery; higher is normal apply.

A crash after apply but before commit may re-apply the same or newer revision. A crash after commit but before ACK may re-ACK without lowering the store. Lower revisions must never be applied or success-ACKed after a higher commit.

### Future Mobile durable key (binding)

When Mobile implements `IAppliedRevisionStore` / credential rows, key material must include:

- **normalized API base URL**
- **account binding** (owner identity)
- **serverDeviceId**
- **registration generation**

If a DB restore (or env switch) surfaces a **lower** server revision/generation under the same device id, **do not silently reset** the local high-water or auto-ACK under the old generation — surface recovery / re-bind instead. MSAL and native secure storage remain separate Mobile work.

## Migration call examples (T13)

```csharp
// BEFORE (T11): optional store with process-local fallback — DO NOT use
// var sync = new DesiredSyncOrchestrator(api, applier);

// AFTER (T13): store is required
IAppliedRevisionStore store = new MyDurableAppliedRevisionStore();
var sync = new DesiredSyncOrchestrator(api, applier, store);

// GetAppliedRevisionAsync is now long? (null = never committed)
long? applied = await store.GetAppliedRevisionAsync(deviceId);
if (applied is null) { /* first apply, including revision 0 */ }

// Register in-flight cancel
if (reg.Kind == ApiErrorKind.UnknownOutcome && reg.RecoveryNeeded)
{
    // ServerDeviceId is null — list/reconcile devices; do not assume no server row.
}
```

## Testing notes

- Client unit/integration tests live in `tests/WakeMesh.Client.Tests` and use a **client-owned** `WebApplicationFactory` + SQLite + RSA test JWT (not production auth bypass inside the client).
- Run: `dotnet test tests/WakeMesh.Client.Tests/WakeMesh.Client.Tests.csproj`
- Do not put real tokens, credentials, or personal ids in logs, fixtures, or this README.

## Mobile handoff (public API delta)

| Change | Impact on Mobile |
|--------|------------------|
| `WakeMeshApiClient.Create(httpsUrl, IAccessTokenProvider, IDeviceCredentialStore?)` | Preferred factory — use this when wiring MSAL |
| `new WakeMeshApiClient(Uri https, tokens, creds)` | Unchanged public production ctor |
| `new WakeMeshApiClient(HttpClient, ...)` | **Not public** (internal + tests). Mobile must not pass raw HttpClient |
| `WakeMeshHttpClientFactory.Create(uri, HttpMessageHandler)` | **Not public** |
| `IAccessTokenProvider` / `IDeviceCredentialStore` | **Unchanged** signatures |
| `IAppliedRevisionStore` | **Required** on `DesiredSyncOrchestrator` (was optional with memory fallback) |
| `IAppliedRevisionStore.GetAppliedRevisionAsync` | Returns **`long?`** (`null` = never committed; `0` is a real applied empty snapshot) |
| `ApiResult&lt;T&gt;.ServerDeviceId` / `RecoveryNeeded` | Register recovery / unknown outcome |
| New `ApiErrorKind` | InvalidResponse, TokenProviderFailed, CredentialPersistFailed, CredentialStoreFailed, **UnknownOutcome** |
| `DesiredSyncStatus.StaleRevisionSkipped` | Strictly lower revision only |
| Equal revision after commit | Re-ACK with new receipt (no re-apply) |

Mobile currently registers token/credential interfaces only and does not construct `WakeMeshApiClient` yet — **no Mobile source edits required** for this slice; compile impact **none** until Mobile constructs the client/orchestrator (must pass an explicit durable store). Real MSAL / native secure storage / AlarmManager remain unverified.
