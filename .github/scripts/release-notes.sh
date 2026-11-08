#!/bin/sh
# Prints the notes of a GitHub release: the version's section of CHANGELOG.md, then how to install it.
# Usage: release-notes.sh <tag> [--no-install]   (tag: v0.2.0; --no-install for old releases without the desktop app)
set -e
tag="$1"
version="${tag#v}"
root="$(cd "$(dirname "$0")/../.." && pwd)"

# From "## [0.2.0] - date" up to the next version heading or the link list at the end.
section=$(awk -v head="## [$version]" '
  index($0, head) == 1 { found = 1; next }
  found && (/^## \[/ || /^\[[^]]+\]: /) { exit }
  found { print }
' "$root/CHANGELOG.md")

if [ -z "$(printf '%s' "$section" | tr -d '[:space:]')" ]; then
  echo "CHANGELOG.md has no section \"## [$version]\"" >&2
  exit 1
fi

# Drop the blank lines after the heading, and join wrapped lines (indented, not a nested "- " item):
# GitHub shows every line break of release notes.
printf '%s\n' "$section" | sed -e '/./,$!d' | awk '
  /^ +[^ -]/ || /^ +-[^ ]/ { sub(/^ +/, ""); line = line " " $0; next }
  { if (started) print line; line = $0; started = 1 }
  END { if (started) print line }
'
if [ "$2" != "--no-install" ]; then
  echo
  cat "$root/.github/release-install.md"
fi
