#!/usr/bin/env python3
"""One-shot pipeline: game folder -> boss database.

Usage::

    python3 datamining/run_all.py <game_root> [--skip-decompile]

``<game_root>`` is the folder containing ``ShapeOfDreams_Data``
(the Steam install root, e.g. ``.../steamapps/common/Shape of Dreams``);
passing ``ShapeOfDreams_Data`` itself also works.

Steps
-----
1. sync inputs from the game folder (only the minimal set the pipeline
   actually consumes):
     - ``RawData/!ModResources/overrides/Monsters``  -> datamining/raw_game_data/
     - the Addressables content + monoscripts bundles
       (``defaultlocalgroup_assets_all_*.bundle``,
       ``*_monoscripts_*.bundle``)                  -> datamining/raw_game_data_unity/
     - ``Managed/Dew.Core.dll(.pdb)`` +
       ``Managed/Dew.Contents.dll(.pdb)``           -> ref/gamefiles/managed/
2. decompile the two assemblies with ilspycmd into ref/decompiled/
   (needs dotnet + ``dotnet tool install --global ilspycmd``;
   --skip-decompile reuses ref/decompiled/)
3. extract_raw.py        (any Python 3.8+, stdlib only)
4. extract_unity_pools.py (needs an interpreter with UnityPy installed;
   the script probes candidates automatically -- on this setup that is
   the Windows Python because the WSL kernel rejects modern conda builds)
5. build_db.py           (final per-difficulty/variant JSONs)

If a copy source is missing but the workspace input directory is
already populated, the step is reused with a warning (resume-friendly).
"""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
from pathlib import Path

SCRIPT_DIR = Path(__file__).resolve().parent
WS_DIR = SCRIPT_DIR.parent
REF_DIR = WS_DIR / "ref"
GAMEFILES = REF_DIR / "gamefiles" / "managed"
RAW_INPUT = SCRIPT_DIR / "raw_game_data"
UNITY_INPUT = SCRIPT_DIR / "raw_game_data_unity"
DECOMPILED = REF_DIR / "decompiled"

DLLS = ["Dew.Core.dll", "Dew.Contents.dll", "Dew.Core.pdb", "Dew.Contents.pdb"]


def info(msg: str):
    print(f"[run_all] {msg}", flush=True)


def wipe(dir_path: Path):
    if dir_path.exists():
        shutil.rmtree(dir_path)
    dir_path.mkdir(parents=True, exist_ok=True)


def copy_tree(src: Path, dst: Path):
    wipe(dst)
    for p in src.rglob("*"):
        if p.is_file():
            rel = p.relative_to(src)
            target = dst / rel
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(p, target)
    return sum(1 for _ in dst.rglob("*") if _.is_file())


def find_data_dir(game_root: Path) -> Path:
    cand = game_root / "ShapeOfDreams_Data"
    if cand.is_dir():
        return cand
    if (game_root / "Managed").is_dir() and (game_root / "globalgamemanagers").exists():
        return game_root
    sys.exit(f"error: {game_root} does not look like the game folder "
             f"(no ShapeOfDreams_Data inside, no Managed/ layout)")


def sync_inputs(data_dir: Path) -> bool:
    """Copy minimal inputs. Returns True if anything was copied."""
    copied_any = False

    # -- RawData monster overrides
    src = data_dir / "RawData" / "!ModResources" / "overrides" / "Monsters"
    if src.is_dir():
        n = copy_tree(src, RAW_INPUT)
        info(f"raw overrides: copied {n} files from {src}")
        copied_any = True
    elif (RAW_INPUT / "Monsters").is_dir():
        info("raw overrides: source missing, reusing existing raw_game_data/")
    else:
        sys.exit("error: no RawData monster overrides found and no existing copy")

    # -- Addressables bundles (content + monoscripts)
    aa = data_dir / "StreamingAssets" / "aa" / "StandaloneWindows64"
    wanted = []
    if aa.is_dir():
        wanted = sorted(aa.glob("defaultlocalgroup_assets_all_*.bundle")) + \
                 sorted(aa.glob("*_monoscripts_*.bundle"))
    if wanted:
        wipe(UNITY_INPUT)
        for p in wanted:
            shutil.copy2(p, UNITY_INPUT / p.name)
            info(f"bundle: copied {p.name} ({p.stat().st_size / 1e6:.0f} MB)")
        copied_any = True
    elif any(UNITY_INPUT.glob("*.bundle")):
        info("bundles: source missing, reusing existing raw_game_data_unity/")
    else:
        sys.exit("error: no Addressables bundles found and no existing copy")

    # -- target assemblies
    managed = data_dir / "Managed"
    have = [d for d in DLLS if (managed / d).is_file()]
    if have:
        wipe(GAMEFILES)
        GAMEFILES.mkdir(parents=True, exist_ok=True)
        for d in have:
            shutil.copy2(managed / d, GAMEFILES / d)
        info(f"dlls: copied {len(have)} files")
        copied_any = True
    elif all((GAMEFILES / d).is_file() for d in DLLS[:2]):
        info("dlls: source missing, reusing existing ref/gamefiles/managed/")
    else:
        sys.exit("error: Dew.Core.dll / Dew.Contents.dll not found in Managed/")
    return copied_any


def run_decompile() -> None:
    ilspy = os.environ.get("ILSYPCMD") or shutil.which("ilspycmd")
    if not ilspy:
        cand = Path.home() / ".dotnet" / "tools" / "ilspycmd"
        if cand.is_file():
            ilspy = str(cand)
    if not ilspy:
        sys.exit("error: ilspycmd not found; install with "
                 "`dotnet tool install --global ilspycmd` "
                 "(needs a dotnet SDK matching the tool)")
    env = dict(os.environ)
    dotnet_root = Path.home() / ".dotnet"
    if dotnet_root.is_dir():
        env.setdefault("DOTNET_ROOT", str(dotnet_root))
        env["PATH"] = f"{dotnet_root}:{dotnet_root / 'tools'}:{env.get('PATH', '')}"

    for asm in ("Dew.Core", "Dew.Contents"):
        dll = GAMEFILES / f"{asm}.dll"
        if not dll.is_file():
            sys.exit(f"error: {dll} missing")
        out_dir = DECOMPILED / asm
        wipe(out_dir)
        info(f"decompiling {asm} ...")
        subprocess.run(
            [ilspy, "-p", "-o", str(out_dir), "-r", str(GAMEFILES), str(dll)],
            check=True, env=env,
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        n = len(list(out_dir.glob("*.cs")))
        info(f"decompiled {asm}: {n} source files")


def run_step(script: str, interpreter: list[str] | None = None) -> None:
    # relative path + cwd: works for both POSIX and Windows interpreters
    # (a Windows python.exe cannot open /mnt/... style paths)
    cmd = (interpreter or [sys.executable]) + [script]
    info(f"running {script} ...")
    subprocess.run(cmd, check=True, cwd=str(SCRIPT_DIR))


def find_unitypy_interpreter() -> list[str]:
    candidates = [[sys.executable]]
    for name in ("python", "python3", "python.exe"):
        w = shutil.which(name)
        if w and [w] not in candidates:
            candidates.append([w])
    for cand in candidates:
        try:
            r = subprocess.run(
                cand + ["-c", "import UnityPy"],
                capture_output=True, timeout=120)
            if r.returncode == 0:
                info(f"UnityPy interpreter: {cand[0]}")
                return cand
        except Exception:
            continue
    sys.exit("error: no interpreter with UnityPy found; "
             "run `pip install UnityPy` with any Python >= 3.9")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("game_root", nargs="?", help="game install folder")
    parser.add_argument("--skip-decompile", action="store_true",
                        help="reuse existing ref/decompiled/")
    args = parser.parse_args()

    if not args.game_root:
        if ((RAW_INPUT / "Monsters").is_dir()
                and any(UNITY_INPUT.glob("*.bundle"))
                and (GAMEFILES / "Dew.Contents.dll").is_file()):
            info("no game_root given; workspace inputs already present, "
                 "reusing them")
        else:
            sys.exit("error: game_root required (or populate the workspace "
                     "inputs first)")
    else:
        data_dir = find_data_dir(Path(args.game_root))
        info(f"game data dir: {data_dir}")
        sync_inputs(data_dir)

    if args.skip_decompile:
        if not (DECOMPILED / "Dew.Contents").is_dir():
            sys.exit("error: --skip-decompile but ref/decompiled is empty")
        info("decompile: skipped")
    else:
        run_decompile()

    run_step("extract_raw.py")
    run_step("extract_unity_pools.py",
             interpreter=find_unitypy_interpreter())
    run_step("build_db.py")

    out = SCRIPT_DIR / "processed_data" / "out"
    bosses = json_count(out / "_monsters.json", "boss_count")
    info(f"done: {bosses} bosses, JSONs in {out}")
    return 0


def json_count(path: Path, key: str):
    import json
    try:
        return json.loads(path.read_text(encoding="utf-8")).get(key)
    except Exception:
        return "?"


if __name__ == "__main__":
    sys.exit(main())
