#!/usr/bin/env python3
"""Stage 1 of the datamining pipeline: raw extraction (PLAN.md section 2).

Path A verdict (from ref/tree.txt): all game resources -- including
monsters -- ship as plain JSON under ``<Game>/RawData``; monster data
lives in ``!ModResources/overrides/Monsters/<monster_dir>/`` where each
directory holds one or more ``Mon_*.json`` (the monster) plus related
``At_*`` (AbilityTrigger), ``Ai_*`` (AbilityInstance) and ``Se_*``
(StatusEffect) resources.

This script therefore only needs to:

  1. walk the input tree,
  2. copy every file verbatim into the output mirror (reviewable raw),
  3. parse every JSON file, recording (not masking) any errors,
  4. emit ``_index.json`` mapping internal resource names to files.

No assumptions are made about the JSON schema of individual resources;
schema-aware transformation and per-difficulty precomputation belong to
``build_db.py`` (stage 2).

Usage::

    python extract_raw.py                       # defaults below
    python extract_raw.py --input <RawData copy> --output <dir>

Defaults (resolved relative to this script's directory):

    --input   ./raw_game_data
    --output  ./processed_data/raw
"""

from __future__ import annotations

import argparse
import json
import shutil
import sys
import time
from collections import Counter
from pathlib import Path, PurePosixPath

SCRIPT_DIR = Path(__file__).resolve().parent
DEFAULT_INPUT = SCRIPT_DIR / "raw_game_data"
DEFAULT_OUTPUT = SCRIPT_DIR / "processed_data" / "raw"

# Top-level resource categories observed in the overrides tree
# (ref/tree.txt). Used for grouping/statistics only; unknown categories
# are kept as-is rather than rejected.
KNOWN_CATEGORIES = {
    "Curses",
    "Gems",
    "Heroes",
    "LucidDreams",
    "Monsters",
    "RoomMods",
    "Shrines",
    "Skills",
    "Stars",
    "Treasures",
}

# Resource filename prefixes observed in the tree, with their presumed
# meaning (presumed because the mapping is inferred from naming; the
# authoritative definition lives in the game's C# assemblies).
KNOWN_PREFIXES = {
    "Mon": "monster",
    "At": "ability_trigger",
    "Ai": "ability_instance",
    "Se": "status_effect",
    "Sum": "summon",
    "GameMod": "game_mod",
}


def normalize_relpath(relpath: Path) -> PurePosixPath:
    """Return a posix-style relative path for stable index keys."""
    return PurePosixPath(*relpath.parts)


def strip_resource_root(relpath: Path) -> Path:
    """Drop a leading ``!ModResources/overrides`` (or similar) prefix.

    The exact copy of the input kept in the output mirror stays
    verbatim; this normalization is only applied to index metadata so
    that the index looks the same regardless of how much of the
    RawData hierarchy the caller copied in.
    """
    parts = list(relpath.parts)
    while parts and parts[0] in ("!ModResources", "overrides"):
        parts.pop(0)
    return Path(*parts) if parts else relpath


def classify(rel: Path) -> dict:
    """Classify a resource file by its position in the overrides tree."""
    parts = rel.parts
    category = next((p for p in parts[:-1] if p in KNOWN_CATEGORIES), None)
    if category is None:
        category = parts[0] if len(parts) > 1 else "(root)"
    group = ""
    if category in parts:
        idx = parts.index(category)
        if len(parts) - idx > 2:  # <category>/<group>/<file>
            group = parts[idx + 1]
    head, sep, _ = rel.name.partition("_")
    prefix = head if sep and head.isalpha() else "(none)"
    return {
        "category": category,
        "group": group,
        "prefix": prefix,
        "prefix_meaning": KNOWN_PREFIXES.get(prefix),
    }


def parse_json_file(path: Path):
    """Parse one JSON file, tolerating a UTF-8 BOM.

    Returns ``(data, None)`` on success or ``(None, error_message)``.
    """
    try:
        text = path.read_text(encoding="utf-8-sig")
    except UnicodeDecodeError as exc:
        return None, f"decode error: {exc}"
    try:
        return json.loads(text), None
    except json.JSONDecodeError as exc:
        return None, f"json error: {exc}"


def is_boss_candidate(rel: Path, info: dict) -> bool:
    """Provisional boss filter by resource name.

    The authoritative filter (``Monster.type == Boss``, per the official
    API docs) is applied in build_db.py once the real JSON schema is
    confirmed; this heuristic only feeds the summary report.
    """
    if info["category"] != "Monsters":
        return False
    haystack = " ".join(rel.parts).lower()
    return "boss" in haystack


def extract(input_dir: Path, output_dir: Path) -> int:
    if not input_dir.is_dir():
        print(f"error: input directory not found: {input_dir}", file=sys.stderr)
        return 2

    entries = []
    errors = []
    copied = 0
    json_ok = 0

    all_files = sorted(p for p in input_dir.rglob("*") if p.is_file())
    for src in all_files:
        rel = src.relative_to(input_dir)
        dst = output_dir / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src, dst)
        copied += 1

        if src.suffix.lower() != ".json":
            continue
        data, err = parse_json_file(src)
        info = classify(strip_resource_root(rel))
        entry = {
            "name": src.stem,
            "path": str(normalize_relpath(rel)),
            **info,
        }
        if err is not None:
            entry["parse_error"] = err
            errors.append({"path": str(normalize_relpath(rel)), "error": err})
        else:
            entry["top_level_type"] = type(data).__name__
            if isinstance(data, dict):
                entry["top_level_keys"] = sorted(data.keys())
            json_ok += 1
        entries.append(entry)

    by_name = {}
    for entry in entries:
        by_name.setdefault(entry["name"], []).append(entry["path"])

    monsters = {}
    for entry in entries:
        if entry["category"] != "Monsters" or not entry["group"]:
            continue
        slot = monsters.setdefault(entry["group"], {"mon_json": [], "support": []})
        if entry["prefix"] == "Mon":
            slot["mon_json"].append(entry["name"])
        else:
            slot["support"].append(entry["name"])

    boss_candidates = sorted(
        {e["group"] or e["name"] for e in entries if is_boss_candidate(Path(e["path"]), e)}
    )

    index = {
        "generated_at": time.strftime("%Y-%m-%d %H:%M:%S"),
        "source_root": str(input_dir),
        "totals": {
            "files_copied": copied,
            "json_parsed_ok": json_ok,
            "json_errors": len(errors),
            "categories": dict(sorted(Counter(e["category"] for e in entries).items())),
            "prefixes": dict(sorted(Counter(e["prefix"] for e in entries).items())),
        },
        "monster_dirs": {
            "count": len(monsters),
            "listing": {k: monsters[k] for k in sorted(monsters)},
        },
        "boss_candidates_provisional": boss_candidates,
        "names_to_paths": by_name,
        "files": entries,
        "errors": errors,
    }

    output_dir.mkdir(parents=True, exist_ok=True)
    index_path = output_dir / "_index.json"
    index_path.write_text(
        json.dumps(index, ensure_ascii=False, indent=2), encoding="utf-8"
    )

    print(f"input : {input_dir}")
    print(f"output: {output_dir}")
    print(f"files copied verbatim : {copied}")
    print(f"json parsed           : {json_ok} ok, {len(errors)} failed")
    for err in errors:
        print(f"  ! {err['path']}: {err['error']}")
    print(f"categories            : {index['totals']['categories']}")
    print(f"monster directories   : {len(monsters)}")
    print(f"boss candidates (provisional, name-based): {len(boss_candidates)}")
    for name in boss_candidates:
        print(f"  - {name}")
    print(f"index written         : {index_path}")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--input", type=Path, default=DEFAULT_INPUT,
                        help="directory holding the copied RawData files")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT,
                        help="output mirror + _index.json destination")
    args = parser.parse_args()
    return extract(args.input, args.output)


if __name__ == "__main__":
    sys.exit(main())
