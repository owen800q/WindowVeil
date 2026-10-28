#!/usr/bin/env bash
# Generates GitHub release notes for a version from Conventional Commits, the build test summary and the
# UI evidence manifest. Usage: generate-release-notes.sh <version> <previous_tag|""> <repo> <test-summary.md> <evidence-manifest.json>
set -euo pipefail
version="$1"; previous="$2"; repo="$3"; tests="$4"; manifest="$5"
tag="v$version"
range="${previous:+$previous..}HEAD"
asset_base="https://github.com/$repo/releases/download/$tag"

section() {
  local title="$1" pattern="$2" lines
  lines=$(git log --no-merges --format='%s (%h)' "$range" | grep -E "$pattern" || true)
  if [[ -n "$lines" ]]; then
    echo "### $title"; echo
    sed -E 's/^([a-z]+)(\([^)]*\))?!?: /- /' <<<"$lines"
    echo
  fi
}

{
  echo "# WindowVeil $tag"
  echo
  echo "WindowVeil is a .NET 8 framework for visually masking, highlighting and extending external Windows application windows with native and WPF overlays."
  echo
  echo "## RetroCap demonstration"
  echo
  echo "Captured on the interactive Windows test runner from the real RetroCap v1.0 executable (SHA-256 verified) for commit \`$(git rev-parse --short HEAD)\`:"
  echo
  echo "![RetroCap before/after comparison]($asset_base/retrocap-comparison.png)"
  echo
  echo "## Changes"
  echo
  if git log --format='%B' "$range" | grep -qE '^(BREAKING[ -]CHANGE:|[a-z]+(\([^)]*\))?!:)'; then
    section "Breaking changes" '^[a-z]+(\([^)]*\))?!:'
  fi
  section "Features" '^feat(\([^)]*\))?!?:'
  section "Fixes" '^fix(\([^)]*\))?!?:'
  section "Performance" '^perf(\([^)]*\))?!?:'
  section "Documentation" '^docs(\([^)]*\))?!?:'
  other=$(git log --no-merges --format='%s (%h)' "$range" | grep -vE '^(feat|fix|perf|docs)(\([^)]*\))?!?:' || true)
  if [[ -n "$other" ]]; then echo "### Other changes"; echo; sed -E 's/^/- /' <<<"$other"; echo; fi
  echo "## Requirements"
  echo
  echo "- Windows 10 (1809 or later) or Windows 11, x64."
  echo "- Libraries: .NET 8 (\`net8.0\` / \`net8.0-windows\`). .NET 8 reaches end of support on November 10, 2026."
  echo "- Demo and editor ZIPs are self-contained .NET 8 win-x64 builds; no runtime installation is required."
  echo
  echo "## Downloads"
  echo
  echo "- \`WindowVeil-Demo-win-x64.zip\` — self-contained demo application"
  echo "- \`WindowVeil-Editor-win-x64.zip\` — self-contained visual rule editor"
  echo "- \`*.nupkg\` — NuGet packages (Core, Native, Wpf, Automation, Tracking, Capture, Ocr, Ocr.Paddle, Profiles)"
  echo "- \`SHA256SUMS\` — checksums of every asset"
  echo "- \`retrocap-*.png\` — screenshot evidence"
  echo
  echo "## Test evidence"
  echo
  if [[ -f "$tests" ]]; then cat "$tests"; echo; fi
  if [[ -f "$manifest" ]]; then
    echo "### Interactive RetroCap UI test"
    echo
    python3 - "$manifest" <<'PY'
import json, sys
m = json.load(open(sys.argv[1], encoding="utf-8"))
env = m.get("environment", {})
scr = env.get("screen", {})
print(f"- Result: **{m.get('result')}** at {m.get('timestampUtc')} on {env.get('os', 'unknown OS')}, "
      f"{scr.get('width')}x{scr.get('height')} @ {env.get('scalePercent')}% scaling")
print(f"- RetroCap {m.get('retroCap', {}).get('version')} (asset sha256 `{m.get('retroCap', {}).get('assetSha256')}`)")
for s in m.get("scenarios", []):
    ok = "passed" if s.get("passed") else "FAILED"
    print(f"- {s.get('name')}: {ok} ({len(s.get('assertions', []))} assertions)")
PY
    echo
  fi
  echo "## Known limitations"
  echo
  echo "- Overlays are visual only: masked controls still exist and stay functional in the target process."
  echo "- Masks are not guaranteed to appear in every capture pipeline and do not redact the underlying data."
  echo "- Accessibility trees can be incomplete; OCR results and OCR geometry can be wrong."
  echo "- Pass-through masks leave the underlying control clickable."
}
