#!/usr/bin/env bash
# WakeMesh local verify helper (repo-relative SDK / optional Android tools only).
# Usage (from repo root or any cwd):
#   ./scripts/verify-local.sh list
#   ./scripts/verify-local.sh test
#   ./scripts/verify-local.sh android-build
#   ./scripts/verify-local.sh checkignore
#
# Portable rules:
# - Never hard-code a user home directory or machine-specific JDK path.
# - Prefer repo-relative .tools/* when present.
# - Host JAVA_HOME / ANDROID_* are used only if already exported by the caller.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

DOTNET_ROOT_REL=".tools/dotnet"
DOTNET_BIN="${ROOT}/${DOTNET_ROOT_REL}/dotnet"
MOBILE_CSPROJ="src/WakeMesh.Mobile/WakeMesh.Mobile.csproj"
MANAGED_SLN="WakeMesh.slnx"
ANDROID_SLN="WakeMesh.Android.slnx"

die() { echo "error: $*" >&2; exit 1; }

require_dotnet() {
  if [[ ! -x "$DOTNET_BIN" ]]; then
    die "local SDK not found at ${DOTNET_ROOT_REL}/dotnet — install .NET 11.0.100 into .tools/dotnet (see README.md)."
  fi
  export DOTNET_ROOT="${ROOT}/${DOTNET_ROOT_REL}"
  export PATH="${DOTNET_ROOT}:${PATH}"
  export DOTNET_CLI_HOME="${ROOT}/.tools/cli-home"
  export DOTNET_CLI_TELEMETRY_OPTOUT=1
  export DOTNET_NOLOGO=1
  # Do not leak host-wide SDK preference; pin via global.json + this binary.
  unset MSBuildSDKsPath || true
}

# Optional Android/JDK: repo-relative first; never embed user-specific absolute defaults.
apply_android_tool_env() {
  local sdk_rel jdk_rel
  sdk_rel="${ROOT}/.tools/android-sdk"
  jdk_rel="${ROOT}/.tools/jdk"

  if [[ -d "$sdk_rel" ]]; then
    export ANDROID_HOME="$sdk_rel"
    export ANDROID_SDK_ROOT="$sdk_rel"
  fi
  # Only set JAVA_HOME from repo tree when the caller has not already set one.
  if [[ -z "${JAVA_HOME:-}" && -d "$jdk_rel" ]]; then
    export JAVA_HOME="$jdk_rel"
    export PATH="${JAVA_HOME}/bin:${PATH}"
  fi
  # If ANDROID_HOME / JAVA_HOME remain unset, leave them unset (dotnet/android will report).
}

CMD="${1:-list}"

case "$CMD" in
  list)
    require_dotnet
    echo "== SDK =="
    "$DOTNET_BIN" --version
    echo
    echo "== ${MANAGED_SLN} (managed / server tests; no Android app) =="
    "$DOTNET_BIN" sln "$MANAGED_SLN" list
    echo
    echo "== ${ANDROID_SLN} (Android build path listing) =="
    "$DOTNET_BIN" sln "$ANDROID_SLN" list
    echo
    echo "== on-disk csproj counts (find; not guessed) =="
    src_n="$(find src -name '*.csproj' | wc -l | tr -d ' ')"
    test_n="$(find tests -name '*.csproj' | wc -l | tr -d ' ')"
    echo "src/: ${src_n}"
    echo "tests/: ${test_n}"
    echo "total: $((src_n + test_n))"
    ;;
  test)
    require_dotnet
    echo "== restore + test ${MANAGED_SLN} (Android workload not required) =="
    "$DOTNET_BIN" restore "$MANAGED_SLN"
    "$DOTNET_BIN" test "$MANAGED_SLN" --no-restore
    ;;
  android-build)
    require_dotnet
    apply_android_tool_env
    # Target Mobile.csproj only. Do not pass -f net11.0-android to a whole solution
    # (that would force ProjectReference Core onto android TFM).
    echo "== restore + build ${MOBILE_CSPROJ} (-f net11.0-android; Core stays net11.0) =="
    echo "ANDROID_HOME=${ANDROID_HOME:-<unset>}"
    echo "JAVA_HOME=${JAVA_HOME:-<unset>}"
    "$DOTNET_BIN" restore "$MOBILE_CSPROJ"
    "$DOTNET_BIN" build "$MOBILE_CSPROJ" -f net11.0-android --no-restore
    ;;
  checkignore)
    # Static policy check: sensitive path globs are listed; test sources stay trackable.
    # Uses git check-ignore when .git exists; otherwise greps .gitignore only.
    fail=0
    must_ignore_samples=(
      ".tools/dotnet/dotnet"
      ".evidence/sample.log"
      "artifacts/app.apk"
      "release.keystore"
      "signing.jks"
      "google-services.json"
      "src/WakeMesh.Mobile/Platforms/Android/google-services.json"
      "service-account-fcm.json"
      "credentials.json"
      "appsettings.Production.local.json"
      "secret.pem"
      "upload.pfx"
    )
    must_not_ignore_samples=(
      "tests/WakeMesh.Mobile.Push.Tests/NoSecretLoggingTests.cs"
      "tests/WakeMesh.Api.Tests/WakeMesh.Api.Tests.csproj"
      "tests/WakeMesh.Mobile.Tests/WakeMesh.Mobile.Tests.csproj"
      "src/WakeMesh.Mobile/Services/SecureDeviceCredentialStore.cs"
      "src/WakeMesh.Api/Services/NotificationStatusService.cs"
      "README.md"
      "WakeMesh.slnx"
      "scripts/verify-local.sh"
    )

    echo "== .gitignore presence =="
    [[ -f .gitignore ]] || die ".gitignore missing"

    if command -v git >/dev/null 2>&1 && [[ -d .git ]]; then
      echo "== git check-ignore (must IGNORE sensitive samples) =="
      for p in "${must_ignore_samples[@]}"; do
        if git check-ignore -q "$p"; then
          echo "OK ignore: $p"
        else
          echo "FAIL expected ignore: $p" >&2
          fail=1
        fi
      done
      echo "== git check-ignore (must NOT ignore test/source samples) =="
      for p in "${must_not_ignore_samples[@]}"; do
        if git check-ignore -q "$p"; then
          echo "FAIL unexpectedly ignored: $p" >&2
          fail=1
        else
          echo "OK trackable: $p"
        fi
      done
    else
      echo "== git unavailable; structural .gitignore rules only =="
      # Ensure we did not introduce a blanket *secret* / *credential* source hide.
      if grep -E '^[^#]*\*(secret|credential)\*' .gitignore >/dev/null 2>&1; then
        echo "FAIL: blanket *secret*/*credential* glob would hide test sources" >&2
        fail=1
      else
        echo "OK: no blanket *secret*/*credential* source glob"
      fi
      for token in '.tools/' '.evidence/' '*.apk' '*.keystore' 'google-services.json' 'service-account'; do
        if grep -F "$token" .gitignore >/dev/null 2>&1; then
          echo "OK rule mentions: $token"
        else
          echo "FAIL missing rule token: $token" >&2
          fail=1
        fi
      done
    fi

    if [[ "$fail" -ne 0 ]]; then
      die "checkignore failed"
    fi
    echo "checkignore: OK"
    ;;
  *)
    echo "usage: $0 {list|test|android-build|checkignore}" >&2
    exit 2
    ;;
esac
