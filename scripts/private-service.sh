#!/usr/bin/env bash
# Run one Anima service as a private instance that a worktree worker owns.
#
# Shared services live on 7410-7416 and are off limits. Private instances use
# ports >= 7500, a PID file under ~/.cache/anima/private-services, and a log next
# to it. start/stop/status only ever signal the PID recorded for that port.
set -euo pipefail

reserved_first=7410
reserved_last=7416
min_port=7500
state_dir="${XDG_CACHE_HOME:-$HOME/.cache}/anima/private-services"

usage() {
  cat <<'EOF'
usage:
  scripts/private-service.sh start --dll <path> --port <port> [--env KEY=VALUE]...
  scripts/private-service.sh stop --port <port>
  scripts/private-service.sh status --port <port>

Ports 7410-7416 are reserved for shared services. Private services must use
7500 or above. start writes <state>/<port>.pid and <state>/<port>.log and
prints the PID; stop signals only that recorded PID.
EOF
}

die() {
  echo "private-service: $*" >&2
  exit 1
}

usage_error() {
  echo "private-service: $*" >&2
  usage >&2
  exit 2
}

pid_file_for() {
  printf '%s/%s.pid\n' "$state_dir" "$1"
}

log_file_for() {
  printf '%s/%s.log\n' "$state_dir" "$1"
}

validate_port() {
  local port="$1"
  case "$port" in
    ''|*[!0-9]*) usage_error "port must be a number: '$port'" ;;
  esac
  if ((port < 1 || port > 65535)); then
    usage_error "port out of range: $port"
  fi
  if ((port >= reserved_first && port <= reserved_last)); then
    die "port $port is reserved for shared services ($reserved_first-$reserved_last)"
  fi
  if ((port < min_port)); then
    die "private services must use port $min_port or above (got $port)"
  fi
}

port_in_use() {
  local port="$1"
  (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null
}

read_pid() {
  local port="$1" file pid
  file="$(pid_file_for "$port")"
  [ -f "$file" ] || return 1
  pid="$(tr -d '[:space:]' <"$file")"
  case "$pid" in
    ''|*[!0-9]*) return 1 ;;
  esac
  printf '%s\n' "$pid"
}

pid_matches_port() {
  local pid="$1" port="$2" cmdline
  # Fail closed when the OS cannot prove process identity.
  [ -r "/proc/$pid/cmdline" ] || return 1
  cmdline="$(tr '\0' ' ' <"/proc/$pid/cmdline")"
  [[ "$cmdline" =~ (^|[^0-9])${port}([^0-9]|$) ]]
}

start_service() {
  local dll="" port="" arg
  local -a env_assignments=()

  while (($# > 0)); do
    arg="$1"
    shift
    case "$arg" in
      --dll)
        [ $# -gt 0 ] || usage_error "--dll needs a path"
        dll="$1"
        shift
        ;;
      --port)
        [ $# -gt 0 ] || usage_error "--port needs a value"
        port="$1"
        shift
        ;;
      --env)
        [ $# -gt 0 ] || usage_error "--env needs KEY=VALUE"
        case "$1" in
          [A-Za-z_]*=*) env_assignments+=("$1") ;;
          *) usage_error "--env must be KEY=VALUE: '$1'" ;;
        esac
        case "${1%%=*}" in
          *[!A-Za-z0-9_]*) usage_error "--env name is not a shell identifier: '${1%%=*}'" ;;
        esac
        shift
        ;;
      -h|--help)
        usage
        exit 0
        ;;
      *)
        usage_error "unknown start argument: $arg"
        ;;
    esac
  done

  [ -n "$port" ] || usage_error "start requires --port"
  [ -n "$dll" ] || usage_error "start requires --dll"
  validate_port "$port"
  [ -f "$dll" ] || die "dll not found: $dll"
  command -v dotnet >/dev/null 2>&1 || die "dotnet is not on PATH"

  dll="$(cd "$(dirname "$dll")" && pwd)/$(basename "$dll")"
  local pid_file log_file existing_pid pid
  pid_file="$(pid_file_for "$port")"
  log_file="$(log_file_for "$port")"

  if existing_pid="$(read_pid "$port")" && kill -0 "$existing_pid" 2>/dev/null; then
    die "port $port is already tracked by PID $existing_pid ($pid_file)"
  fi
  if port_in_use "$port"; then
    die "port $port is already listening; choose another private port"
  fi

  umask 077
  mkdir -p "$state_dir"
  : >"$log_file"

  env "${env_assignments[@]+"${env_assignments[@]}"}" \
    nohup dotnet "$dll" --urls "http://127.0.0.1:$port" \
    >>"$log_file" 2>&1 </dev/null &
  pid=$!

  printf '%s\n' "$pid" >"$pid_file"

  # The process can still die during startup (bad DLL, port race, config error).
  sleep 0.5
  if ! kill -0 "$pid" 2>/dev/null; then
    rm -f "$pid_file"
    echo "private-service: PID $pid exited during startup; log tail ($log_file):" >&2
    tail -n 20 "$log_file" >&2 || true
    exit 1
  fi

  printf '%s\n' "$pid"
}

stop_service() {
  local port="" arg pid pid_file
  while (($# > 0)); do
    arg="$1"
    shift
    case "$arg" in
      --port)
        [ $# -gt 0 ] || usage_error "--port needs a value"
        port="$1"
        shift
        ;;
      -h|--help)
        usage
        exit 0
        ;;
      *)
        usage_error "unknown stop argument: $arg"
        ;;
    esac
  done

  [ -n "$port" ] || usage_error "stop requires --port"
  validate_port "$port"
  pid_file="$(pid_file_for "$port")"

  if ! pid="$(read_pid "$port")"; then
    echo "private-service: no usable PID file for port $port ($pid_file)" >&2
    exit 1
  fi
  if ! kill -0 "$pid" 2>/dev/null; then
    rm -f "$pid_file"
    echo "private-service: PID $pid for port $port is not running; removed stale PID file" >&2
    exit 1
  fi
  if ! pid_matches_port "$pid" "$port"; then
    die "PID $pid does not look like a service on port $port; refusing to signal it"
  fi

  kill "$pid"
  local attempts=0
  while kill -0 "$pid" 2>/dev/null; do
    attempts=$((attempts + 1))
    if ((attempts > 100)); then
      die "PID $pid did not exit after SIGTERM; PID file kept at $pid_file"
    fi
    sleep 0.1
  done

  rm -f "$pid_file"
  printf 'stopped port=%s pid=%s\n' "$port" "$pid"
}

status_service() {
  local port="" arg pid pid_file
  while (($# > 0)); do
    arg="$1"
    shift
    case "$arg" in
      --port)
        [ $# -gt 0 ] || usage_error "--port needs a value"
        port="$1"
        shift
        ;;
      -h|--help)
        usage
        exit 0
        ;;
      *)
        usage_error "unknown status argument: $arg"
        ;;
    esac
  done

  [ -n "$port" ] || usage_error "status requires --port"
  validate_port "$port"
  pid_file="$(pid_file_for "$port")"

  if ! pid="$(read_pid "$port")"; then
    printf 'stopped port=%s reason=no-usable-pid-file\n' "$port"
    exit 1
  fi
  if ! kill -0 "$pid" 2>/dev/null; then
    printf 'stopped port=%s pid=%s reason=stale-pid-file\n' "$port" "$pid"
    exit 1
  fi
  if ! pid_matches_port "$pid" "$port"; then
    printf 'unknown port=%s pid=%s reason=cmdline-mismatch\n' "$port" "$pid"
    exit 1
  fi

  printf 'running port=%s pid=%s\n' "$port" "$pid"
}

[ $# -gt 0 ] || { usage >&2; exit 2; }
command="$1"
shift
case "$command" in
  start) start_service "$@" ;;
  stop) stop_service "$@" ;;
  status) status_service "$@" ;;
  -h|--help|help) usage ;;
  *) usage_error "unknown command: $command" ;;
esac
