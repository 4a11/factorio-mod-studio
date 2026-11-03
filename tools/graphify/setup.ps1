<#
.SYNOPSIS
  Optional, local-only: build a code knowledge graph of this repository with graphify and export it
  as an Obsidian vault. See docs/graphify.md.

.DESCRIPTION
  Run from anywhere. If script execution is blocked on your machine, use:
    powershell -ExecutionPolicy Bypass -File tools\graphify\setup.ps1

.PARAMETER Hooks
  Also install the local Claude Code rule/hooks (CLAUDE.md, .claude\) and, in a git checkout, git hooks
  that keep the graph fresh. Opt-in.
.PARAMETER Refresh
  Skip installation; only rebuild the graph and re-export the vault.
.PARAMETER NoObsidian
  Do not export the Obsidian vault.
.PARAMETER Uninstall
  Remove hooks (if installed) and the virtual environment; keeps graphify-out\.
.PARAMETER Purge
  With -Uninstall: also delete graphify-out\.
.PARAMETER Help
  Show this help.
#>
[CmdletBinding()]
param(
    [switch]$Hooks,
    [switch]$Refresh,
    [switch]$NoObsidian,
    [switch]$Uninstall,
    [switch]$Purge,
    [switch]$Help
)

$ErrorActionPreference = 'Stop'

# Pinned on purpose (supply chain): the PyPI package is "graphifyy" (double y). Other "graphify*"
# packages are NOT affiliated with the project. Bump this version deliberately, after reviewing
# the release (see "Upgrading" in docs/graphify.md).
$GraphifyVersion = '0.9.100'

if ($Help) {
    Get-Help $PSCommandPath -Detailed
    exit 0
}

function Fail([string]$Message) {
    [Console]::Error.WriteLine("error: $Message")
    exit 1
}

# Runs a native command and fails with $Message on a non-zero exit code.
# Windows PowerShell 5.1 turns native stderr output into errors under 'Stop' in some hosts (ISE, CI); pip and
# graphify do write to stderr, so judge by the exit code only.
function Invoke-Native([string]$Exe, [string[]]$ArgList, [string]$Message) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Exe @ArgList } finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw $Message }
}

# Like Invoke-Native, but only returns the exit code (used where failure is just a warning).
function Invoke-NativeSoft([string]$Exe, [string[]]$ArgList) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Exe @ArgList } finally { $ErrorActionPreference = $previous }
    return $LASTEXITCODE
}

try {
    if ($Purge -and -not $Uninstall) { throw '-Purge only makes sense together with -Uninstall' }

    $RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
    Set-Location -LiteralPath $RepoRoot

    $VenvRel   = '.fmstudio\graphify-venv'
    $Venv      = Join-Path $RepoRoot $VenvRel
    $VenvPy    = Join-Path $Venv 'Scripts\python.exe'
    $Graphify  = Join-Path $Venv 'Scripts\graphify.exe'
    $VaultRel  = 'graphify-out\obsidian'

    # Safety net: this tool only needs the local AST path; never use an LLM backend.
    $env:GRAPHIFY_BACKEND = 'none'
    # Avoid console-encoding errors on Windows code pages.
    $env:PYTHONUTF8 = '1'

    function Get-InstalledVersion {
        if (-not (Test-Path -LiteralPath $VenvPy)) { return '' }
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'   # Windows PowerShell 5.1 turns native stderr into errors otherwise
        try {
            $v = & $VenvPy -c "import importlib.metadata as m; print(m.version('graphifyy'))" 2>$null
            if ($LASTEXITCODE -ne 0 -or -not $v) { return '' }
            return ([string]$v).Trim()
        } finally { $ErrorActionPreference = $prev }
    }

    # ------------------------------------------------------------ uninstall
    if ($Uninstall) {
        if (Test-Path -LiteralPath $Graphify) {
            if ((Invoke-NativeSoft $Graphify @('claude', 'uninstall')) -ne 0) { Write-Warning "'graphify claude uninstall' failed; check CLAUDE.md and .claude\settings.json by hand" }
            if (Test-Path -LiteralPath (Join-Path $RepoRoot '.git')) {
                if ((Invoke-NativeSoft $Graphify @('hook', 'uninstall')) -ne 0) { Write-Warning "'graphify hook uninstall' failed; check .git\hooks by hand" }
            }
        } else {
            Write-Host 'graphify is not installed; skipping hook removal'
        }
        if (Test-Path -LiteralPath $Venv) { Remove-Item -LiteralPath $Venv -Recurse -Force }
        Write-Host "removed $VenvRel"
        if ($Purge) {
            $out = Join-Path $RepoRoot 'graphify-out'
            if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
            Write-Host 'removed graphify-out\'
        } else {
            Write-Host 'kept graphify-out\ (derived data; use -Uninstall -Purge to delete it)'
        }
        exit 0
    }

    # ------------------------------------------------------------ install
    if ($Refresh) {
        if (-not (Test-Path -LiteralPath $Graphify)) {
            throw "graphify is not installed ($VenvRel missing). Run tools\graphify\setup.ps1 without -Refresh first."
        }
    } else {
        # Candidates: python3, python, and the Windows launcher "py -3".
        $candidates = @(
            ,@('python3')
            ,@('python')
            ,@('py', '-3')
        )
        $pyExe = $null; $pyPre = @()
        foreach ($c in $candidates) {
            if (-not (Get-Command $c[0] -ErrorAction SilentlyContinue)) { continue }
            $pre = @(); if ($c.Count -gt 1) { $pre = $c[1..($c.Count - 1)] }
            $prev = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                # The Microsoft Store "python" alias stubs fail here and are skipped.
                & $c[0] @pre -c 'import sys; sys.exit(0 if sys.version_info >= (3, 10) else 1)' *> $null
                $ok = ($LASTEXITCODE -eq 0)
            } finally { $ErrorActionPreference = $prev }
            if ($ok) { $pyExe = $c[0]; $pyPre = $pre; break }
        }
        if (-not $pyExe) {
            throw 'Python 3.10 or newer was not found. Install it from https://www.python.org/downloads/ (or: winget install Python.Python.3.12) and re-run.'
        }

        $have = Get-InstalledVersion
        if ($have -eq $GraphifyVersion -and (Test-Path -LiteralPath $Graphify)) {
            Write-Host "graphifyy $GraphifyVersion already installed"
        } else {
            if ($have) { Write-Host "found graphifyy $have, installing pinned $GraphifyVersion" }
            if (-not (Test-Path -LiteralPath $VenvPy)) {
                Write-Host "creating virtual environment in $VenvRel"
                Invoke-Native $pyExe ($pyPre + @('-m', 'venv', $Venv)) 'could not create a virtual environment.'
            }
            Write-Host "installing graphifyy==$GraphifyVersion"
            Invoke-Native $VenvPy @('-m', 'pip', 'install', '--disable-pip-version-check', "graphifyy==$GraphifyVersion") `
                "pip install of graphifyy==$GraphifyVersion failed (network or PyPI problem?)."
        }
    }

    # ------------------------------------------------------------ build
    Write-Host 'building the code graph (local, no LLM)...'
    Invoke-Native $Graphify @('update', '.') "'graphify update' failed."

    if (-not $NoObsidian) {
        Write-Host 'exporting the Obsidian vault...'
        Invoke-Native $Graphify @('export', 'obsidian', '--dir', $VaultRel) "'graphify export obsidian' failed."
    }

    if ($Hooks) {
        Write-Host 'installing local Claude Code integration...'
        Invoke-Native $Graphify @('claude', 'install') "'graphify claude install' failed."
        if (Test-Path -LiteralPath (Join-Path $RepoRoot '.git')) {
            Invoke-Native $Graphify @('hook', 'install') "'graphify hook install' failed."
            # The hook installer also adds a merge-driver line to the TRACKED .gitattributes. graph.json is
            # git-ignored here, so the line is useless and would only dirty the working tree; remove it.
            $attr = Join-Path $RepoRoot '.gitattributes'
            if (Test-Path -LiteralPath $attr) {
                $lines = @(Get-Content -LiteralPath $attr)
                if ($lines -contains 'graphify-out/graph.json merge=graphify') {
                    $kept = @($lines | Where-Object { $_ -ne 'graphify-out/graph.json merge=graphify' })
                    if ($kept.Count -gt 0) { Set-Content -LiteralPath $attr -Value $kept } else { Remove-Item -LiteralPath $attr }
                }
            }
        } else {
            Write-Host 'no .git folder here; skipping git hooks'
        }
    }

    # ------------------------------------------------------------ summary
    $counts = ''
    if (Test-Path -LiteralPath 'graphify-out\graph.json') {
        $prev = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $code = @'
import json
g = json.load(open("graphify-out/graph.json", encoding="utf-8"))
print("%d nodes, %d edges" % (len(g.get("nodes", [])), len(g.get("links", g.get("edges", [])))))
'@
            $r = $code | & $VenvPy - 2>$null
            if ($LASTEXITCODE -eq 0 -and $r) { $counts = ([string]$r).Trim() }
        } finally { $ErrorActionPreference = $prev }
    }

    Write-Host ''
    if ($counts) { Write-Host "Done. Graph: $counts." } else { Write-Host 'Done.' }
    Write-Host '  report : graphify-out\GRAPH_REPORT.md'
    Write-Host '  graph  : graphify-out\graph.html (open in a browser)'
    if (-not $NoObsidian) {
        Write-Host "  vault  : $VaultRel  (Obsidian: 'Open folder as vault', then pick this folder)"
    }
    Write-Host ''
    Write-Host 'Next steps:'
    Write-Host "  $VenvRel\Scripts\graphify.exe query `"how are mods validated?`""
    Write-Host "  $VenvRel\Scripts\graphify.exe explain `"ModCatalog`""
    Write-Host '  powershell -ExecutionPolicy Bypass -File tools\graphify\setup.ps1 -Refresh   # after code changes'
}
catch {
    Fail $_.Exception.Message
}
