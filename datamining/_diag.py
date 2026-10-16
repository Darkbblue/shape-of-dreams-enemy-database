import UnityPy, pathlib
from collections import Counter
game = pathlib.Path(r"D:/Uploader's/Steam/steamapps/common/Shape of Dreams/Shape of Dreams_Data")

def load_maps(paths):
    m = {}
    for fp in paths:
        env = UnityPy.load(str(fp))
        objs = env.objects
        if hasattr(objs, "values"): objs = list(objs.values())
        for o in objs:
            if o.type.name == "MonoScript":
                try: m[(fp.name, o.path_id)] = o.read().m_ClassName
                except Exception: pass
    return m

env = UnityPy.load(str(game / "level14"))
objs = env.objects
if hasattr(objs, "values"): objs = list(objs.values())
sf = None
for o in objs:
    af = getattr(o, "assets_file", None)
    if af is not None:
        sf = af
        break
exts = [str(e.path).rsplit("/", 1)[-1] for e in (sf.externals or [])]
print("externals:", exts)
fail_pairs = Counter()
for o in objs:
    if o.type.name != "MonoBehaviour": continue
    try: o.read_typetree()
    except Exception:
        t = None
    try:
        import UnityPy
        raw = o.read()
        sp = raw.m_Script
        fid = getattr(sp, "file_id", None)
        pid = getattr(sp, "path_id", None)
        fail_pairs[(fid, pid)] += 1
    except Exception as e:
        fail_pairs[("ERR", str(e)[:40])] += 1
print("fail m_Script pairs:", fail_pairs.most_common(12))
maps = load_maps([game / n for n in exts if n.endswith(".assets")])
resolved = Counter()
for (fid, pid), n in fail_pairs.items():
    if isinstance(fid, int) and 0 < fid <= len(exts):
        cn = maps.get((exts[fid - 1], pid))
        resolved[cn] += n
print("failed objs are:", resolved.most_common(10))
