#!/usr/bin/env python3
"""Build phone-mass-controllers.unitypackage without Unity.

A .unitypackage is a gzipped tar where every asset is a directory named by its
GUID containing:

    <guid>/pathname    destination path in the importing project (line 1; "00" on line 2)
    <guid>/asset       file bytes (absent for folders)
    <guid>/asset.meta  importer meta text

GUIDs come from each file's .meta (`guid:` line). Files without a .meta get a
deterministic GUID (uuid5 of the repo-relative source path) plus a synthesized
meta, so the output is byte-stable across machines.

Mapping (documented in README — the unitypackage layout is Assets/-rooted
because Unity packages can't install into Packages/):

    Packages/<pkg>/Runtime/**      -> Assets/PhoneMassControllers/Runtime/**
    Packages/<pkg>/Editor/**       -> Assets/PhoneMassControllers/Editor/**
    Packages/<pkg>/Web/**          -> Assets/PhoneMassControllers/Web/**
    Packages/<pkg>/package.json    -> Assets/PhoneMassControllers/package.json
    Packages/<pkg>/Samples~/**     -> Assets/PhoneMassControllers/Samples/**
    LICENSE / LICENSE.md           -> Assets/PhoneMassControllers/LICENSE.md
    CHANGELOG.md                   -> Assets/PhoneMassControllers/CHANGELOG.md

Usage: python tools/make_unitypackage.py [--repo ROOT] [--out PATH] [--validate-unity]
Env:   UNITY_EXE — Unity editor binary; with --validate-unity the built package
       is imported into a scratch project and the editor log checked for errors.
"""
from __future__ import annotations

import argparse
import gzip
import io
import os
import re
import subprocess
import sys
import tarfile
import tempfile
import uuid

PKG_DIR = "Packages/com.splatterfacegames.phone-mass-controllers"
PKG_NAME = "phone-mass-controllers"
DEST_ROOT = "Assets/PhoneMassControllers"
# Namespace seed for synthesized GUIDs — stable per repo-relative path.
UUID5_SEED = "https://github.com/splatterfacegames/unity-phone-mass-controllers/"

# Repo-relative source prefix -> unitypackage destination prefix. Order matters:
# longest/most specific prefixes first.
MAPPINGS = [
    (PKG_DIR + "/Runtime/", DEST_ROOT + "/Runtime/"),
    (PKG_DIR + "/Editor/", DEST_ROOT + "/Editor/"),
    (PKG_DIR + "/Web/", DEST_ROOT + "/Web/"),
    (PKG_DIR + "/Samples~/", DEST_ROOT + "/Samples/"),
]

ROOT_FILES = [
    (PKG_DIR + "/package.json", DEST_ROOT + "/package.json"),
    ("LICENSE", DEST_ROOT + "/LICENSE.md"),
    ("LICENSE.md", DEST_ROOT + "/LICENSE.md"),
    ("CHANGELOG.md", DEST_ROOT + "/CHANGELOG.md"),
]

GUID_RE = re.compile(rb"^guid:\s*([0-9a-fA-F]{32})\s*$", re.M)

FOLDER_META = """fileFormatVersion: 2
guid: {guid}
folderAsset: yes
DefaultImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

SCRIPT_META = """fileFormatVersion: 2
guid: {guid}
MonoImporter:
  externalObjects: {{}}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {{instanceID: 0}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

ASMDEF_META = """fileFormatVersion: 2
guid: {guid}
AssemblyDefinitionImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

DEFAULT_META = """fileFormatVersion: 2
guid: {guid}
DefaultImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def to_repo_rel(root: str, path: str) -> str:
    return os.path.relpath(path, root).replace(os.sep, "/")


def map_dest(repo_rel: str) -> str | None:
    """Repo-relative file path -> unitypackage pathname, or None to exclude."""
    for src_prefix, dst_prefix in MAPPINGS:
        if repo_rel.startswith(src_prefix):
            return dst_prefix + repo_rel[len(src_prefix):]
    for src, dst in ROOT_FILES:
        if repo_rel == src:
            return dst
    return None


def meta_guid(meta_path: str) -> str | None:
    try:
        with open(meta_path, "rb") as f:
            m = GUID_RE.search(f.read())
            return m.group(1).decode("ascii").lower() if m else None
    except OSError:
        return None


def synth_guid(key: str) -> str:
    return uuid.uuid5(uuid.NAMESPACE_URL, UUID5_SEED + key).hex


def synth_meta(dest_path: str, guid: str, is_dir: bool) -> bytes:
    if is_dir:
        return FOLDER_META.format(guid=guid).encode("utf-8")
    if dest_path.endswith(".cs"):
        return SCRIPT_META.format(guid=guid).encode("utf-8")
    if dest_path.endswith(".asmdef"):
        return ASMDEF_META.format(guid=guid).encode("utf-8")
    return DEFAULT_META.format(guid=guid).encode("utf-8")


def add_bytes(tf: tarfile.TarFile, name: str, data: bytes) -> None:
    ti = tarfile.TarInfo(name)
    ti.size = len(data)
    ti.mtime = 0
    ti.mode = 0o644
    tf.addfile(ti, io.BytesIO(data))


def build(repo: str, out_path: str) -> tuple[int, int]:
    """Write the package. Returns (asset_count, synthesized_meta_count)."""
    # Collect (repo_rel, dest_rel, src_abs) for every mapped file.
    files: list[tuple[str, str, str]] = []
    srcs = [m[0] for m in MAPPINGS] + [s for s, _ in ROOT_FILES]
    for src in srcs:
        src_abs = os.path.join(repo, src)
        if src.endswith("/"):
            for dirpath, _dirs, names in os.walk(src_abs):
                for name in names:
                    if name.endswith(".meta"):
                        continue  # metas are read, not shipped as assets
                    if name.startswith("."):
                        continue
                    abs_path = os.path.join(dirpath, name)
                    rel = to_repo_rel(repo, abs_path)
                    dest = map_dest(rel)
                    if dest:
                        files.append((rel, dest, abs_path))
        elif os.path.isfile(src_abs):
            dest = map_dest(src)
            if dest:
                files.append((src, dest, src_abs))
    files.sort(key=lambda f: f[1])
    if not files:
        raise SystemExit("make_unitypackage: no source files mapped — wrong --repo?")

    # Every directory in the dest tree needs a folder entry (folderAsset meta).
    dirs: set[str] = set()
    for _rel, dest, _abs in files:
        d = os.path.dirname(dest)
        while d:
            dirs.add(d)
            d = os.path.dirname(d)

    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
    synthesized = 0
    with open(out_path, "wb") as raw:
        # mtime=0 + sorted inputs -> deterministic bytes for identical trees.
        with gzip.GzipFile(fileobj=raw, mode="wb", mtime=0) as gz:
            with tarfile.open(fileobj=gz, mode="w", format=tarfile.GNU_FORMAT) as tf:
                for d in sorted(dirs):
                    guid = synth_guid("dir:" + d)
                    add_bytes(tf, guid + "/pathname", (d + "\n00\n").encode("utf-8"))
                    add_bytes(tf, guid + "/asset.meta", synth_meta(d, guid, True))
                for rel, dest, abs_path in files:
                    guid = meta_guid(abs_path + ".meta")
                    if guid is None:
                        guid = synth_guid(rel)
                        meta = synth_meta(dest, guid, False)
                        synthesized += 1
                    else:
                        with open(abs_path + ".meta", "rb") as f:
                            meta = f.read()
                    add_bytes(tf, guid + "/pathname", (dest + "\n00\n").encode("utf-8"))
                    add_bytes(tf, guid + "/asset.meta", meta)
                    with open(abs_path, "rb") as f:
                        add_bytes(tf, guid + "/asset", f.read())
    return len(files), synthesized


def verify(out_path: str) -> tuple[int, int]:
    """Reopen the tarball: every <guid>/ must have pathname + asset.meta (+asset for files)."""
    entries: dict[str, set[str]] = {}
    total = 0
    with tarfile.open(out_path, "r:gz") as tf:
        for m in tf:
            parts = m.name.strip("/").split("/")
            if len(parts) == 2:
                entries.setdefault(parts[0], set()).add(parts[1])
            total += 1
    bad = []
    for guid, kinds in entries.items():
        if not re.fullmatch(r"[0-9a-f]{32}", guid):
            bad.append(f"{guid}: not a GUID dir")
        if "pathname" not in kinds or "asset.meta" not in kinds:
            bad.append(f"{guid}: missing pathname/asset.meta")
    if bad:
        raise SystemExit("make_unitypackage: malformed tar entries:\n  " + "\n  ".join(bad[:20]))
    size = os.path.getsize(out_path)
    if size < 1024:
        raise SystemExit(f"make_unitypackage: suspiciously small output ({size} bytes)")
    return len(entries), total


def validate_unity(out_path: str) -> None:
    """Optional: import into a scratch project with the real editor (needs UNITY_EXE)."""
    unity = os.environ.get("UNITY_EXE")
    if not unity:
        print("make_unitypackage: UNITY_EXE not set; skipping real-import validation")
        return
    with tempfile.TemporaryDirectory(prefix="pmc-pkgcheck-") as tmp:
        log = os.path.join(tmp, "unity.log")
        cmd = [unity, "-batchmode", "-nographics", "-quit", "-createProject", tmp,
               "-importPackage", os.path.abspath(out_path), "-logFile", log]
        print("make_unitypackage: validating with Unity:", " ".join(cmd))
        r = subprocess.run(cmd, timeout=900)
        text = ""
        try:
            with open(log, "r", encoding="utf-8", errors="replace") as f:
                text = f.read()
        except OSError:
            pass
        errors = [l for l in text.splitlines() if "error CS" in l or "Fatal!" in l]
        if r.returncode != 0 or errors:
            for l in errors[:30]:
                print("  " + l)
            raise SystemExit(f"make_unitypackage: Unity import failed (exit {r.returncode})")
        print("make_unitypackage: Unity import clean")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--repo", default=".", help="repo root (default: cwd)")
    ap.add_argument("--out", default=None,
                    help=f"output path (default: dist/{PKG_NAME}.unitypackage)")
    ap.add_argument("--validate-unity", action="store_true",
                    help="import the built package in a scratch Unity project (needs UNITY_EXE)")
    args = ap.parse_args()
    repo = os.path.abspath(args.repo)
    out = args.out or os.path.join(repo, "dist", f"{PKG_NAME}.unitypackage")

    n_files, n_synth = build(repo, out)
    n_assets, n_members = verify(out)
    size = os.path.getsize(out)
    print(f"make_unitypackage: {out}")
    print(f"  {n_files} files -> {n_assets} tar asset entries ({n_members} members), "
          f"{size} bytes; {n_synth} synthesized .meta (missing in repo)")
    validate_unity(out)


if __name__ == "__main__":
    main()
