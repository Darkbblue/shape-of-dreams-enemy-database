#!/usr/bin/env python3
"""Probe Unity asset files to locate monster prefab data.

Run with the Windows Python that has UnityPy installed:

    python.exe datamining/probe_unity.py [--input datamining/raw_game_data_unity]

For every loadable file this reports:
  * object type counts (GameObject / MonoBehaviour / MonoScript / ...),
  * whether serialized typetrees are present or were stripped in the
    player build (decides if TypeTreeGenerator is needed),
  * GameObject names matching Mon_/At_/Boss (prefab candidates),
  * MonoScript class names matching Mon_/At_/Dew/Monster (the m_Script
    back-references needed to identify Monster components).

catalog.bin (Addressables) is scanned as raw bytes for "Mon_" keys --
UnityPy does not parse Addressables catalogs.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
DEFAULT_INPUT = SCRIPT_DIR / "raw_game_data_unity"

NAME_RE = re.compile(r"^(Mon_|At_|Ai_|Se_|Boss|Sum_|Dew[A-Z]|Mon$)", re.ASCII)


def probe_file(path: Path):
    import UnityPy

    print(f"\n=== {path.name} ({path.stat().st_size / 1e6:.1f} MB) ===")
    try:
        env = UnityPy.load(str(path))
    except Exception as exc:
        print(f"  LOAD FAILED: {exc!r}")
        return

    type_counts = Counter()
    gameobject_hits = []
    monoscript_hits = []
    monobehaviour_total = 0
    monobehaviour_with_typetree = 0
    named_samples = Counter()

    try:
        objects = list(env.objects)
    except Exception as exc:
        print(f"  ENUMERATION FAILED: {exc!r}")
        return

    for obj in objects:
        type_counts[obj.type.name] += 1
        if obj.type.name == "MonoBehaviour":
            monobehaviour_total += 1
            try:
                if getattr(obj, "serialized_type", None) is not None and \
                        getattr(obj.serialized_type, "node", None) is not None:
                    monobehaviour_with_typetree += 1
            except Exception:
                pass
        elif obj.type.name in ("GameObject", "MonoScript"):
            try:
                data = obj.read()
                name = getattr(data, "m_Name", "") or ""
            except Exception:
                continue
            if not name:
                continue
            if obj.type.name == "MonoScript":
                if NAME_RE.match(name):
                    monoscript_hits.append(name)
            elif NAME_RE.match(name):
                gameobject_hits.append(name)
            else:
                head = re.split(r"[\s_]", name, 1)[0]
                named_samples[head] += 1

    print(f"  objects: {len(objects)}")
    for tname, count in type_counts.most_common(12):
        print(f"    {count:8d}  {tname}")
    print(f"  MonoBehaviour typetree: {monobehaviour_with_typetree}/{monobehaviour_total} "
          f"({'present' if monobehaviour_with_typetree else 'STRIPPED -> TypeTreeGenerator needed'})")
    print(f"  GameObject name prefixes (non-Mon, top 8): "
          f"{dict(named_samples.most_common(8))}")
    print(f"  Mon/At/Ai/Se/Boss-prefixed GameObjects: {len(gameobject_hits)}")
    for name in gameobject_hits[:25]:
        print(f"    GO  {name}")
    if len(gameobject_hits) > 25:
        print(f"    ... and {len(gameobject_hits) - 25} more")
    print(f"  matching MonoScripts: {len(monoscript_hits)}")
    for name in monoscript_hits[:25]:
        print(f"    MS  {name}")
    if len(monoscript_hits) > 25:
        print(f"    ... and {len(monoscript_hits) - 25} more")


def scan_catalog(path: Path):
    raw = path.read_bytes()
    hits = sorted(set(re.findall(rb"[A-Za-z][A-Za-z0-9_]{3,60}(?:Mon_|_Mon_)[A-Za-z0-9_]+", raw)))
    mon_hits = sorted(set(re.findall(rb"Mon_[A-Za-z0-9_]+", raw)))
    print(f"\n=== {path.name} (raw scan) ===")
    print(f"  bytes containing 'Mon_': keys like {len(mon_hits)}")
    for h in mon_hits[:30]:
        print(f"    {h.decode('ascii', 'replace')}")
    if len(mon_hits) > 30:
        print(f"    ... and {len(mon_hits) - 30} more")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--input", type=Path, default=DEFAULT_INPUT)
    args = parser.parse_args()

    if not args.input.is_dir():
        print(f"input directory not found: {args.input}", file=sys.stderr)
        return 2

    for path in sorted(args.input.iterdir()):
        if not path.is_file():
            continue
        if path.name == "catalog.bin":
            scan_catalog(path)
        elif path.suffix in (".assets", ".bundle"):
            probe_file(path)
    return 0


if __name__ == "__main__":
    sys.exit(main())
