#!/usr/bin/env python3
"""Repo/package sanity checks for CI — no Unity required.

Checks:
  1. Packages/<pkg>/package.json parses and carries name + version + unity fields,
     and the directory name matches the package name.
  2. Every .asmdef under Packages/ and Assets/ parses and declares "name".
  3. .meta coverage: every importable file/dir under Assets/ and embedded
     packages (Packages/<dir containing package.json>) has a sibling .meta.
     Skipped like Unity's importer: dot-names, names ending in '~' (Samples~,
     Documentation~), VCS/IDE/cache dirs, and .meta files themselves.
  4. Runtime/Core stays engine-free: no UnityEngine/UnityEditor references
     (the noEngineReferences guarantee — it also compiles in tests/dotnet).
  5. README.md and SPEC.md mention the UPM git URL (?path=/Packages/<pkg>).

Exit 0 when clean; prints every violation and exits 1 otherwise.
Usage: python tools/check_package.py [--repo ROOT]
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys

PKG_NAME = "com.splatterfacegames.phone-mass-controllers"
PKG_DIR = "Packages/" + PKG_NAME
UPM_URL_FRAGMENT = "?path=/" + PKG_DIR

# Unity never imports these names -> no .meta expected.
SKIP_DIRS = {"node_modules", "obj", "bin", ".git", ".vs", ".idea", ".vscode",
             "Library", "Temp", "Logs", "UserSettings", "MemoryCaptures"}
ENGINE_RE = re.compile(r"\b(UnityEngine|UnityEditor)\b")


def ignored_by_unity(name: str) -> bool:
    return name.startswith(".") or name.endswith("~") or name.endswith(".tmp")


def collect_unmetaed(root: str, problems: list[str]) -> None:
    if not os.path.isdir(root):
        return
    for dirpath, dirs, files in os.walk(root):
        dirs[:] = [d for d in dirs if not ignored_by_unity(d) and d not in SKIP_DIRS]
        rel_dir = os.path.relpath(dirpath, root)
        for d in dirs:
            meta = os.path.join(dirpath, d + ".meta")
            if not os.path.isfile(meta):
                problems.append("missing dir .meta: " + os.path.join(rel_dir, d))
        for f in files:
            if f.endswith(".meta") or ignored_by_unity(f):
                continue
            meta = os.path.join(dirpath, f + ".meta")
            if not os.path.isfile(meta):
                problems.append("missing .meta: " + os.path.join(rel_dir, f))


def check_package_json(repo: str, problems: list[str]) -> None:
    path = os.path.join(repo, PKG_DIR, "package.json")
    try:
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
    except OSError:
        problems.append(f"{PKG_DIR}/package.json: missing")
        return
    except json.JSONDecodeError as e:
        problems.append(f"{PKG_DIR}/package.json: invalid JSON ({e})")
        return
    if data.get("name") != PKG_NAME:
        problems.append(f"package.json name {data.get('name')!r} != {PKG_NAME!r}")
    if not re.fullmatch(r"\d+\.\d+\.\d+(-[\w.]+)?", str(data.get("version", ""))):
        problems.append(f"package.json version {data.get('version')!r} is not semver")
    if not data.get("unity"):
        problems.append("package.json missing \"unity\" (minimum editor version)")


def check_asmdefs(repo: str, problems: list[str]) -> int:
    count = 0
    for base in ("Packages", "Assets"):
        broot = os.path.join(repo, base)
        for dirpath, dirs, files in os.walk(broot):
            dirs[:] = [d for d in dirs if not ignored_by_unity(d) and d not in SKIP_DIRS]
            for f in files:
                if not f.endswith(".asmdef"):
                    continue
                count += 1
                p = os.path.join(dirpath, f)
                try:
                    with open(p, "r", encoding="utf-8") as fh:
                        data = json.load(fh)
                    if not data.get("name"):
                        problems.append(f"{p}: asmdef missing \"name\"")
                except (OSError, json.JSONDecodeError) as e:
                    problems.append(f"{p}: asmdef invalid ({e})")
    return count


def check_core_is_engine_free(repo: str, problems: list[str]) -> None:
    core = os.path.join(repo, PKG_DIR, "Runtime", "Core")
    for dirpath, _dirs, files in os.walk(core):
        for f in files:
            if not f.endswith(".cs"):
                continue
            p = os.path.join(dirpath, f)
            try:
                with open(p, "r", encoding="utf-8", errors="replace") as fh:
                    for i, line in enumerate(fh, 1):
                        if ENGINE_RE.search(line):
                            problems.append(
                                f"engine reference in core: {os.path.relpath(p, repo)}:{i}: {line.strip()}")
                            break
            except OSError:
                pass


def check_upm_url(repo: str, problems: list[str]) -> None:
    for doc in ("README.md", "SPEC.md"):
        p = os.path.join(repo, doc)
        try:
            with open(p, "r", encoding="utf-8", errors="replace") as f:
                if UPM_URL_FRAGMENT not in f.read():
                    problems.append(f"{doc}: UPM git URL ({UPM_URL_FRAGMENT}) not mentioned")
        except OSError:
            problems.append(f"{doc}: missing")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--repo", default=".", help="repo root (default: cwd)")
    args = ap.parse_args()
    repo = os.path.abspath(args.repo)

    problems: list[str] = []
    check_package_json(repo, problems)
    n_asmdefs = check_asmdefs(repo, problems)
    if n_asmdefs == 0:
        problems.append("no .asmdef files found under Packages/ or Assets/")
    check_core_is_engine_free(repo, problems)
    check_upm_url(repo, problems)

    # .meta coverage: Assets/ (incl. Assets.meta itself) plus every embedded
    # package (Packages/<dir with package.json>).
    assets = os.path.join(repo, "Assets")
    if os.path.isdir(assets) and not os.path.isfile(assets + ".meta"):
        problems.append("missing dir .meta: Assets")
    collect_unmetaed(assets, problems)
    pkg_root = os.path.join(repo, "Packages")
    if os.path.isdir(pkg_root):
        for name in sorted(os.listdir(pkg_root)):
            pdir = os.path.join(pkg_root, name)
            if os.path.isdir(pdir) and os.path.isfile(os.path.join(pdir, "package.json")):
                collect_unmetaed(pdir, problems)

    if problems:
        print(f"check_package: {len(problems)} problem(s):", file=sys.stderr)
        for p in problems:
            print("  " + p, file=sys.stderr)
        return 1
    print(f"check_package: OK ({n_asmdefs} asmdefs, package.json v{json.load(open(os.path.join(repo, PKG_DIR, 'package.json')))['version']})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
