#!/usr/bin/env bash
# Self-test for tools/compute-version.sh using a throwaway repository.
set -euo pipefail
script="$(cd "$(dirname "$0")/.." && pwd)/compute-version.sh"
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
cd "$tmp"; git init -q; git config user.email ci@example.com; git config user.name ci
c() { git commit -q --allow-empty -m "$1"; }
v() { bash "$script" | grep '^version=' | cut -d= -f2; }
expect() { local got; got=$(v); [[ "$got" == "$1" ]] || { echo "expected $1, got $got ($2)"; exit 1; }; echo "ok: $2 -> $got"; }
c "chore: init";                      expect 0.1.0 "first release"
git tag v0.1.0
[[ "$(bash "$script" | grep '^skip=')" == "skip=true" ]] && echo "ok: already tagged commit is skipped"
c "fix: bug";                         expect 0.1.1 "fix -> patch"
c "feat(api): thing";                 expect 0.2.0 "feat -> minor"
c "refactor!: break";                 expect 0.2.0 "pre-1.0 breaking -> minor"
git tag v0.2.0
c "$(printf 'chore: ga\n\nRelease-As: 1.0.0')"; expect 1.0.0 "Release-As"
git tag v1.0.0
c "docs: x";                          expect 1.0.1 "other -> patch"
c "feat: y";                          expect 1.1.0 "feat -> minor"
c "$(printf 'fix: z\n\nBREAKING CHANGE: api')"; expect 2.0.0 "breaking footer -> major"
echo "all version tests passed"
