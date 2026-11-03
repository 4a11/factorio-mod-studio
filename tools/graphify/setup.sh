#!/usr/bin/env bash
# Optional, local-only: build a code knowledge graph of this repository with graphify
# and export it as an Obsidian vault. See docs/graphify.md.
set -euo pipefail

# Pinned on purpose (supply chain): the PyPI package is "graphifyy" (double y). Other "graphify*"
# packages are NOT affiliated with the project. Bump this version deliberately, after reviewing
# the release (see "Upgrading" in docs/graphify.md).
GRAPHIFY_VERSION="0.9.100"

usage() {
  cat <<'USAGE'
Usage: tools/graphify/setup.sh [options]

  (no options)    install graphify into .fmstudio/graphify-venv, build the graph, export the Obsidian vault
  --hooks         also install the local Claude Code rule/hooks (CLAUDE.md, .claude/) and, in a git
                  checkout, git hooks that keep the graph fresh. Opt-in.
  --refresh       skip installation; only rebuild the graph and re-export the vault
  --no-obsidian   do not export the Obsidian vault
  --uninstall     remove hooks (if installed) and the virtual environment; keeps graphify-out/
  --purge         with --uninstall: also delete graphify-out/
  --help          show this help
USAGE
}

HOOKS=0; REFRESH=0; OBSIDIAN=1; UNINSTALL=0; PURGE=0
for arg in "$@"; do
  case "$arg" in
    --hooks) HOOKS=1 ;;
    --refresh) REFRESH=1 ;;
    --no-obsidian) OBSIDIAN=0 ;;
    --uninstall) UNINSTALL=1 ;;
    --purge) PURGE=1 ;;
    --help|-h) usage; exit 0 ;;
    *) echo "error: unknown option '$arg'" >&2; usage >&2; exit 2 ;;
  esac
done
if [ "$PURGE" -eq 1 ] && [ "$UNINSTALL" -eq 0 ]; then
  echo "error: --purge only makes sense together with --uninstall" >&2; exit 2
fi

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/../.." && pwd)"
cd "$REPO_ROOT"

VENV_REL=".fmstudio/graphify-venv"
VENV="$REPO_ROOT/$VENV_REL"
VENV_PY="$VENV/bin/python"
GRAPHIFY="$VENV/bin/graphify"
VAULT_REL="graphify-out/obsidian"

# Safety net: this tool only needs the local AST path; never use an LLM backend.
export GRAPHIFY_BACKEND=none

fail() { echo "error: $*" >&2; exit 1; }

installed_version() {
  [ -x "$VENV_PY" ] || return 0
  "$VENV_PY" -c "import importlib.metadata as m; print(m.version('graphifyy'))" 2>/dev/null || true
}

# ---------------------------------------------------------------- uninstall
if [ "$UNINSTALL" -eq 1 ]; then
  if [ -x "$GRAPHIFY" ]; then
    "$GRAPHIFY" claude uninstall || echo "warning: 'graphify claude uninstall' failed; check CLAUDE.md and .claude/settings.json by hand" >&2
    if [ -e "$REPO_ROOT/.git" ]; then
      "$GRAPHIFY" hook uninstall || echo "warning: 'graphify hook uninstall' failed; check .git/hooks by hand" >&2
    fi
  else
    echo "graphify is not installed; skipping hook removal"
  fi
  rm -rf -- "$VENV"
  echo "removed $VENV_REL"
  if [ "$PURGE" -eq 1 ]; then
    rm -rf -- "$REPO_ROOT/graphify-out"
    echo "removed graphify-out/"
  else
    echo "kept graphify-out/ (derived data; use --uninstall --purge to delete it)"
  fi
  exit 0
fi

# ---------------------------------------------------------------- install
if [ "$REFRESH" -eq 1 ]; then
  [ -x "$GRAPHIFY" ] || fail "graphify is not installed ($VENV_REL missing). Run tools/graphify/setup.sh without --refresh first."
else
  PY=""
  for cand in python3 python; do
    if command -v "$cand" >/dev/null 2>&1 \
       && "$cand" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 10) else 1)' >/dev/null 2>&1; then
      PY="$cand"; break
    fi
  done
  [ -n "$PY" ] || fail "Python 3.10 or newer was not found. Install it (https://www.python.org/downloads/ or your package manager) and re-run."

  have="$(installed_version)"
  if [ "$have" = "$GRAPHIFY_VERSION" ] && [ -x "$GRAPHIFY" ]; then
    echo "graphifyy $GRAPHIFY_VERSION already installed"
  else
    if [ -n "$have" ]; then
      echo "found graphifyy $have, installing pinned $GRAPHIFY_VERSION"
    fi
    if [ ! -x "$VENV_PY" ]; then
      echo "creating virtual environment in $VENV_REL"
      "$PY" -m venv "$VENV" || fail "could not create a virtual environment (on Debian/Ubuntu: apt install python3-venv)."
    fi
    echo "installing graphifyy==$GRAPHIFY_VERSION"
    "$VENV_PY" -m pip install --disable-pip-version-check "graphifyy==$GRAPHIFY_VERSION" \
      || fail "pip install of graphifyy==$GRAPHIFY_VERSION failed (network or PyPI problem?)."
  fi
fi

# ---------------------------------------------------------------- build
echo "building the code graph (local, no LLM)..."
"$GRAPHIFY" update . || fail "'graphify update' failed."

if [ "$OBSIDIAN" -eq 1 ]; then
  echo "exporting the Obsidian vault..."
  "$GRAPHIFY" export obsidian --dir "$VAULT_REL" || fail "'graphify export obsidian' failed."
fi

if [ "$HOOKS" -eq 1 ]; then
  echo "installing local Claude Code integration..."
  "$GRAPHIFY" claude install || fail "'graphify claude install' failed."
  if [ -e "$REPO_ROOT/.git" ]; then
    "$GRAPHIFY" hook install || fail "'graphify hook install' failed."
    # The hook installer also adds a merge-driver line to the TRACKED .gitattributes. graph.json is
    # git-ignored here, so the line is useless and would only dirty the working tree; remove it.
    if [ -f .gitattributes ] && grep -qxF 'graphify-out/graph.json merge=graphify' .gitattributes; then
      grep -vxF 'graphify-out/graph.json merge=graphify' .gitattributes > .gitattributes.tmp || true
      if [ -s .gitattributes.tmp ]; then mv .gitattributes.tmp .gitattributes; else rm -f .gitattributes.tmp .gitattributes; fi
    fi
  else
    echo "no .git here; skipping git hooks"
  fi
fi

# ---------------------------------------------------------------- summary
counts=""
if [ -f graphify-out/graph.json ]; then
  counts="$("$VENV_PY" - <<'PY' 2>/dev/null || true
import json
g = json.load(open("graphify-out/graph.json", encoding="utf-8"))
n = len(g.get("nodes", []))
e = len(g.get("links", g.get("edges", [])))
print(f"{n} nodes, {e} edges")
PY
)"
fi

echo
echo "Done.${counts:+ Graph: $counts.}"
echo "  report : graphify-out/GRAPH_REPORT.md"
echo "  graph  : graphify-out/graph.html (open in a browser)"
if [ "$OBSIDIAN" -eq 1 ]; then
  echo "  vault  : $VAULT_REL  (Obsidian: 'Open folder as vault', then pick this folder)"
fi
echo
echo "Next steps:"
echo "  $VENV_REL/bin/graphify query \"how are mods validated?\""
echo "  $VENV_REL/bin/graphify explain \"ModCatalog\""
echo "  tools/graphify/setup.sh --refresh      # after code changes"
