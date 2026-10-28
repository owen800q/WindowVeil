#!/usr/bin/env bash
# Downloads the pinned PP-OCRv6 ONNX models listed in tools/models.lock.json and verifies their SHA-256.
#
# Usage: tools/download-models.sh [--variant tiny|small|medium|all] [--dest <dir>] [--force]
#   --variant  model size to download (default: small)
#   --dest     destination root (default: <repo>/models); files land in <dest>/<model>/<file>,
#              e.g. models/PP-OCRv6_small_det/inference.onnx
#   --force    re-download files even when a verified copy exists
#
# Every URL names an exact Hugging Face commit; "latest"/main is never used. Any size or hash mismatch
# deletes the partial file and fails with exit code 1. Requires curl and sha256sum (or shasum).
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/.." && pwd)"
lock="$script_dir/models.lock.json"
variant="small"
dest="$repo_root/models"
force=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --variant) variant="${2:?--variant needs a value}"; shift 2 ;;
    --dest) dest="${2:?--dest needs a value}"; shift 2 ;;
    --force) force=1; shift ;;
    -h|--help) sed -n '2,13p' "$0"; exit 0 ;;
    *) echo "error: unknown argument '$1'" >&2; exit 2 ;;
  esac
done

case "$variant" in
  tiny|small|medium|all) ;;
  *) echo "error: --variant must be tiny, small, medium or all (got '$variant')" >&2; exit 2 ;;
esac

[[ -f "$lock" ]] || { echo "error: manifest not found: $lock" >&2; exit 1; }
command -v curl >/dev/null || { echo "error: curl is required" >&2; exit 1; }

sha256_of() {
  if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1
  elif command -v shasum >/dev/null; then shasum -a 256 "$1" | cut -d' ' -f1
  else echo "error: sha256sum or shasum is required" >&2; exit 1
  fi
}

# One JSON object per line in the "files" array (see models.lock.json); extract a string/number field.
field() { sed -n "s/.*\"$2\": *\"\{0,1\}\([^\",}]*\)\"\{0,1\}.*/\1/p" <<<"$1"; }

entries=()
while IFS= read -r line; do entries+=("$line"); done < <(grep '"variant":' "$lock")
[[ ${#entries[@]} -gt 0 ]] || { echo "error: no entries in $lock" >&2; exit 1; }

count=0
for entry in "${entries[@]}"; do
  v="$(field "$entry" variant)"
  [[ "$variant" == "all" || "$v" == "$variant" ]] || continue
  model="$(field "$entry" model)"; repo="$(field "$entry" repo)"; rev="$(field "$entry" revision)"
  file="$(field "$entry" file)"; sha="$(field "$entry" sha256)"; size="$(field "$entry" size)"
  if [[ ! "$rev" =~ ^[0-9a-f]{40}$ ]]; then echo "error: $repo has no pinned 40-hex revision in $lock" >&2; exit 1; fi
  if [[ ! "$sha" =~ ^[0-9a-f]{64}$ ]]; then echo "error: $repo/$file has no sha256 in $lock" >&2; exit 1; fi

  out_dir="$dest/$model"; out="$out_dir/$file"
  mkdir -p "$out_dir"
  if [[ $force -eq 0 && -f "$out" && "$(sha256_of "$out")" == "$sha" ]]; then
    echo "ok       $model/$file (already verified)"; count=$((count + 1)); continue
  fi

  url="https://huggingface.co/$repo/resolve/$rev/$file"
  echo "download $url"
  tmp="$out.partial"
  rm -f "$tmp"
  if ! curl --fail --location --silent --show-error --retry 3 --output "$tmp" "$url"; then
    rm -f "$tmp"; echo "error: download failed: $url" >&2; exit 1
  fi
  actual_size=$(wc -c <"$tmp" | tr -d ' ')
  if [[ "$actual_size" != "$size" ]]; then
    rm -f "$tmp"; echo "error: SIZE MISMATCH for $model/$file: expected $size bytes, got $actual_size" >&2; exit 1
  fi
  actual="$(sha256_of "$tmp")"
  if [[ "$actual" != "$sha" ]]; then
    rm -f "$tmp"; echo "error: SHA-256 MISMATCH for $model/$file: expected $sha, got $actual" >&2; exit 1
  fi
  mv -f "$tmp" "$out"
  echo "verified $model/$file sha256=$sha"
  count=$((count + 1))
done

[[ $count -gt 0 ]] || { echo "error: no files matched variant '$variant'" >&2; exit 1; }
echo "done: $count file(s) in $dest (license: Apache-2.0, see https://huggingface.co/PaddlePaddle)"
