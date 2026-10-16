#!/usr/bin/env python3
"""Build the boss -> room-scene mapping by scanning room scenes in the content bundle.

Rooms are standalone Unity scenes with generic names (Room_DarkCave_Boss_0); the boss
identity lives in each scene's MonsterSpawnRule pools (RoomMonsters.defaultRule and
Section_Monsters.ruleOverride).  This script loads the same bundles as
extract_unity_pools.py, walks every room scene, resolves the Mon_* prefabs referenced
by each pool, and emits the mapping consumed by the mod (about/boss_scenes.json).

Run with the Windows Python that has UnityPy installed (same as extract_unity_pools):

    python.exe datamining/extract_room_boss_map.py \
        [--input datamining/raw_game_data_unity] \
        [--output datamining/processed_data/out/_boss_scenes.json]

Output JSON:
    boss_scenes:  {Mon_X: [Room_*, ...]}
    scene_bosses: {Room_*: [Mon_X, ...]}
    details:      per-scene rule evidence (rule asset name, pool entry chance/conditions)
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
DEFAULT_INPUT = SCRIPT_DIR / "raw_game_data_unity"
DEFAULT_OUTPUT = SCRIPT_DIR / "processed_data" / "out" / "_boss_scenes.json"

sys.path.insert(0, str(SCRIPT_DIR))

from extract_unity_pools import (  # noqa: E402
    build_env_index,
    gameobject_name,
    iter_serialized_files,
    resolve_ptr,
    script_class_name,
)


def augment_index_with_top_level(index, env) -> int:
    """Index aggregated env.objects (loose level files loaded via UnityPy.load) too.

    build_env_index only walks env.files (bundle CABs).  Loose SerializedFiles such as
    level123 surface through env.objects as a flat list; each object carries its own
    assets_file, which gives us the file key and externals for PPtr resolution.
    """
    objs = env.objects
    if hasattr(objs, "values") or not isinstance(objs, (list, tuple)):
        return 0
    added = 0
    for obj in objs:
        sf = getattr(obj, "assets_file", None)
        name = (getattr(sf, "name", "") or "").rsplit("/", 1)[-1]
        key = name or "toplevel"
        index["objs"].setdefault(key, {})[obj.path_id] = obj
        if obj.type.name == "MonoScript":
            index["scripts"].setdefault(key, {})[obj.path_id] = obj
        if key not in index["externals"]:
            try:
                index["externals"][key] = [
                    str(e.path).rsplit("/", 1)[-1] for e in (sf.externals or [])
                ]
            except Exception:
                index["externals"][key] = []
        added += 1
    return added

MONSTER_TYPE_BOSS = 3  # Monster.MonsterType.Boss


def resolve_tree(index, owner_file_key: str, ptr) -> tuple[str, dict] | None:
    """Resolve a PPtr to (file_key, typetree dict) of the target object."""
    resolved = resolve_ptr(index, owner_file_key, ptr)
    if resolved is None:
        return None
    target_file, path_id = resolved
    obj = index["objs"].get(target_file, {}).get(path_id)
    if obj is None:
        return None
    try:
        tree = obj.read_typetree()
    except Exception:
        return None
    if not isinstance(tree, dict):
        return None
    return target_file, tree


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--input", type=Path, default=DEFAULT_INPUT)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument(
        "--gamedata",
        type=Path,
        default=None,
        help="Path to ShapeOfDreams_Data (for level* scene files + globalgamemanagers). "
        "Optional: without it only bundle assets are scanned and no room scenes exist there.",
    )
    args = parser.parse_args()

    import UnityPy  # deferred so --help works without UnityPy

    bundle_paths = sorted(args.input.glob("*.bundle"))
    asset_paths = sorted(args.input.glob("*.assets"))
    if not bundle_paths:
        print("no .bundle files found in input", file=sys.stderr)
        return 2

    boss_scene_names: list[str] = []
    if args.gamedata is not None:
        ggm = args.gamedata / "globalgamemanagers"
        if not ggm.exists():
            print(f"globalgamemanagers not found under {args.gamedata}", file=sys.stderr)
            return 2
        print("reading build settings (scene table) ...", flush=True)
        env_ggm = UnityPy.load(str(ggm))
        ggm_objects = env_ggm.objects
        if hasattr(ggm_objects, "values"):
            ggm_objects = list(ggm_objects.values())
        scene_paths: list[str] = []
        for obj in ggm_objects:
            if obj.type.name != "BuildSettings":
                continue
            try:
                tree = obj.read_typetree()
            except Exception:
                continue
            # BuildSettings serializes the scene table as `scenes`: list of asset paths.
            for path in tree.get("scenes") or []:
                if isinstance(path, str) and path:
                    scene_paths.append(path)
        if not scene_paths:
            print("no BuildSettings scenes readable (typetree missing?)", file=sys.stderr)
            return 2
        # level{i} matches build scene order.
        boss_scene_names = [
            f"level{i}"
            for i, path in enumerate(scene_paths)
            if "/room" in path.lower() and "boss" in path.lower()
        ]
        print(f"build settings: {len(scene_paths)} scenes, {len(boss_scene_names)} boss room scenes selected")

    load_paths = [str(p) for p in bundle_paths + asset_paths]
    if args.gamedata is not None:
        # Scene MonoScripts (RoomMonsters etc.) are not in the monoscripts bundle —
        # built scenes keep them in globalgamemanagers.assets.
        ggm_assets = args.gamedata / "globalgamemanagers.assets"
        if ggm_assets.exists():
            load_paths.append(str(ggm_assets))
        for name in boss_scene_names:
            level = args.gamedata / name
            if level.exists():
                load_paths.append(str(level))
            else:
                print(f"warning: {name} missing on disk", file=sys.stderr)
    print(f"loading {len(load_paths)} files ...", flush=True)
    t0 = time.time()
    env = UnityPy.load(*load_paths)
    print(f"loaded in {time.time() - t0:.1f}s", flush=True)

    index = build_env_index(env)
    added = augment_index_with_top_level(index, env)
    if added:
        print(f"indexed {added} extra top-level objects (loose level files)", flush=True)

    # --- pass 1: index monsters, pools, rules, room components -------------------
    monster_names: dict[tuple[str, int], str] = {}  # (file, path_id) -> Mon_* name
    boss_names: set[str] = set()
    pools: dict[tuple[str, int], dict] = {}
    rules: dict[tuple[str, int], dict] = {}
    rooms_by_scene: dict[str, list[dict]] = {}  # scene file -> [RoomMonsters trees]
    sections_by_scene: dict[str, list[dict]] = {}  # scene file -> [Section_Monsters trees]
    scene_files: set[str] = set()

    t0 = time.time()
    class_counter: dict[str, int] = {}
    unresolved_scripts = 0
    for file_key, objs in index["objs"].items():
        for path_id, obj in objs.items():
            if obj.type.name != "MonoBehaviour":
                continue
            try:
                tree = obj.read_typetree()
            except Exception:
                continue
            if not isinstance(tree, dict):
                continue
            cls = script_class_name(index, file_key, tree)
            if cls is None:
                unresolved_scripts += 1
                continue
            class_counter[cls] = class_counter.get(cls, 0) + 1
            if cls.startswith("Mon_"):
                gname = gameobject_name(index, file_key, tree)
                name = gname if gname else cls
                monster_names[(file_key, path_id)] = name
                if tree.get("type") == MONSTER_TYPE_BOSS:
                    boss_names.add(name)
            elif cls == "MonsterPool":
                pools[(file_key, path_id)] = tree
            elif cls == "MonsterSpawnRule":
                rules[(file_key, path_id)] = tree
            elif cls == "RoomMonsters":
                rooms_by_scene.setdefault(file_key, []).append(tree)
                scene_files.add(file_key)
            elif cls == "Section_Monsters":
                sections_by_scene.setdefault(file_key, []).append(tree)
    top_classes = sorted(class_counter.items(), key=lambda kv: -kv[1])[:6]
    interesting = {
        k: v
        for k, v in class_counter.items()
        if any(w in k for w in ("Room", "Monster", "Zone", "Pool", "Spawn", "Section"))
    }
    print(f"script resolution: {sum(class_counter.values())} resolved, {unresolved_scripts} unresolved; top: {top_classes}", flush=True)
    print(f"interesting classes: {interesting}", flush=True)
    print(
        f"indexed in {time.time() - t0:.1f}s: {len(monster_names)} monster prefabs "
        f"({len(boss_names)} bosses), {len(pools)} pools, {len(rules)} rules, "
        f"{len(rooms_by_scene)} scenes with RoomMonsters, "
        f"{len(sections_by_scene)} scenes with Section_Monsters",
        flush=True,
    )

    def monster_from_ptr(owner_file: str, ptr) -> str | None:
        r = resolve_ptr(index, owner_file, ptr)
        if r is None:
            return None
        return monster_names.get(r)

    def asset_ref_name(ref) -> str | None:
        # AssetRef<T> serializes with a guid + typeName; typeName IS the resource name.
        if isinstance(ref, dict):
            tn = ref.get("typeName")
            if isinstance(tn, str):
                return tn
        return None

    def pool_from_ptr(owner_file: str, ptr) -> dict | None:
        r = resolve_ptr(index, owner_file, ptr)
        if r is None:
            return None
        return pools.get(r)

    def rule_from_ptr(owner_file: str, ptr) -> dict | None:
        r = resolve_ptr(index, owner_file, ptr)
        if r is None:
            return None
        return rules.get(r)

    def rule_pool_bosses(owner_file: str, rule_tree: dict) -> list[dict]:
        out: list[dict] = []
        pool = pool_from_ptr(owner_file, rule_tree.get("pool") or {})
        if not pool:
            return out
        for entry in pool.get("entries") or []:
            if not isinstance(entry, dict):
                continue
            boss = asset_ref_name(entry.get("monster"))
            if boss in boss_names:
                out.append(
                    {
                        "boss": boss,
                        "chance": entry.get("chance"),
                        "min_count": entry.get("minCount"),
                        "max_count": entry.get("maxCount"),
                        "conditions": entry.get("conditions"),
                    }
                )
        return out

    print("--- spawn rules found in bundle ---")
    for (rf, rid), rule_tree in sorted(rules.items()):
        try:
            pool_ptr = rule_tree.get("pool") or {}
            r = resolve_ptr(index, rf, pool_ptr)
            pool = pools.get(r) if r else None
            if pool is None:
                print(f"rule {rule_tree.get('m_Name')}: pool UNRESOLVED (ptr={pool_ptr!r:.120}, resolved={r})")
                continue
            raw_entries = pool.get("entries") or []
            sample = raw_entries[0] if raw_entries else None
            print(
                f"rule {rule_tree.get('m_Name')}: pool ok, {len(raw_entries)} entries; "
                f"sample={json.dumps(sample, default=str)[:260]}; "
                f"bossNamesSample={sorted(boss_names)[:3]}"
            )
        except Exception as exc:
            keys = sorted(k for k in rule_tree.keys() if not k.startswith("m_"))
            print(f"rule {rule_tree.get('m_Name')}: FAILED ({exc}); fields={keys}")

    # --- pass 2: per-scene rule resolution ---------------------------------------
    scene_bosses: dict[str, list[str]] = {}
    details: list[dict] = []
    all_scene_files = sorted(set(rooms_by_scene) | set(sections_by_scene))
    for scene in all_scene_files:
        scene_rules: list[tuple[str, dict]] = []
        for tree in rooms_by_scene.get(scene, []):
            rule = rule_from_ptr(scene, tree.get("defaultRule") or {})
            if rule:
                scene_rules.append(("RoomMonsters.defaultRule", rule))
        for tree in sections_by_scene.get(scene, []):
            rule = rule_from_ptr(scene, tree.get("ruleOverride") or {})
            if rule:
                scene_rules.append(("Section_Monsters.ruleOverride", rule))
        if not scene_rules:
            continue
        bosses: list[str] = []
        evidence: list[dict] = []
        for source, rule in scene_rules:
            rule_name = rule.get("m_Name") or f"rule@{id(rule)}"
            found = rule_pool_bosses(scene, rule)
            if found:
                evidence.append({"source": source, "rule": rule_name, "entries": found})
                for e in found:
                    if e["boss"] not in bosses:
                        bosses.append(e["boss"])
        if bosses:
            scene_bosses[scene] = bosses
            details.append({"scene": scene, "rules": evidence})

    boss_scenes: dict[str, list[str]] = {}
    for scene, bosses in scene_bosses.items():
        for boss in bosses:
            boss_scenes.setdefault(boss, []).append(scene)
    for v in boss_scenes.values():
        v.sort()

    doc = {
        "schema": 1,
        "generated": time.strftime("%Y-%m-%d"),
        "source": "unity scenes (extract_room_boss_map.py)",
        "boss_scenes": boss_scenes,
        "scene_bosses": scene_bosses,
        "details": details,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(doc, ensure_ascii=False, indent=1), encoding="utf-8")

    unmatched = sorted(boss_names - set(boss_scenes))
    print(f"wrote {args.output}")
    print(f"scenes with boss pools: {len(scene_bosses)}")
    print(f"bosses mapped: {len(boss_scenes)}/{len(boss_names)}")
    if unmatched:
        print(f"bosses with NO scene: {unmatched}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
