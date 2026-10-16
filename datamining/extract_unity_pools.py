#!/usr/bin/env python3
"""Extract monster ability pools + difficulty settings from Unity assets.

Run with the Windows Python that has UnityPy installed:

    python.exe datamining/extract_unity_pools.py \
        [--input datamining/raw_game_data_unity] \
        [--output datamining/processed_data/unity_dump]

Inputs: the content bundle (defaultlocalgroup_assets_all_*.bundle) plus
the monoscripts bundle for m_Script class-name resolution.  The content
bundle carries embedded typetrees for every MonoBehaviour (verified by
probe_unity.py), so no TypeTreeGenerator is required.

Outputs (one JSON each, in --output):
    _summary.json        counts + monster/difficulty name lists
    monsters.json        full typetree per Mon_* prefab root, keyed by
                         the GameObject name (includes ``type`` enum,
                         ``abilityPools`` with per-entry chance / HP
                         window / trigger AssetRef)
    difficulty.json      every DewDifficultySettings asset, full fields
    ability_triggers.json  full typetree per At_* / Ai_* / Se_* prefab
                         root component (prefab defaults: configs[],
                         startAnim, cooldownTime, ... -- the values the
                         RawData override patches modify)
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import time
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
DEFAULT_INPUT = SCRIPT_DIR / "raw_game_data_unity"
DEFAULT_OUTPUT = SCRIPT_DIR / "processed_data" / "unity_dump"



def gameobject_name(env_index, owner_file_key: str, tree: dict) -> str | None:
    """Resolve a MonoBehaviour's GameObject name from the raw typetree."""
    try:
        ptr = tree.get("m_GameObject")
        if not isinstance(ptr, dict):
            return None
        resolved = resolve_ptr(env_index, owner_file_key, ptr)
        if resolved is None:
            return None
        obj = env_index["objs"].get(resolved[0], {}).get(resolved[1])
        if obj is None:
            return None
        return obj.read().m_Name
    except Exception:
        return None


def script_class_name(env_index, owner_file_key: str, tree: dict) -> str | None:
    """Resolve m_Script PPtr to a MonoScript class name."""
    try:
        ptr = tree.get("m_Script")
        if not isinstance(ptr, dict):
            return None
        resolved = resolve_ptr(env_index, owner_file_key, ptr)
        if resolved is None:
            return None
        obj = env_index["scripts"].get(resolved[0], {}).get(resolved[1])
        if obj is None:
            return None
        return obj.read().m_ClassName
    except Exception:
        return None


def simplify(value, depth=0):
    """Make a typetree value JSON-safe, keeping scalars/strings verbatim."""
    if depth > 12:
        return "<deep>"
    if value is None or isinstance(value, (bool, int, float, str)):
        return value
    if isinstance(value, bytes):
        return f"<{len(value)} bytes>"
    if isinstance(value, dict):
        # PPtr-like: m_FileID/m_PathID
        if "m_PathID" in value and "m_FileID" in value:
            return {"file_id": value.get("m_FileID"),
                    "path_id": str(value.get("m_PathID"))}
        return {k: simplify(v, depth + 1) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [simplify(v, depth + 1) for v in value]
    # UnityPy typetree objects: try common containers
    for attr in ("values", "children"):
        child = getattr(value, attr, None)
        if child is not None:
            return simplify(child, depth + 1)
    for attr in ("m_Name", "name"):
        name = getattr(value, attr, None)
        if isinstance(name, str):
            extra = {a: simplify(getattr(value, a), depth + 1)
                     for a in ("resourceId", "_resourceId")
                     if hasattr(value, a)}
            return {"m_Name": name, **extra} if extra else name
    return str(value)


def iter_serialized_files(env):
    """Yield (file_key, SerializedFile) for every file in the env."""
    stack = list(env.files.values())
    while stack:
        f = stack.pop()
        name = getattr(f, "name", "") or ""
        if hasattr(f, "objects"):  # SerializedFile
            yield name, f
        nested = getattr(f, "files", None)
        if nested:
            stack.extend(nested.values())


def build_env_index(env) -> dict:
    """Index objects/scripts per SerializedFile for PPtr resolution.

    PPtr semantics: m_FileID == 0 -> same SerializedFile as the owner;
    m_FileID > 0 -> externals[m_FileID - 1] of that file.
    """
    objs_by_file: dict[str, dict] = {}
    scripts_by_file: dict[str, dict] = {}
    externals_by_file: dict[str, list] = {}
    for name, sf in iter_serialized_files(env):
        key = name.rsplit("/", 1)[-1]
        objs_by_file.setdefault(key, {})
        externals_by_file.setdefault(key, [])
        try:
            externals_by_file[key] = [
                str(e.path).rsplit("/", 1)[-1] for e in sf.externals
            ]
        except Exception:
            pass
        try:
            sf_objects = sf.objects or {}
        except Exception:
            sf_objects = {}
        if not hasattr(sf_objects, "items"):
            continue
        for path_id, obj in sf_objects.items():
            objs_by_file[key][path_id] = obj
            if obj.type.name == "MonoScript":
                scripts_by_file.setdefault(key, {})[path_id] = obj
    return {
        "objs": objs_by_file,
        "scripts": scripts_by_file,
        "externals": externals_by_file,
    }


def resolve_ptr(index, owner_file_key: str, ptr) -> tuple | None:
    """Resolve a PPtr (dict or object) to (target_file_key, path_id)."""
    file_id = getattr(ptr, "file_id", None)
    path_id = getattr(ptr, "path_id", None)
    if file_id is None and isinstance(ptr, dict):
        file_id = ptr.get("m_FileID")
        path_id = ptr.get("m_PathID")
    path_id = int(path_id)
    if file_id == 0:
        return owner_file_key, path_id
    exts = index["externals"].get(owner_file_key, [])
    if 0 < file_id <= len(exts):
        target = exts[file_id - 1]
        if target in index["objs"]:
            return target, path_id
    return None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--input", type=Path, default=DEFAULT_INPUT)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    args = parser.parse_args()

    import UnityPy  # deferred so --help works without UnityPy

    bundle_paths = sorted(args.input.glob("*.bundle"))
    asset_paths = sorted(args.input.glob("*.assets"))
    if not bundle_paths:
        print("no .bundle files found in input", file=sys.stderr)
        return 2

    load_paths = [str(p) for p in bundle_paths + asset_paths]
    print(f"loading {len(load_paths)} files ...", flush=True)
    t0 = time.time()
    env = UnityPy.load(*load_paths)
    print(f"loaded in {time.time() - t0:.1f}s", flush=True)

    index = build_env_index(env)

    monsters = {}
    difficulties = {}
    triggers = {}
    stats = {
        "monobehaviours": 0,
        "monsters": 0,
        "difficulty_assets": 0,
        "trigger_prefabs": 0,
        "other": 0,
    }

    t0 = time.time()
    for file_key, objs in index["objs"].items():
        for path_id, obj in objs.items():
            if obj.type.name != "MonoBehaviour":
                continue
            stats["monobehaviours"] += 1
            try:
                tree = obj.read_typetree()
            except Exception:
                stats["other"] += 1
                continue
            if not isinstance(tree, dict):
                stats["other"] += 1
                continue

            if "enemyAbilityHasteFlat" in tree:
                name = tree.get("m_Name") or f"difficulty_{path_id}"
                doc = simplify(tree)
                doc["unity_path_id"] = str(path_id)
                doc["class_name"] = script_class_name(index, file_key, tree)
                difficulties[name] = doc
                stats["difficulty_assets"] += 1
                continue

            if "cooldownFloorRatioByAbilityHaste" in tree:
                name = tree.get("m_Name") or f"ges_{path_id}"
                doc = simplify(tree)
                doc["unity_path_id"] = str(path_id)
                doc["class_name"] = script_class_name(index, file_key, tree)
                difficulties[f"_ges:{name}"] = doc
                stats["ges_assets"] = stats.get("ges_assets", 0) + 1
                continue

            if "abilityPools" in tree:
                go_name = gameobject_name(index, file_key, tree)
                if not go_name or not go_name.startswith("Mon_"):
                    stats["other"] += 1
                    continue
                doc = simplify(tree)
                doc["unity_path_id"] = str(path_id)
                doc["class_name"] = script_class_name(index, file_key, tree)
                doc["gameobject_name"] = go_name
                monsters[go_name] = doc
                stats["monsters"] += 1
                if stats["monsters"] % 10 == 0:
                    print(f"  monsters: {stats['monsters']} "
                          f"({time.time() - t0:.0f}s)", flush=True)
                continue

            go_name = gameobject_name(index, file_key, tree)
            if go_name and re.match(r"^(At|Ai|Se|Sum)_", go_name) and "configs" in tree:
                doc = simplify(tree)
                doc["unity_path_id"] = str(path_id)
                doc["class_name"] = script_class_name(index, file_key, tree)
                doc["gameobject_name"] = go_name
                # a prefab root may hold several components; keep them all
                triggers.setdefault(go_name, []).append(doc)
                stats["trigger_prefabs"] += 1
                continue

            stats["other"] += 1

    out: Path = args.output
    out.mkdir(parents=True, exist_ok=True)

    (out / "monsters.json").write_text(
        json.dumps(monsters, ensure_ascii=False, indent=2), encoding="utf-8")
    (out / "difficulty.json").write_text(
        json.dumps(difficulties, ensure_ascii=False, indent=2), encoding="utf-8")
    (out / "ability_triggers.json").write_text(
        json.dumps(triggers, ensure_ascii=False, indent=2), encoding="utf-8")
    (out / "_summary.json").write_text(
        json.dumps({
            "elapsed_seconds": round(time.time() - t0, 1),
            "stats": stats,
            "monster_names": sorted(monsters),
            "difficulty_names": sorted(difficulties),
        }, ensure_ascii=False, indent=2), encoding="utf-8")

    print(f"monsters: {stats['monsters']}  difficulty: {stats['difficulty_assets']}  "
          f"trigger prefab roots: {len(triggers)}")
    print(f"output: {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
