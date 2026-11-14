# Security & secrets policy (WakeMesh)

This document is operational guidance only. It contains **no** real account IDs, tokens, connection strings, keystore passwords, or service-account material.

## What stays outside Git

Place personal and production values **outside** the repository (user secret store, CI secret variables, or a path under your home directory that is never copied into the tree).

| Kind | Examples (names only) | Typical placement |
|------|------------------------|-------------------|
| Local SDK / Android SDK / JDK | `.tools/dotnet`, `.tools/android-sdk`, `.tools/jdk` | Repo-relative `.tools/` (gitignored) |
| Evidence / logs / APK | `.evidence/`, `*.apk`, `*.aab` | gitignored paths |
| Firebase client config | `google-services.json` | External path via `-p:GoogleServicesJsonPath=...` |
| FCM server credentials | service-account JSON | `WakeMesh:Fcm:CredentialsPath` absolute path or ADC env — never inline JSON in appsettings |
| Signing material | `*.keystore`, `*.jks`, `*.pfx`, `*.pem` | External path; pass MSBuild signing properties locally |
| Auth / DB overlays | `appsettings.*.local.json`, `.env` | gitignored local overlays |

## `.gitignore` design rule

Ignore **artifact and filename patterns** (APK, keystore, `google-services.json`, `service-account*.json`, `credentials.json`, `.tools/`, `.evidence/`).

Do **not** use broad globs such as repository-wide `*secret*` or `*credential*` that would hide legitimate **test sources** (for example `NoSecretLoggingTests.cs`, `SecureDeviceCredentialStore.cs`).

## Runtime auth / push honesty

- **Zero GUID** (`00000000-0000-0000-0000-000000000000`) for `WakeMeshAuthClientId` / client IDs means **unconfigured**. Login and production API calls must not proceed on that placeholder.
- **FCM Accepted** means the push provider accepted the message for delivery attempt. It is **not** device apply confirmation.
- **Device apply** is confirmed only via desired snapshot apply + **ACK** (`POST /api/v1/devices/{id}/acks`) advancing applied revision.
- Production signing keys for API JWT validation come from **HTTPS OIDC MetadataAddress** only (no static PEM config key in production).

## Local build properties (no values committed)

```bash
# Nonzero lowercase GUID required for real MSAL redirect wiring on device builds.
dotnet build src/WakeMesh.Mobile/WakeMesh.Mobile.csproj \
  -f net11.0-android \
  -p:WakeMeshAuthClientId=<your-public-client-guid-lowercase> \
  -p:GoogleServicesJsonPath=/absolute/path/outside/repo/google-services.json
```

Release signing properties (names only; supply values from a private store):

- `AndroidKeyStore`, `AndroidSigningKeyStore`, `AndroidSigningStorePass`, `AndroidSigningKeyAlias`, `AndroidSigningKeyPass`

Never commit those values, keystore files, or a filled `Directory.Build.local.props`.

## Reporting

This is a personal project tree. There is no public vulnerability bounty endpoint documented here.
