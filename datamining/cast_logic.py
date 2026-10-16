#!/usr/bin/env python3
"""Extract per-move cast logic from decompiled boss AI classes.

For every ``Helper_CastAbilityAuto`` call site in a boss's C# class this
module captures the governing ``if`` / ``else if`` / ``foreach`` chain,
inlines constant values (prefab serialized fields first, then code
field defaults, then custom skill-table entries), normalizes helper
names, and returns readable pseudo-code lines per trigger.

Result shape::

    {
      "At_Mon_..._Blink": {
          "method": "PoolAIUpdate",
          "logic": [
              "if (_lastBlinkHpRatio - hp_ratio >= 0.1)",
              "    if (hp_ratio < 0.3333)",
              "        if (ready(At_Mon_..._Blink))",
              "            CAST(At_Mon_..._Blink)",
          ],
      },
      ...
    }

Table-driven bosses (e.g. Mon_Special_BossMaw iterating a serialized
``totalSkillList``) are expanded per table entry: the same generic call
site is attributed to every entry, and the entry's own values (chance,
customRange, ...) are substituted into the code lines.
"""

from __future__ import annotations

import re
from pathlib import Path

DECOMPILED_CONTENT = Path(__file__).resolve().parent.parent / "ref" / "decompiled" / "Dew.Contents"
DECOMPILED_CORE = Path(__file__).resolve().parent.parent / "ref" / "decompiled" / "Dew.Core"

CAST_CALL_RE = re.compile(
    r"Helper_CastAbility(?:Auto)?\s*(?:<\s*([\w.]+)\s*>)?\s*\(\s*([\w.]*)")
FOREACH_RE = re.compile(
    r"foreach\s*\(\s*[\w.<>\s]+\s+(\w+)\s+in\s+(?:this\.)?([\w.]+)\s*\)")
METHOD_RE = re.compile(
    r"^\s*(?:protected|public|private|internal)[\w\s]*?\b(bool|void|float|int|IEnumerable[^ ]*)\s+(\w+)\s*\(")
HEADER_RE = re.compile(r"^\s*(?:if|else\s+if|else|foreach|for|while|switch|case\b|default:)")
SKIP_LINE_RE = re.compile(
    r"^\s*[\{\}]\s*$|^\s*return\s+[;\w]?\s*$|^\s*break;\s*$|"
    r"^\s*continue;\s*$|^\s*$")

CLEANUP_SUBS = [
    (re.compile(r"\bUnityEngine\.Random\.value\b"), "rand"),
    (re.compile(r"\bcontext\.deltaTime\b"), "dt"),
    (re.compile(r"\bAI\.Helper_CanBeCast\s*<"), "ready<"),
    (re.compile(r"\bAI\.Helper_CanBeCast\b"), "ready"),
    (re.compile(r"\bAI\.Helper_IsTargetInRange\s*<"), "in_range<"),
    (re.compile(r"\bAI\.Helper_IsTargetInRange\b"), "in_range"),
    (re.compile(r"\bAI\.Helper_CastAbilityAuto\s*<\s*([\w.]+)\s*>\s*\(\s*\)"),
     r"CAST(\1)"),
    (re.compile(r"\bAI\.Helper_CastAbilityAuto\("), "CAST("),
    (re.compile(r"\bnormalizedHealth\b"), "hp_ratio"),
    (re.compile(r"\bAI\."), ""),
    (re.compile(r"\bbase\."), ""),
    (re.compile(r"\bthis\."), ""),
]

F_SUFFIX_RE = re.compile(r"(\d)f\b")

_CLASS_INDEX: dict | None = None


def find_attack_ability_bindings(class_name: str) -> set[str]:
    """Triggers bound as the entity's attack ability in code
    (``Ability.attackAbility is <T>`` patterns)."""
    path = _find_class_file(class_name)
    if path is None:
        return set()
    text = path.read_text(encoding="utf-8", errors="replace")
    return set(re.findall(r"attackAbility\s+is\s+(\w+)", text))


def _classwide_cast_index() -> dict:
    """Map trigger class name -> set of classes casting it (all sources)."""
    global _CLASS_INDEX
    if _CLASS_INDEX is None:
        idx: dict = {}
        pat = re.compile(r"Helper_CastAbility(?:Auto)?\s*<\s*([\w.]+)\s*>")
        for folder in (DECOMPILED_CONTENT, DECOMPILED_CORE):
            if not folder.is_dir():
                continue
            for cs in folder.glob("*.cs"):
                try:
                    text = cs.read_text(encoding="utf-8", errors="replace")
                except Exception:
                    continue
                for m in pat.finditer(text):
                    idx.setdefault(m.group(1), set()).add(cs.stem)
        _CLASS_INDEX = idx
    return _CLASS_INDEX


def _eval_default(expr: str):
    """Evaluate a simple C# field initializer like ``1f / 3f``."""
    expr = expr.strip().rstrip(";")
    try:
        expr = F_SUFFIX_RE.sub(r"\1", expr)
        if not re.fullmatch(r"[\d.\s+\-*/()]+", expr):
            return None
        return round(float(eval(expr)), 6)  # noqa: S307 -- digits/operators only
    except Exception:
        return None


def _find_class_file(class_name: str) -> Path | None:
    for folder in (DECOMPILED_CONTENT, DECOMPILED_CORE):
        p = folder / f"{class_name}.cs"
        if p.exists():
            return p
    return None


def collect_tables(doc, path: str = "", out: list | None = None) -> list:
    """Find serialized lists whose entries reference triggers by typeName."""
    if out is None:
        out = []
    if isinstance(doc, dict):
        for k, v in doc.items():
            if isinstance(v, list) and v and isinstance(v[0], dict):
                entries = [e for e in v if isinstance(e, dict)]
                if any(_entry_trigger(e) for e in entries):
                    out.append({"field": k, "entries": entries})
            collect_tables(v, f"{path}.{k}" if path else k, out)
    return out


def _entry_trigger(entry: dict):
    for key in ("abilityTrigger", "triggerPrefab"):
        ref = entry.get(key)
        if isinstance(ref, dict) and ref.get("typeName"):
            return ref["typeName"]
    return None


def extract_cast_logic(class_name: str, prefab_doc: dict,
                       candidates: set[str] | None = None) -> dict:
    """Return per-trigger cast-logic docs for one boss class."""
    path = _find_class_file(class_name)
    if path is None:
        return {}
    text = path.read_text(encoding="utf-8", errors="replace")
    lines = text.splitlines()

    # constant candidates: prefab scalars, then code field defaults
    constants: dict = {}
    for k, v in (prefab_doc or {}).items():
        if isinstance(v, (bool, int, float, str)):
            constants.setdefault(k, v)
    for m in re.finditer(
            r"public (?:[\w<>,\.\[\]]+\s+)?(\w+) (\w+) = ([^;]+);", text):
        val = _eval_default(m.group(3))
        if val is not None:
            constants.setdefault(m.group(2), val)

    tables = collect_tables(prefab_doc or {})
    foreach_bindings: dict[str, str] = {}
    for m in FOREACH_RE.finditer(text):
        foreach_bindings.setdefault(m.group(1), m.group(2))

    # brace depth per line
    depths = []
    depth = 0
    for line in lines:
        depths.append(depth)
        depth += line.count("{") - line.count("}")

    results: dict = {}

    def inline_values(line: str, loop_entry: dict | None,
                      loop_var: str | None = None) -> str:
        def fmt(v):
            return ("%g" % v) if isinstance(v, float) else str(v)

        if loop_var:
            line = re.sub(rf"{re.escape(loop_var)}\.", "", line)
        if loop_entry:
            for k, v in loop_entry.items():
                if isinstance(v, (bool, int, float)):
                    line = re.sub(rf"\b{re.escape(k)}\b", fmt(v), line)
        for k, v in constants.items():
            if isinstance(v, (bool, int, float)):
                line = re.sub(rf"\b{re.escape(k)}\b", fmt(v), line)
        for pat, repl in CLEANUP_SUBS:
            line = pat.sub(repl, line)
        return line.rstrip()

    def governing_chain(idx: int) -> list[tuple[int, int]]:
        """Return (depth, line_no) stack of headers governing line idx."""
        stack: list[tuple[int, int]] = []
        for i in range(idx):
            line = lines[i]
            if re.match(r"^\s*else\b", line):
                # else negates the preceding same-depth chain
                while stack and depths[i] <= stack[-1][0]:
                    stack.pop()
            elif re.match(r"^\s*(case\b|default:)", line):
                # a new case replaces the previous case label but keeps
                # its enclosing switch
                while stack and depths[i] == stack[-1][0] and \
                        re.match(r"^\s*(case\b|default:)", lines[stack[-1][1]]):
                    stack.pop()
            else:
                while stack and depths[i] < stack[-1][0]:
                    stack.pop()
            if HEADER_RE.match(line):
                stack.append((depths[i], i))
        while stack and depths[idx] < stack[-1][0]:
            stack.pop()
        # drop early-exit guards: a header with a DIRECT return in its
        # own body (not nested inside a child statement)
        kept = []
        for d, i in stack:
            j = i + 1
            while j < idx and depths[j] <= d:
                j += 1  # skip brace-only lines before the body starts
            has_direct_return = False
            while j < idx and depths[j] > d:
                if depths[j] == d + 1 and re.search(r"\breturn\b", lines[j]):
                    has_direct_return = True
                    break
                j += 1
            if has_direct_return:
                continue
            kept.append((d, i))
        return kept

    def method_of(idx: int) -> str:
        for i in range(idx, -1, -1):
            m = METHOD_RE.match(lines[i])
            if m and depths[i] <= 1:
                return m.group(2)
        return "?"

    # loop/iteration variable names used with runtime trigger references
    loop_vars: set[str] = set(foreach_bindings)
    for i in range(len(lines)):
        m = re.search(r"\b(\w+)\s*=\s*[\w.]+\[\s*\w+\s*\]", lines[i])
        if m:
            loop_vars.add(m.group(1))

    def entry_for_call(call_arg: str) -> list[tuple[str, dict]]:
        """Attribute a non-generic ``X.triggerInstance`` call to table
        entries: via a foreach binding when possible, else the single
        serialized table of the prefab."""
        var = call_arg.split(".")[0] if "." in call_arg else None
        field = foreach_bindings.get(var) if var else None
        cand = [t for t in tables if t["field"] == field] if field else []
        if not cand and len(tables) == 1:
            cand = tables
        out = []
        for t in cand:
            for entry in t["entries"]:
                tn = _entry_trigger(entry)
                if tn:
                    out.append((tn, entry))
        return out

    for idx, line in enumerate(lines):
        m = CAST_CALL_RE.search(line)
        if not m:
            continue
        generic_type = m.group(1)
        call_arg = (m.group(2) or "").strip()

        chain = governing_chain(idx)
        method = method_of(idx)

        targets: list[tuple[str, dict | None]] = []
        if generic_type:
            targets.append((generic_type, None))
        else:
            targets.extend(entry_for_call(call_arg))

        if not targets:
            continue

        # build the snippet: governing headers + the call line
        loop_var = call_arg.split(".")[0] if "." in call_arg else None
        snippet: list[str] = [f"// in {method}()"]
        for d, i in chain:
            if SKIP_LINE_RE.match(lines[i]):
                continue
            indent = " " * 4 * max(0, len([1 for dd, _ in chain if dd < d]))
            snippet.append(indent + inline_values(lines[i].strip(), None))
        call_indent = " " * 4 * len(chain)
        snippet.append(call_indent + inline_values(line.strip(), None))

        for tn, entry in targets:
            lines_out = []
            for s in snippet:
                lines_out.append(
                    inline_values(s, entry, loop_var) if entry else s)
            doc = {"method": method, "logic": lines_out}
            if entry is not None:
                doc["table_entry"] = {
                    k: v for k, v in entry.items()
                    if isinstance(v, (bool, int, float, str))
                }
            existing = results.get(tn)
            if existing is None or existing["method"] == "?":
                results[tn] = doc

    # fallback: triggers with no cast site in this class
    if candidates:
        index = _classwide_cast_index()
        for tn in candidates:
            if tn in results:
                continue
            others = sorted(index.get(tn, set()) - {class_name})
            if others:
                results[tn] = {
                    "method": "other class",
                    "logic": [f"// no cast site in {class_name}; "
                              f"cast by: {', '.join(others)}"],
                }
            else:
                results[tn] = {
                    "method": "unattributed",
                    "logic": [f"// no cast site found in decompiled code"],
                }
    return results
