#!/usr/bin/env bash
# Compile every .proto under contracts/proto into a single descriptor set.
# Agents can add a contract and run this; no C# project has to be rebuilt.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
proto_root="$root/contracts/proto"
out_dir="$root/contracts/descriptors"
protoc_bin="${PROTOC:-protoc}"

if ! command -v "$protoc_bin" >/dev/null 2>&1; then
  echo "protoc is required to compile contracts (install protobuf or set PROTOC)." >&2
  exit 1
fi

mapfile -t protos < <(find "$proto_root" -name '*.proto' -type f | sort)
if [ "${#protos[@]}" -eq 0 ]; then
  echo "No .proto files found under $proto_root" >&2
  exit 1
fi

mkdir -p "$out_dir"
out="$out_dir/anima.pb"

"$protoc_bin" \
  --proto_path="$proto_root" \
  --include_imports \
  --include_source_info \
  --descriptor_set_out="$out" \
  "${protos[@]}"

echo "wrote $out"
