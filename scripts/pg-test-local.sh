#!/usr/bin/env bash
# Disposable local PostgreSQL 17 for WakeMesh opt-in database tests.
#
#   WAKEMESH_PG_RUN_ROOT=/abs/dedicated/dir ./scripts/pg-test-local.sh start
#   eval "$(./scripts/pg-test-local.sh env)"     # exports the connection string + run id
#   dotnet test tests/WakeMesh.Api.Tests/WakeMesh.Api.Tests.csproj
#   ./scripts/pg-test-local.sh stop
#
#   ./scripts/pg-test-local.sh run [test-project] [dotnet test args...]
#       Creates its own private mktemp root, runs the tests, always stops and removes it.
#
# Safety model:
# - No implicit shared root. start/stop/env/status require an explicit WAKEMESH_PG_RUN_ROOT
#   that is an absolute, dedicated directory the caller owns.
# - Socket-only server (listen_addresses = ''), trust auth, fixture user, no TCP listener,
#   private 0700 root/socket/data dirs, no password anywhere.
# - Ownership is proven from a state file that is parsed (never sourced) and validated against
#   the live process: format, run id, owner uid, recorded pid, postmaster.pid, and the
#   postmaster argv datadir must all agree. Anything ambiguous fails closed and changes nothing.
# - `start` never reuses an existing instance without an explicit run-id confirmation; a
#   non-empty root without proven ownership is left untouched.
# - `stop` only stops/removes an instance whose ownership is proven; otherwise nothing (state
#   included) is deleted.
# - `run` only cleans up the fresh mktemp root it created itself.
set -euo pipefail
umask 077

PGBIN="${WAKEMESH_PG_BIN:-/usr/lib/postgresql/17/bin}"
PGUSER_FIXTURE="wakemesh"
STATE_FORMAT=1
STATE_FILE_NAME="state.env"
LOG_FILE_NAME="postgres.log"

RUN_ROOT="${WAKEMESH_PG_RUN_ROOT:-}"
ROOT_REAL=""
DATADIR=""
SOCKDIR=""
STATE_FILE=""
LOGFILE=""
LOCK_DIR=""
LOCK_HELD=0
LOCK_OWNER=""
LOAD_REASON=""
SR_REASON=""
RUN_ROOT_WAS_TEMP=0
CREATED_DATADIR=""
CREATED_SOCKDIR=""
RUN_EXIT=0

die() { echo "error: $*" >&2; exit 1; }
log() { echo "[pg-test-local] $*" >&2; }
usage() {
  cat >&2 <<'EOF'
usage: pg-test-local.sh <command>

  start    initdb + start a disposable socket-only instance in $WAKEMESH_PG_RUN_ROOT
  env      print export lines (connection string, run id) for the owned instance
  status   report whether the owned instance is running
  stop     stop and remove only the instance whose ownership is proven
  run [test-project] [dotnet test args...]
           create a private temp root, run `dotnet test`, always clean up

start/stop/env/status require an explicit WAKEMESH_PG_RUN_ROOT (absolute, dedicated,
writable directory you own). There is no shared default root.
EOF
}

require_binaries() {
  [[ -d "$PGBIN" ]] || die "PostgreSQL bin dir not found: $PGBIN (set WAKEMESH_PG_BIN)"
  local b
  for b in initdb pg_ctl psql; do
    [[ -x "$PGBIN/$b" ]] || die "missing $PGBIN/$b"
  done
}

require_run_root() {
  [[ -n "$RUN_ROOT" ]] || die "WAKEMESH_PG_RUN_ROOT must be set to a dedicated directory you own; start/stop/env/status have no shared default."
  [[ "$RUN_ROOT" == /* ]] || die "WAKEMESH_PG_RUN_ROOT must be an absolute path (got '$RUN_ROOT')"
  [[ "$RUN_ROOT" != "/" ]] || die "WAKEMESH_PG_RUN_ROOT must not be '/'"
  case "$RUN_ROOT" in
    *"'"*|*$'\n'*|*" "*|*$'\t'*) die "WAKEMESH_PG_RUN_ROOT must not contain spaces, tabs, quotes, or newlines" ;;
  esac
  local parent
  parent="$(dirname -- "$RUN_ROOT")"
  [[ -d "$parent" ]] || die "parent directory of WAKEMESH_PG_RUN_ROOT does not exist: $parent"
  ROOT_REAL="$(realpath -m -- "$RUN_ROOT")"
  DATADIR="$ROOT_REAL/data"
  SOCKDIR="$ROOT_REAL/sock"
  STATE_FILE="$ROOT_REAL/$STATE_FILE_NAME"
  LOGFILE="$ROOT_REAL/$LOG_FILE_NAME"
}

prepare_root() {
  [[ -L "$RUN_ROOT" ]] && die "refusing to use symlinked RUN_ROOT: $RUN_ROOT"
  if [[ -e "$RUN_ROOT" && ! -d "$RUN_ROOT" ]]; then
    die "RUN_ROOT exists but is not a directory: $RUN_ROOT"
  fi
  [[ -d "$RUN_ROOT" ]] || mkdir -m 700 -- "$RUN_ROOT" || die "cannot create RUN_ROOT: $RUN_ROOT"
  local mode
  mode="$(stat -c %a -- "$ROOT_REAL")"
  (( (8#$mode & 077) == 0 )) || die "RUN_ROOT must not be group/other accessible (mode $mode)"
}

# --- state file: parsed, never sourced -------------------------------------
S_FORMAT=""
S_RUN_ID=""
S_OWNER_UID=""
S_PG_PID=""
S_PG_DATADIR=""
S_PG_SOCKDIR=""
S_PG_PORT=""
S_PG_USER=""
S_PG_BIN=""
S_CREATED_AT=""

reset_state_vars() {
  S_FORMAT=""; S_RUN_ID=""; S_OWNER_UID=""; S_PG_PID=""; S_PG_DATADIR=""
  S_PG_SOCKDIR=""; S_PG_PORT=""; S_PG_USER=""; S_PG_BIN=""; S_CREATED_AT=""
}

load_state() {
  reset_state_vars
  LOAD_REASON=""
  [[ -e "$STATE_FILE" ]] || { LOAD_REASON="no state file"; return 1; }
  [[ -L "$STATE_FILE" ]] && { LOAD_REASON="state file is a symlink"; return 1; }
  [[ -f "$STATE_FILE" ]] || { LOAD_REASON="state file is not a regular file"; return 1; }
  [[ "$(stat -c %u -- "$STATE_FILE")" == "$(id -u)" ]] || { LOAD_REASON="state file is owned by another uid"; return 1; }

  local line key value
  while IFS= read -r line || [[ -n "$line" ]]; do
    [[ -z "$line" ]] && continue
    [[ "$line" =~ ^([A-Z][A-Z0-9_]*)=(.*)$ ]] || { LOAD_REASON="malformed state line"; return 1; }
    key="${BASH_REMATCH[1]}"
    value="${BASH_REMATCH[2]}"
    case "$key" in
      FORMAT) S_FORMAT="$value" ;;
      RUN_ID) S_RUN_ID="$value" ;;
      OWNER_UID) S_OWNER_UID="$value" ;;
      PG_PID) S_PG_PID="$value" ;;
      PG_DATADIR) S_PG_DATADIR="$value" ;;
      PG_SOCKDIR) S_PG_SOCKDIR="$value" ;;
      PG_PORT) S_PG_PORT="$value" ;;
      PG_USER) S_PG_USER="$value" ;;
      PG_BIN) S_PG_BIN="$value" ;;
      CREATED_AT) S_CREATED_AT="$value" ;;
      *) LOAD_REASON="unexpected key in state file: $key"; return 1 ;;
    esac
  done < "$STATE_FILE"

  [[ "$S_FORMAT" == "$STATE_FORMAT" ]] || { LOAD_REASON="unsupported state format '$S_FORMAT'"; return 1; }
  [[ "$S_RUN_ID" =~ ^[0-9a-f]{32}$ ]] || { LOAD_REASON="invalid run id"; return 1; }
  [[ "$S_OWNER_UID" == "$(id -u)" ]] || { LOAD_REASON="state recorded a different owner uid"; return 1; }
  [[ "$S_PG_PID" =~ ^[0-9]+$ ]] || { LOAD_REASON="invalid pg pid"; return 1; }
  [[ "$S_PG_PORT" =~ ^[0-9]+$ ]] && (( S_PG_PORT >= 1 && S_PG_PORT <= 65535 )) || { LOAD_REASON="invalid port"; return 1; }
  [[ "$S_PG_DATADIR" == "$DATADIR" ]] || { LOAD_REASON="recorded datadir '$S_PG_DATADIR' is not this root's datadir"; return 1; }
  [[ "$S_PG_SOCKDIR" == "$SOCKDIR" ]] || { LOAD_REASON="recorded socket dir '$S_PG_SOCKDIR' is not this root's socket dir"; return 1; }
  [[ "$S_PG_USER" == "$PGUSER_FIXTURE" ]] || { LOAD_REASON="unexpected fixture user"; return 1; }
  [[ "$S_PG_BIN" == /* && -d "$S_PG_BIN" ]] || { LOAD_REASON="recorded bin dir is unusable"; return 1; }
  return 0
}

state_running() {
  SR_REASON=""
  [[ -n "$S_PG_PID" ]] || { SR_REASON="no recorded pid"; return 1; }
  [[ -f "$S_PG_DATADIR/postmaster.pid" ]] || { SR_REASON="postmaster.pid missing"; return 1; }
  [[ -L "$S_PG_DATADIR/postmaster.pid" ]] && { SR_REASON="postmaster.pid is a symlink"; return 1; }
  local pid cmdline
  pid="$(head -1 -- "$S_PG_DATADIR/postmaster.pid" 2>/dev/null || true)"
  [[ "$pid" == "$S_PG_PID" ]] || { SR_REASON="postmaster.pid does not match recorded pid"; return 1; }
  kill -0 "$pid" 2>/dev/null || { SR_REASON="recorded pid $pid is not running"; return 1; }
  cmdline="$(tr '\0' ' ' < "/proc/$pid/cmdline" 2>/dev/null || true)"
  [[ "$cmdline" == *postgres* ]] || { SR_REASON="pid $pid is not a postgres process"; return 1; }
  [[ "$cmdline" == *"-D $S_PG_DATADIR"* ]] || { SR_REASON="pid $pid belongs to a different datadir"; return 1; }
  [[ "$(stat -c %u -- "/proc/$pid" 2>/dev/null || echo)" == "$(id -u)" ]] || { SR_REASON="pid $pid is not owned by this uid"; return 1; }
  return 0
}

# --- lock (atomic mkdir, stale-lock aware) ---------------------------------
acquire_lock() {
  LOCK_DIR="${ROOT_REAL}.lock"
  local attempt owner_pid
  for attempt in 1 2 3; do
    if mkdir -m 700 -- "$LOCK_DIR" 2>/dev/null; then
      printf '%s\n' "$$" > "$LOCK_DIR/pid"
      LOCK_HELD=1
      return 0
    fi
    owner_pid=""
    [[ -f "$LOCK_DIR/pid" ]] && owner_pid="$(head -1 -- "$LOCK_DIR/pid" 2>/dev/null || true)"
    if [[ "$owner_pid" =~ ^[0-9]+$ ]] && kill -0 "$owner_pid" 2>/dev/null; then
      LOCK_OWNER="$owner_pid"
      return 1
    fi
    # Stale lock left by a dead process; only remove it when we own the directory entry.
    [[ "$(stat -c %u -- "$LOCK_DIR" 2>/dev/null || echo)" == "$(id -u)" ]] || { LOCK_OWNER="${owner_pid:-unknown}"; return 1; }
    rm -rf -- "$LOCK_DIR" 2>/dev/null || { LOCK_OWNER="${owner_pid:-unknown}"; return 1; }
  done
  LOCK_OWNER="${owner_pid:-unknown}"
  return 1
}

release_lock() {
  if [[ "${LOCK_HELD:-0}" == "1" && -n "${LOCK_DIR:-}" && -d "${LOCK_DIR:-}" ]]; then
    rm -rf -- "$LOCK_DIR" 2>/dev/null || true
  fi
  LOCK_HELD=0
}

# --- owned postmaster shutdown helpers -------------------------------------
stop_datadir_postmaster() { # datadir mode
  local datadir="$1" mode="$2" pid
  [[ -f "$datadir/postmaster.pid" ]] || return 0
  pid="$(head -1 -- "$datadir/postmaster.pid" 2>/dev/null || true)"
  [[ "$pid" =~ ^[0-9]+$ ]] || return 0
  kill -0 "$pid" 2>/dev/null || return 0
  log "stopping postmaster pid $pid (datadir $datadir)"
  if ! "$PGBIN/pg_ctl" -D "$datadir" -m "$mode" -w -t 60 stop >/dev/null 2>&1; then
    log "pg_ctl stop failed for pid $pid; sending TERM"
    kill -TERM "$pid" 2>/dev/null || true
    local i
    for i in $(seq 1 30); do
      kill -0 "$pid" 2>/dev/null || return 0
      sleep 0.2
    done
    kill -KILL "$pid" 2>/dev/null || true
  fi
  return 0
}

remove_created_artifacts() {
  # Only paths created by this invocation in a root that was verified empty.
  local p real
  for p in "$CREATED_DATADIR" "$CREATED_SOCKDIR"; do
    [[ -n "$p" ]] || continue
    real="$(realpath -m -- "$p")"
    case "$real" in
      "$DATADIR"|"$SOCKDIR") rm -rf -- "$real" ;;
      *) die "refusing to remove unexpected path: $p" ;;
    esac
  done
  if [[ -n "$LOGFILE" && -f "$LOGFILE" ]]; then
    rm -f -- "$LOGFILE"
  fi
  rm -f -- "$STATE_FILE.tmp."* 2>/dev/null || true
}

cleanup_partial_start() {
  if [[ -n "$CREATED_DATADIR" ]]; then
    stop_datadir_postmaster "$CREATED_DATADIR" immediate || true
  fi
  remove_created_artifacts || true
  CREATED_DATADIR=""
  CREATED_SOCKDIR=""
}

# --- commands ---------------------------------------------------------------
cmd_start() {
  require_binaries
  require_run_root
  prepare_root

  if ! acquire_lock; then
    die "lock busy: another pg-test-local invocation (pid ${LOCK_OWNER:-unknown}) is operating on $ROOT_REAL; refusing to race it"
  fi

  if load_state; then
    if state_running; then
      if [[ "${WAKEMESH_PG_RUN_ID:-}" == "$S_RUN_ID" ]]; then
        log "already running (run ${S_RUN_ID}, pid ${S_PG_PID}); reusing the instance you own"
        release_lock
        cmd_env
        return 0
      fi
      release_lock
      die "RUN_ROOT $ROOT_REAL is owned by another live run (${S_RUN_ID}); refusing silent reuse. Set WAKEMESH_PG_RUN_ID=${S_RUN_ID} to confirm ownership, or run: $0 stop"
    fi
    release_lock
    die "ownership state for run ${S_RUN_ID} exists but the instance is not running (${SR_REASON}); refusing to overwrite it. Run: $0 stop"
  fi

  if [[ -e "$STATE_FILE" ]] || [[ -n "$(ls -A -- "$ROOT_REAL" 2>/dev/null || true)" ]]; then
    release_lock
    die "RUN_ROOT $ROOT_REAL is not empty and ownership cannot be proven (${LOAD_REASON:-existing files}); refusing to touch it. Nothing was changed."
  fi

  local port run_id
  port="$(
    python3 - <<'PY'
import socket
s = socket.socket()
s.bind(("127.0.0.1", 0))
print(s.getsockname()[1])
s.close()
PY
  )"
  [[ "$port" =~ ^[0-9]+$ ]] || { release_lock; die "could not allocate a socket port"; }
  run_id="$(python3 -c 'import uuid; print(uuid.uuid4().hex)')"

  mkdir -m 700 -- "$SOCKDIR"
  CREATED_SOCKDIR="$SOCKDIR"

  log "initdb (socket-only, trust, fixture user ${PGUSER_FIXTURE})"
  if ! "$PGBIN/initdb" -D "$DATADIR" -U "$PGUSER_FIXTURE" -A trust --no-locale -E UTF8 >/dev/null; then
    release_lock
    cleanup_partial_start
    die "initdb failed"
  fi
  CREATED_DATADIR="$DATADIR"

  cat >> "$DATADIR/postgresql.conf" <<EOF

# --- WakeMesh disposable test instance ---
listen_addresses = ''
unix_socket_directories = '${SOCKDIR}'
unix_socket_permissions = 0700
port = ${port}
max_connections = 50
fsync = off
synchronous_commit = off
full_page_writes = off
log_min_messages = warning
EOF

  log "starting postgres (port ${port}, socket ${SOCKDIR})"
  if ! "$PGBIN/pg_ctl" -D "$DATADIR" -l "$LOGFILE" -w -t 60 start >/dev/null 2>&1; then
    release_lock
    cleanup_partial_start
    die "postgres failed to start"
  fi

  local pid
  pid="$(head -1 -- "$DATADIR/postmaster.pid" 2>/dev/null || true)"
  [[ "$pid" =~ ^[0-9]+$ ]] || { release_lock; cleanup_partial_start; die "postmaster.pid missing after start"; }

  {
    printf 'FORMAT=%s\n' "$STATE_FORMAT"
    printf 'RUN_ID=%s\n' "$run_id"
    printf 'OWNER_UID=%s\n' "$(id -u)"
    printf 'PG_PID=%s\n' "$pid"
    printf 'PG_DATADIR=%s\n' "$DATADIR"
    printf 'PG_SOCKDIR=%s\n' "$SOCKDIR"
    printf 'PG_PORT=%s\n' "$port"
    printf 'PG_USER=%s\n' "$PGUSER_FIXTURE"
    printf 'PG_BIN=%s\n' "$PGBIN"
    printf 'CREATED_AT=%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  } > "$STATE_FILE.tmp.$$"
  chmod 600 -- "$STATE_FILE.tmp.$$"
  mv -f -- "$STATE_FILE.tmp.$$" "$STATE_FILE"

  CREATED_DATADIR=""
  CREATED_SOCKDIR=""
  release_lock

  # Prove the server answers over the private socket only.
  if ! "$PGBIN/psql" -h "$SOCKDIR" -p "$port" -U "$PGUSER_FIXTURE" -d postgres \
        -tAc "select 1;" >/dev/null 2>&1; then
    log "socket probe failed; stopping the instance this run started"
    ( "$0" stop ) >/dev/null 2>&1 || true
    die "instance did not answer on its private socket"
  fi

  log "ready (run ${run_id}, pid ${pid})"
  load_state || die "state file written by this run failed validation: ${LOAD_REASON}"
  cmd_env
}

cmd_env() {
  require_run_root
  load_state || die "no instance owned by this script for $ROOT_REAL (${LOAD_REASON}); run: $0 start"
  state_running || die "ownership state exists but the instance is not running (${SR_REASON})"
  cat <<EOF
export WAKEMESH_TEST_POSTGRES='Host=${S_PG_SOCKDIR};Port=${S_PG_PORT};Database=postgres;Username=${S_PG_USER}'
export WAKEMESH_PG_PORT='${S_PG_PORT}'
export WAKEMESH_PG_SOCKDIR='${S_PG_SOCKDIR}'
export WAKEMESH_PG_RUN_ROOT='${ROOT_REAL}'
export WAKEMESH_PG_RUN_ID='${S_RUN_ID}'
EOF
}

cmd_stop() {
  require_run_root
  if [[ ! -e "$RUN_ROOT" ]]; then
    log "no run root at $RUN_ROOT; nothing to stop"
    return 0
  fi
  [[ -L "$RUN_ROOT" ]] && die "refusing to stop through a symlinked RUN_ROOT: $RUN_ROOT"
  [[ -d "$RUN_ROOT" ]] || die "RUN_ROOT is not a directory: $RUN_ROOT"
  if ! acquire_lock; then
    die "lock busy: another pg-test-local invocation (pid ${LOCK_OWNER:-unknown}) is operating on $ROOT_REAL; nothing was changed"
  fi
  if ! load_state; then
    release_lock
    die "refusing to stop: ownership of $ROOT_REAL cannot be proven (${LOAD_REASON}); nothing was changed"
  fi

  if state_running; then
    log "stopping owned instance (run ${S_RUN_ID}, pid ${S_PG_PID})"
    if ! "$S_PG_BIN/pg_ctl" -D "$S_PG_DATADIR" -m fast -w -t 60 stop >/dev/null; then
      release_lock
      die "pg_ctl stop failed; state and data were left in place"
    fi
  elif kill -0 "$S_PG_PID" 2>/dev/null; then
    # A recorded pid is alive but cannot be proven to be this run's postmaster
    # (foreign process / pid reuse / changed argv). Fail closed, remove nothing.
    release_lock
    die "refusing to stop: recorded pid $S_PG_PID is alive but not provably this run's postmaster (${SR_REASON}); nothing was changed"
  else
    log "recorded postmaster is gone (${SR_REASON}); removing owned leftovers"
  fi

  # Remove only the paths this root owns, resolved and matched first.
  local real
  for real in "$DATADIR" "$SOCKDIR"; do
    case "$(realpath -m -- "$real")" in
      "$DATADIR"|"$SOCKDIR") ;;
      *) release_lock; die "refusing to remove unexpected path: $real" ;;
    esac
  done
  rm -rf -- "$DATADIR" "$SOCKDIR"
  rm -f -- "$LOGFILE" "$STATE_FILE"
  rm -f -- "$STATE_FILE.tmp."* 2>/dev/null || true
  if rmdir -- "$ROOT_REAL" 2>/dev/null; then
    log "stopped and removed the disposable instance"
  else
    log "stopped; left $ROOT_REAL in place because it still contains unowned files"
  fi
  release_lock
}

cmd_status() {
  require_run_root
  if [[ ! -e "$RUN_ROOT" ]]; then
    echo "not running (no run root at $RUN_ROOT)"
    return 1
  fi
  if ! load_state; then
    echo "not running (ownership cannot be proven: ${LOAD_REASON})"
    return 1
  fi
  if state_running; then
    echo "running run=${S_RUN_ID} pid=${S_PG_PID} port=${S_PG_PORT} socket=${S_PG_SOCKDIR}"
    return 0
  fi
  echo "not running (owned state present: ${SR_REASON})"
  return 1
}

# --- run: private temp root, deterministic cleanup -------------------------
on_signal() {
  local sig="$1"
  trap - EXIT INT TERM
  log "received signal; shutting down"
  if [[ -n "${DOTNET_PID:-}" ]]; then
    kill -TERM "$DOTNET_PID" 2>/dev/null || true
  fi
  if [[ "$CREATED_DATADIR" == "$DATADIR" && -d "$DATADIR" ]]; then
    stop_datadir_postmaster "$DATADIR" immediate || true
  fi
  ( "$0" stop ) >/dev/null 2>&1 || true
  release_lock
  if [[ "$RUN_ROOT_WAS_TEMP" == "1" && -d "$ROOT_REAL" && "$ROOT_REAL" == "${TMPDIR:-/tmp}/wakemesh-pg17-run."* ]]; then
    rm -rf -- "$ROOT_REAL" || true
  fi
  exit "$sig"
}

on_exit() {
  local rc=$?
  trap - EXIT INT TERM
  local crc=0
  if [[ "$CREATED_DATADIR" == "$DATADIR" && -d "$DATADIR" ]]; then
    stop_datadir_postmaster "$DATADIR" immediate || true
  fi
  ( "$0" stop ) >/dev/null 2>&1 || crc=$?
  release_lock
  if [[ "$crc" -eq 0 && "$RUN_ROOT_WAS_TEMP" == "1" && -d "$ROOT_REAL" \
        && "$ROOT_REAL" == "${TMPDIR:-/tmp}/wakemesh-pg17-run."* ]]; then
    rm -rf -- "$ROOT_REAL" || crc=$?
  elif [[ "$crc" -ne 0 ]]; then
    log "cleanup failed; leaving $ROOT_REAL in place for inspection"
  fi
  if [[ "$rc" -eq 0 && "$crc" -ne 0 ]]; then
    rc=1
  fi
  exit "$rc"
}

cmd_run() {
  local dotnet_bin="${DOTNET_ROOT:-$PWD/.tools/dotnet}/dotnet"
  [[ -x "$dotnet_bin" ]] || die "dotnet not found at $dotnet_bin (set DOTNET_ROOT)"
  local project="${1:-tests/WakeMesh.Api.Tests/WakeMesh.Api.Tests.csproj}"
  [[ $# -gt 0 ]] && shift
  [[ -z "$RUN_ROOT" ]] || die "run creates its own private root; unset WAKEMESH_PG_RUN_ROOT"
  local tmp_root
  tmp_root="$(mktemp -d "${TMPDIR:-/tmp}/wakemesh-pg17-run.XXXXXXXX")" || die "mktemp failed"
  chmod 700 -- "$tmp_root"
  RUN_ROOT="$tmp_root"
  export WAKEMESH_PG_RUN_ROOT="$tmp_root"
  RUN_ROOT_WAS_TEMP=1
  trap 'on_signal 130' INT
  trap 'on_signal 143' TERM
  trap 'on_exit' EXIT

  cmd_start
  load_state || die "state written by this run failed validation: ${LOAD_REASON}"
  export WAKEMESH_TEST_POSTGRES="Host=${S_PG_SOCKDIR};Port=${S_PG_PORT};Database=postgres;Username=${S_PG_USER}"
  export WAKEMESH_PG_PORT="$S_PG_PORT"
  export WAKEMESH_PG_SOCKDIR="$S_PG_SOCKDIR"
  export WAKEMESH_PG_RUN_ROOT="$ROOT_REAL"
  export WAKEMESH_PG_RUN_ID="$S_RUN_ID"

  # Run in the background and `wait` so a trapped signal is handled immediately
  # instead of after the child exits.
  "$dotnet_bin" test "$project" "$@" &
  DOTNET_PID=$!
  wait "$DOTNET_PID" && RUN_EXIT=0 || RUN_EXIT=$?
  exit "$RUN_EXIT"
}

# Release the operation lock on any unexpected exit path (start/stop).
trap 'release_lock' EXIT

case "${1:-}" in
  start) cmd_start ;;
  stop) cmd_stop ;;
  env) cmd_env ;;
  status) cmd_status ;;
  run) shift || true; cmd_run "$@" ;;
  help|-h|--help) usage ;;
  *) usage; exit 2 ;;
esac
