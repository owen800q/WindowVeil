#!/usr/bin/env bash
# Computes the next semantic version for the commit at HEAD from existing v* tags and
# Conventional Commit messages since the previous release tag.
#
# Policy
#   * No previous release tag             -> 0.1.0
#   * HEAD already carries a release tag  -> skip=true (never re-release or move a tag)
#   * "Release-As: X.Y.Z" commit footer   -> exactly X.Y.Z (must be greater than the last release)
#   * >= 1.0.0: breaking -> major, feat -> minor, anything else -> patch
#   * <  1.0.0: breaking -> minor, feat -> minor, anything else -> patch
#     (pre-1.0 APIs may change in minor versions; 1.0.0 is cut deliberately with Release-As)
#   A breaking change is a subject like "feat!:" / "fix(scope)!:" or a "BREAKING CHANGE:" footer.
#
# Output: key=value lines (version, tag, previous_tag, bump, skip) — also appended to
# $GITHUB_OUTPUT when that variable is set.
set -euo pipefail

ref="${1:-HEAD}"
semver_re='^v([0-9]+)\.([0-9]+)\.([0-9]+)$'

emit() {
  echo "$1=$2"
  if [[ -n "${GITHUB_OUTPUT:-}" ]]; then echo "$1=$2" >> "$GITHUB_OUTPUT"; fi
}

existing=$(git tag --points-at "$ref" --list 'v*' | grep -E "$semver_re" | sort -V | tail -n1 || true)
if [[ -n "$existing" ]]; then
  emit skip true
  emit version "${existing#v}"
  emit tag "$existing"
  emit previous_tag "$existing"
  emit bump none
  exit 0
fi

previous=$(git tag --merged "$ref" --list 'v*' | grep -E "$semver_re" | sort -V | tail -n1 || true)

if [[ -z "$previous" ]]; then
  major=0; minor=0; patch=0
  range="$ref"
else
  [[ "$previous" =~ $semver_re ]]
  major=${BASH_REMATCH[1]}; minor=${BASH_REMATCH[2]}; patch=${BASH_REMATCH[3]}
  range="$previous..$ref"
fi

log=$(git log --format='%s%n%b%n--END--' "$range")

release_as=$(grep -E '^Release-As: *[0-9]+\.[0-9]+\.[0-9]+ *$' <<<"$log" | head -n1 | sed -E 's/^Release-As: *//; s/ *$//' || true)
breaking=false; feature=false
if grep -qE '^[a-zA-Z]+(\([^)]*\))?!:' <<<"$log" || grep -qE '^BREAKING[ -]CHANGE:' <<<"$log"; then breaking=true; fi
if grep -qE '^feat(\([^)]*\))?:' <<<"$log"; then feature=true; fi

if [[ -n "$release_as" ]]; then
  version="$release_as"; bump="release-as"
  if [[ -n "$previous" ]] && [[ "$(printf '%s\n%s\n' "${previous#v}" "$version" | sort -V | tail -n1)" != "$version" || "${previous#v}" == "$version" ]]; then
    echo "Release-As $version is not greater than the last release $previous" >&2
    exit 1
  fi
elif [[ -z "$previous" ]]; then
  version="0.1.0"; bump="initial"
elif [[ "$breaking" == true && "$major" -ge 1 ]]; then
  version="$((major + 1)).0.0"; bump="major"
elif [[ "$breaking" == true || "$feature" == true ]]; then
  version="$major.$((minor + 1)).0"; bump="minor"
else
  version="$major.$minor.$((patch + 1))"; bump="patch"
fi

emit skip false
emit version "$version"
emit tag "v$version"
emit previous_tag "${previous:-}"
emit bump "$bump"
