#!/usr/bin/env python3
"""Stage 2 (rev.3): build the human-readable per-difficulty/variant DB.

Reads the same four sources as rev.2 (RawData override patches, Unity
prefab dumps, decompiled formulas, decompiled AI classes) but emits one
self-contained file per (difficulty, playable variant) combination:

    processed_data/out/<Mon_name>/<difficulty>.base.json
    processed_data/out/<Mon_name>/<difficulty>.hard_variant.json

Variant files are only emitted for difficulties that can actually roll
the hard variant (difficulty.hardVariantBossZones > 0).  Every file is
standalone: stats are difficulty-adjusted, cooldowns/channels/postDelays
are precomputed to final numbers, and all moves the boss can cast in
that combination (pool moves in priority order, reactive AI-class moves,
basic attack, off-pool extras) appear in a single "moves" list -- no
cross-referencing between files or sections.

Design rules (from the project owner):
  * The JSON is for humans first (GitHub rendering); machine-oriented
    provenance (raw overrides, prefab defaults, per-difficulty formula
    tables) stays in processed_data/raw and processed_data/unity_dump.
  * pools + abilities are merged: every move entry carries its own
    selection info, so one Ctrl+F finds everything about a move.
  * Reactive AI-class information is folded into the move entries
    (class threshold fields travel with each reactive move).
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import time
from pathlib import Path

from cast_logic import extract_cast_logic, find_attack_ability_bindings

SCRIPT_DIR = Path(__file__).resolve().parent
RAW_DIR = SCRIPT_DIR / "processed_data" / "raw" / "Monsters"
UNITY_DIR = SCRIPT_DIR / "processed_data" / "unity_dump"
DECOMPILED_CONTENT = SCRIPT_DIR.parent / "ref" / "decompiled" / "Dew.Contents"
DECOMPILED_CORE = SCRIPT_DIR.parent / "ref" / "decompiled" / "Dew.Core"
DEFAULT_OUTPUT = SCRIPT_DIR / "processed_data" / "out"

SCHEMA_VERSION = 3

INT_RE = re.compile(r"^[+-]?\d+$")
FLOAT_RE = re.compile(r"^[+-]?(\d+\.\d*|\.\d+|\d+)([eE][+-]?\d+)?$")

MONSTER_TYPE_NAMES = {0: "Lesser", 1: "Normal", 2: "MiniBoss", 3: "Boss"}

CONFIG_FIELDS = {
    "isActive": "is_active",
    "_cooldownTime": "cooldown",
    "_minimumDelay": "minimum_delay",
    "_maxCharges": "max_charges",
    "startCharges": "start_charges",
    "_addedCharges": "added_charges",
    "_manaCost": "mana_cost",
    "postDelay": "post_delay",
    "canReceiveCooldownReduction": "can_receive_cooldown_reduction",
    "lockCooldownUntilKilled": "lock_cooldown_until_killed",
    "lockCastUntilKilled": "lock_cast_until_killed",
}
CAST_METHOD_FIELDS = {
    "_angle": "angle",
    "_length": "length",
    "_width": "width",
    "_range": "range",
    "_radius": "radius",
    "_isClamping": "is_clamping",
}
CONFIGS_RE = re.compile(r"^configs\.(\d+)\.(.+)$")

TRIGGER_NOISE_KEYS = {
    "configs", "unity_path_id", "class_name", "gameobject_name",
    "m_GameObject", "m_Script", "m_Enabled", "m_Name", "triggerIcon",
    "syncDirection", "syncMode", "syncInterval",
}


# --------------------------------------------------------------- loading

def coerce(raw: str):
    if raw == "":
        return None
    if raw == "True":
        return True
    if raw == "False":
        return False
    if INT_RE.match(raw):
        return int(raw)
    if FLOAT_RE.match(raw):
        return float(raw)
    return raw


def load_patch(path: Path):
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    if not (isinstance(data, list) and len(data) == 1 and isinstance(data[0], dict)):
        raise ValueError(f"unexpected patch shape in {path}")
    entry = data[0]
    return entry["target"], {
        k: coerce(v) for k, v in (entry.get("overrides") or {}).items()
    }


def pop_prefix(overrides: dict, prefix: str) -> dict:
    picked = {}
    for key in [k for k in overrides if k.startswith(prefix)]:
        picked[key[len(prefix):]] = overrides.pop(key)
    return picked


def scan_class_bases() -> dict:
    bases = {}
    for folder in (DECOMPILED_CONTENT, DECOMPILED_CORE):
        if not folder.is_dir():
            continue
        for cs in folder.glob("*.cs"):
            m = re.search(
                r"public class (\w+)\s*:\s*([\w<>,\s]+?)(?:\s*$|\{)",
                cs.read_text(encoding="utf-8", errors="replace"), re.M)
            if m:
                bases[m.group(1)] = m.group(2).strip()
    return bases


# ------------------------------------------------------- move building

def normalize_config(raw_cfg: dict, index: int) -> dict:
    c = dict(raw_cfg)
    c["config_index"] = index
    c.pop("triggerIcon", None)
    cm = c.get("castMethod")
    if isinstance(cm, dict):
        c["cast_method"] = {
            "type": cm.get("type"),
            "range": cm.get("_range"),
            "width": cm.get("_width"),
            "length": cm.get("_length"),
            "angle": cm.get("_angle"),
            "radius": cm.get("_radius"),
            "is_clamping": bool(cm.get("_isClamping")),
        }
        c.pop("castMethod", None)
    sir = c.get("spawnedInstanceRef")
    if isinstance(sir, dict):
        c["instance"] = sir.get("typeName") or None
        c.pop("spawnedInstanceRef", None)
    for k in list(c):
        if k in CONFIG_FIELDS:
            c[CONFIG_FIELDS[k]] = c.pop(k)
    return c


def merge_trigger(tname: str, prefab_comp: dict | None,
                  overrides_list: list[dict]) -> dict:
    """Return {trigger, class_name, base_class, configs, trigger_params}."""
    if prefab_comp:
        configs = [normalize_config(c, i)
                   for i, c in enumerate(prefab_comp.get("configs") or [])]
        merged = {
            "trigger": tname,
            "class_name": prefab_comp.get("class_name") or tname,
            "configs": configs,
        }
        params = {}
        for key, value in prefab_comp.items():
            if key in TRIGGER_NOISE_KEYS:
                continue
            params[key] = value
        merged["trigger_params"] = params
        source = "prefab"
    else:
        merged = {"trigger": tname, "class_name": tname, "configs": [],
                  "trigger_params": {}}
        source = "overrides_only"

    for overrides in overrides_list:
        config_ovr: dict[int, dict] = {}
        trig_ovr: dict = {}
        for key, value in overrides.items():
            m = CONFIGS_RE.match(key)
            if m:
                config_ovr.setdefault(int(m.group(1)), {})[m.group(2)] = value
            else:
                trig_ovr[key] = value
        for key, value in trig_ovr.items():
            if key in ("m_Name",):
                continue
            out_key = CONFIG_FIELDS.get(key, key)
            merged["trigger_params"][out_key] = value
        for idx, fields in sorted(config_ovr.items()):
            cfg = next((c for c in merged["configs"]
                        if c["config_index"] == idx), None)
            if cfg is None:
                cfg = {"config_index": idx}
                merged["configs"].append(cfg)
                merged["configs"].sort(key=lambda c: c["config_index"])
            for key, value in fields.items():
                if key.startswith("castMethod."):
                    sub = key[len("castMethod."):]
                    cfg.setdefault("cast_method", {})[
                        CAST_METHOD_FIELDS.get(sub, sub)] = value
                else:
                    cfg[CONFIG_FIELDS.get(key, key)] = value
        source = "prefab+override" if prefab_comp else "overrides_only"
    merged["source"] = source
    return merged


def fmt_num(x) -> int | float | None:
    """Round floats for human reading (4 decimals max)."""
    if isinstance(x, bool) or x is None:
        return x
    if isinstance(x, int):
        return x
    if isinstance(x, float):
        r = round(x, 4)
        return int(r) if r == int(r) else r
    return x


def difficulty_factors(doc: dict) -> dict:
    return {
        "health_pct": doc.get("enemyHealthPercentage") or 0.0,
        "power_pct": doc.get("enemyPowerPercentage") or 0.0,
        "move_pct": doc.get("enemyMovementSpeedPercentage") or 0.0,
        "aspd_pct": doc.get("enemyAttackSpeedPercentage") or 0.0,
        "haste_flat": doc.get("enemyAbilityHasteFlat") or 0.0,
        "boss_armor_flat": doc.get("heroicBossArmor") or 0.0,
    }


# --------------------------------------------------------------- main

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--raw", type=Path, default=RAW_DIR)
    parser.add_argument("--unity", type=Path, default=UNITY_DIR)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    args = parser.parse_args()

    for label, d in (("--raw", args.raw), ("--unity", args.unity)):
        if not d.is_dir():
            print(f"{label} directory not found: {d}", file=sys.stderr)
            return 2

    unity_monsters = json.loads(
        (args.unity / "monsters.json").read_text(encoding="utf-8"))
    unity_triggers = json.loads(
        (args.unity / "ability_triggers.json").read_text(encoding="utf-8"))
    unity_difficulty = json.loads(
        (args.unity / "difficulty.json").read_text(encoding="utf-8"))

    difficulty_table = {
        name: doc for name, doc in unity_difficulty.items()
        if not name.startswith("_ges:")
    }
    bases = scan_class_bases()

    raw_dirs: dict[str, list] = {}
    for dir_path in sorted(p for p in args.raw.iterdir() if p.is_dir()):
        patches = []
        for file_path in sorted(dir_path.glob("*.json")):
            try:
                patches.append(load_patch(file_path))
            except (ValueError, json.JSONDecodeError) as exc:
                print(f"  ! {file_path.name}: {exc}", file=sys.stderr)
        raw_dirs[dir_path.name] = patches

    def find_raw_dir(mon_name: str):
        for dir_name, patches in raw_dirs.items():
            if any(t == mon_name for t, _ in patches):
                return dir_name, patches
        return None, []

    # ---------- classify + per-boss merged trigger index
    bosses = []
    roster = {}
    for mon_name, prefab in sorted(unity_monsters.items()):
        mtype = MONSTER_TYPE_NAMES.get(prefab.get("type"),
                                       f"unknown({prefab.get('type')})")
        pools = prefab.get("abilityPools") or []
        dir_name, patches = find_raw_dir(mon_name)
        roster[mon_name] = {
            "monster_type": mtype,
            "pool_count": len(pools),
            "has_hard_variant": len(pools) > 1,
            "source_dir": dir_name,
        }
        if mtype == "Boss":
            bosses.append((mon_name, prefab, patches, dir_name))

    out: Path = args.output
    if out.exists():
        import shutil
        for stale in out.iterdir():
            if stale.is_dir():
                shutil.rmtree(stale)
            else:
                stale.unlink()
    out.mkdir(parents=True, exist_ok=True)

    files_written = 0
    for boss_name, prefab, patches, dir_name in bosses:
        raw_by_target: dict[str, list] = {}
        for target, overrides in patches:
            raw_by_target.setdefault(target, []).append(overrides)

        # ---- monster stats (RawData full coverage of EntityStatus)
        mon_ovr: dict = {}
        for o in raw_by_target.get(boss_name, []):
            mon_ovr.update(o)
        base_stats = pop_prefix(dict(mon_ovr), "EntityStatus::baseStats.")
        pop_prefix(dict(mon_ovr), "EntityStatus::scalingStats.")
        control = pop_prefix(dict(mon_ovr), "EntityControl::")

        # ---- merged triggers for everything this boss references
        pool_defs = prefab.get("abilityPools") or []
        referenced: set[str] = set()
        for pool in pool_defs:
            for e in pool.get("abilities") or []:
                tn = (e.get("triggerPrefab") or {}).get("typeName")
                if tn:
                    referenced.add(tn)
            atk = (pool.get("attackTriggerPrefab") or {}).get("typeName")
            if atk:
                referenced.add(atk)
        referenced |= {t for t in raw_by_target if t.startswith("At_")}

        merged_triggers: dict[str, dict] = {}
        for tname in referenced:
            prefab_docs = unity_triggers.get(tname) or []
            comp = prefab_docs[0] if prefab_docs else None
            merged_triggers[tname] = merge_trigger(
                tname, comp, raw_by_target.get(tname, []))

        ai_class_name = prefab.get("class_name") or boss_name
        # inline constants resolve against prefab values with RawData
        # overrides applied (the values the live game actually uses)
        prefab_for_logic = {**prefab, **mon_ovr}
        logic_map = extract_cast_logic(
            ai_class_name, prefab_for_logic, candidates=set(merged_triggers))
        attack_bindings = find_attack_ability_bindings(ai_class_name)

        # ---- move entries per pool-variant
        def build_moves(pool_index: int | None) -> list:
            moves = []
            used: set[str] = set()
            if pool_index is not None:
                pool = pool_defs[pool_index]
                atk_name = (pool.get("attackTriggerPrefab") or {}).get("typeName")
                if atk_name and atk_name in merged_triggers:
                    used.add(atk_name)
                for order, e in enumerate(pool.get("abilities") or []):
                    tn = (e.get("triggerPrefab") or {}).get("typeName")
                    used.add(tn)
            # ordered: basic attack, pool moves (order), reactive, extras
            def selection_for(tname: str) -> dict:
                if pool_index is not None:
                    pool = pool_defs[pool_index]
                    atk_name = (pool.get("attackTriggerPrefab") or {}).get("typeName")
                    if tname == atk_name:
                        return {"mode": "basic_attack",
                                "note": "bound as the pool's attack trigger"}
                    for order, e in enumerate(pool.get("abilities") or []):
                        tn = (e.get("triggerPrefab") or {}).get("typeName")
                        if tn == tname:
                            nh = e.get("normalizedHealthRange") or {}
                            return {
                                "mode": "pool",
                                "order": order,
                                "chance": fmt_num(e.get("chance")),
                                "hp_window": {
                                    "enabled": bool(e.get("healthConditionEnable")),
                                    "min": fmt_num(nh.get("x")),
                                    "max": fmt_num(nh.get("y")),
                                },
                                "note": "AIUpdate walks pool entries in order; "
                                        "first entry passing chance roll, "
                                        "cooldown-ready, range and HP window "
                                        "checks is cast",
                            }
                if tname in attack_bindings:
                    return {"mode": "basic_attack",
                            "note": "bound as the entity's attack ability "
                                    "in AI code (attackAbility)"}
                if re.search(r"_(Atk|AltAtk|AltAk)$", tname):
                    return {"mode": "basic_attack",
                            "note": "assumed basic/alt attack (every "
                                    "off-pool attack-family trigger across "
                                    "all bosses matches this pattern)"}
                if tname in logic_map:
                    if not pool_defs:
                        return {"mode": "ai_class",
                                "note": f"cast by {ai_class_name} AI code"}
                    return {"mode": "reactive",
                            "note": f"cast by {ai_class_name} AI code "
                                    f"outside the ability pool"}
                return {"mode": "off_pool",
                        "note": "not referenced by the ability pool and no "
                                "cast site found in the AI class"}

            ordered = []
            sibling = None
            if pool_index is not None:
                other = 1 - pool_index if len(pool_defs) > 1 else None
                if other is not None:
                    sibling = {
                        (e.get("triggerPrefab") or {}).get("typeName")
                        for e in (pool_defs[other].get("abilities") or [])
                    }
                pool = pool_defs[pool_index]
                atk_name = (pool.get("attackTriggerPrefab") or {}).get("typeName")
                if atk_name and atk_name in merged_triggers:
                    ordered.append(atk_name)
                for e in pool.get("abilities") or []:
                    tn = (e.get("triggerPrefab") or {}).get("typeName")
                    if tn and tn not in ordered:
                        ordered.append(tn)
            for tn in logic_map:
                if tn in merged_triggers and tn not in ordered \
                        and not (sibling and tn in sibling):
                    # sibling-pool exclusive moves are not castable in
                    # this combination even if AI code references them
                    ordered.append(tn)
            for tn in sorted(merged_triggers):
                if tn not in ordered and not (sibling and tn in sibling):
                    ordered.append(tn)
                elif tn not in ordered:
                    # variant-exclusive to the sibling pool: not castable
                    # in this combination, so it is left out entirely
                    continue

            for tname in ordered:
                trig = merged_triggers[tname]
                base_class = bases.get(trig["class_name"])
                sel = selection_for(tname)
                logic_doc = logic_map.get(tname)
                if logic_doc and logic_doc.get("table_entry") \
                        and "table_entry" not in sel:
                    sel["table_entry"] = {
                        k: (fmt_num(v) if isinstance(v, float) else v)
                        for k, v in logic_doc["table_entry"].items()
                    }
                for cfg in trig["configs"]:
                    if cfg.get("is_active") is False:
                        continue
                    move = {
                        "name": tname,
                        "config_index": cfg["config_index"],
                        "selection": sel,
                        "base_class": base_class,
                    }
                    if logic_doc:
                        move["cast_logic"] = logic_doc["logic"]
                    move["cooldown"] = fmt_num(cfg.get("cooldown"))
                    move["minimum_delay"] = fmt_num(cfg.get("minimum_delay"))
                    move["max_charges"] = cfg.get("max_charges")
                    move["start_charges"] = cfg.get("start_charges")
                    move["added_charges"] = cfg.get("added_charges")
                    move["mana_cost"] = fmt_num(cfg.get("mana_cost"))
                    move["post_delay"] = fmt_num(cfg.get("post_delay"))
                    ch = cfg.get("channel")
                    move["channel_duration"] = fmt_num((ch or {}).get("duration")) \
                        if isinstance(ch, dict) else None
                    move["can_receive_cooldown_reduction"] = cfg.get(
                        "can_receive_cooldown_reduction", False)
                    move["lock_cooldown_until_killed"] = bool(
                        cfg.get("lock_cooldown_until_killed"))
                    move["lock_cast_until_killed"] = bool(
                        cfg.get("lock_cast_until_killed"))
                    move["cast_method"] = cfg.get("cast_method")
                    move["instance"] = cfg.get("instance")
                    extra = {}
                    for k, v in cfg.items():
                        if k in ("config_index", "cooldown", "minimum_delay",
                                 "max_charges", "start_charges", "added_charges",
                                 "mana_cost", "post_delay", "channel",
                                 "can_receive_cooldown_reduction",
                                 "lock_cooldown_until_killed",
                                 "lock_cast_until_killed", "cast_method",
                                 "instance", "is_active", "startAnim",
                                 "endAnim", "castVoice", "effectOnCast",
                                 "appliedStatusEffectRef", "victim"):
                            continue
                        if isinstance(v, (bool, int, float, str)) or v is None:
                            extra[k] = v
                        elif isinstance(v, dict) and all(
                                isinstance(x, (bool, int, float, str, type(None)))
                                for x in v.values()):
                            extra[k] = v
                    if extra:
                        move["extra"] = extra
                    moves.append(move)
            return moves

        def scaled_stats(fac: dict) -> dict:
            def pct(v, p):
                return fmt_num((v or 0) * (1 + p / 100.0))
            stats = {
                "max_health": pct(base_stats.get("maxHealth"), fac["health_pct"]),
                "attack_damage": pct(base_stats.get("attackDamage"), fac["power_pct"]),
                "ability_power": pct(base_stats.get("abilityPower"), fac["power_pct"]),
                "armor": fmt_num((base_stats.get("armor") or 0) + fac["boss_armor_flat"]),
                "ability_haste": fmt_num((base_stats.get("abilityHaste") or 0)
                                         + fac["haste_flat"]),
                "attack_speed_multiplier": fmt_num(1 + fac["aspd_pct"] / 100.0),
                "movement_speed": fmt_num((control.get("baseAgentSpeed") or 0)
                                          * (1 + fac["move_pct"] / 100.0)),
                "crit_chance": fmt_num(base_stats.get("critChance")),
                "tenacity": fmt_num(base_stats.get("tenacity")),
                "max_mana": fmt_num(base_stats.get("maxMana")),
                "health_regen": fmt_num(base_stats.get("healthRegen")),
                "mana_regen": fmt_num(base_stats.get("manaRegen")),
            }
            return stats

        def precompute(cfg: dict, base_class: str | None, fac: dict) -> dict:
            values = {}
            cd = cfg.get("cooldown")
            aspd = 1 + fac["aspd_pct"] / 100.0
            if isinstance(cd, (int, float)):
                if base_class == "AttackTrigger":
                    values["cooldown"] = fmt_num(cd / aspd)
                elif cfg.get("can_receive_cooldown_reduction"):
                    values["cooldown"] = fmt_num(
                        cd / (1 + (fac["haste_flat"]) / 100.0))
                else:
                    values["cooldown"] = fmt_num(cd)
            pd = cfg.get("post_delay")
            if isinstance(pd, (int, float)) and base_class == "AttackTrigger":
                values["post_delay"] = fmt_num(pd / aspd)
            ch = cfg.get("channel")
            chd = (ch or {}).get("duration") if isinstance(ch, dict) else None
            if isinstance(chd, (int, float)) and base_class == "AttackTrigger":
                values["channel_duration"] = fmt_num(chd / aspd)
            return values

        # ---- emit per (difficulty, variant)
        has_variant = len(pool_defs) > 1
        boss_dir = out / boss_name
        boss_dir.mkdir(exist_ok=True)

        for diff_name, diff_doc in sorted(difficulty_table.items()):
            fac = difficulty_factors(diff_doc)
            can_roll_variant = (diff_doc.get("hardVariantBossZones") or 0) > 0
            variants = (["base", "hard_variant"]
                        if has_variant and can_roll_variant else ["base"])
            for variant in variants:
                if not pool_defs:
                    pool_index = None
                elif variant == "hard_variant":
                    pool_index = 1
                else:
                    pool_index = 0
                moves = build_moves(pool_index)
                for move in moves:
                    trig = merged_triggers[move["name"]]
                    cfg = next(c for c in trig["configs"]
                               if c["config_index"] == move["config_index"])
                    bc = move["base_class"]
                    eff = precompute(cfg, bc, fac)
                    for key, value in eff.items():
                        move[key] = value

                doc = {
                    "schema_version": SCHEMA_VERSION,
                    "boss": boss_name,
                    "monster_type": MONSTER_TYPE_NAMES.get(prefab.get("type")),
                    "difficulty": diff_name,
                    "variant": variant,
                    "generated_at": time.strftime("%Y-%m-%d"),
                    "stats": scaled_stats(fac),
                    "variant_note": (
                        f"this variant spawns when the run's randomly picked "
                        f"hard-variant boss zones include this zone "
                        f"(difficulty.hardVariantBossZones = "
                        f"{diff_doc.get('hardVariantBossZones')})"
                    ) if has_variant else None,
                    "ai_logic_note": (
                        f"all moves are driven by {ai_class_name} AI "
                        f"code (no ability pool)" if not pool_defs else None
                    ),
                    "reading_notes": {
                        "selection.order": "AIUpdate walks pool entries in "
                                           "order; first valid entry casts",
                        "selection.chance": "per-second probability gate",
                        "hp_window": "normalized health bounds; move only "
                                     "casts inside the window",
                        "cooldown": "already scaled for this difficulty "
                                    "(attack-speed for AttackTrigger moves, "
                                    "ability haste for others when "
                                    "can_receive_cooldown_reduction)",
                        "minimum_delay": "unscaled by the engine",
                        "instance": "AbilityInstance resource executing the "
                                    "move's effect (details in unity_dump)",
                        "cast_logic": "pseudo-code from the decompiled AI "
                                      "class with constants inlined: "
                                      "ready<T>() = cooldown ready + not "
                                      "blocked; in_range<T>() = target "
                                      "inside cast range; CAST<T>() = "
                                      "execute; rand = 0..1 roll; dt = "
                                      "frame delta time; hp_ratio = boss "
                                      "health fraction",
                    },
                    "moves": moves,
                }
                doc = {k: v for k, v in doc.items() if v is not None}
                fname = f"{diff_name}.{variant}.json"
                (boss_dir / fname).write_text(
                    json.dumps(doc, ensure_ascii=False, indent=2),
                    encoding="utf-8")
                files_written += 1

    roster_doc = {
        "schema_version": SCHEMA_VERSION,
        "generated_at": time.strftime("%Y-%m-%d %H:%M:%S"),
        "monster_count": len(roster),
        "boss_count": len(bosses),
        "bosses": sorted(n for n, v in roster.items()
                         if v["monster_type"] == "Boss"),
        "monsters": dict(sorted(roster.items())),
    }
    (out / "_monsters.json").write_text(
        json.dumps(roster_doc, ensure_ascii=False, indent=2), encoding="utf-8")

    ges_default = unity_difficulty.get("_ges:GSS - Default", {})
    difficulty_doc = {
        "schema_version": SCHEMA_VERSION,
        "source": "unity_dump/difficulty.json (DewDifficultySettings assets)",
        "gameplay_experience_settings": {
            "default_asset": "GSS - Default",
            "cooldownFloorRatioByAbilityHaste":
                ges_default.get("cooldownFloorRatioByAbilityHaste", 0.0),
        },
        "difficulties": dict(sorted(difficulty_table.items())),
    }
    (out / "_difficulty.json").write_text(
        json.dumps(difficulty_doc, ensure_ascii=False, indent=2),
        encoding="utf-8")

    print(f"bosses   : {len(bosses)}")
    print(f"files    : {files_written} per-difficulty/variant JSONs")
    print(f"output   : {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
