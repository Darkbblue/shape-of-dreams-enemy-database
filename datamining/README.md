# Shape of Dreams — Boss 招式数据库（datamining/）

从游戏文件提取每个 Boss 在每个难度 × 变体组合下的出招数据（冷却已按难度
预计算、触发条件为反编译代码内联数值后的伪代码）。

## 一键运行

```bash
python3 datamining/run_all.py "<游戏根目录>"
# <游戏根目录> = 含 ShapeOfDreams_Data 的目录（Steam 安装目录）
# 已有工作区输入时可省略参数（断点续跑）；--skip-decompile 跳过反编译
```

产物：`processed_data/out/<Mon_X>/<difficulty>.<variant>.json`
（`_monsters.json` 全 roster、`_difficulty.json` 难度表为全局索引）。

## 最小输入依赖（实证结论，见 git 历史）

| 工作区路径 | 游戏侧来源 | 用途 |
|---|---|---|
| `raw_game_data/` | `RawData/!ModResources/overrides/Monsters/`（732 JSON, 2.1MB） | 开发者调参补丁 |
| `raw_game_data_unity/` | `StreamingAssets/aa/StandaloneWindows64/` 下仅 2 个 bundle：`defaultlocalgroup_assets_all_*.bundle`（1.9GB, 内容总包, typetree 内嵌）+ `*_monoscripts_*.bundle`（0.1MB, 类名解析） | 预制体：abilityPools/type 枚举/触发器默认值 |
| `ref/gamefiles/managed/` | `Managed/Dew.Core.dll(.pdb)` + `Dew.Contents.dll(.pdb)` | 反编译目标（零引用反编译输出与全集一致，已实证） |
| `ref/decompiled/` | 生成物 | build_db/cast_logic 消费 |

不需要：`resources.assets`、`globalgamemanagers.assets`、`sharedassets0.assets`、
`catalog.bin`、`Assembly-CSharp.dll`（内容为渲染杂项）、其余 23 个引用 DLL、
TypeTreeGenerator（bundle 自带 typetree）。

## 环境要求

- **反编译**：dotnet SDK + `dotnet tool install --global ilspycmd`
  （ilspycmd ≥10 只发 net10 资产；.NET 8 装不上时升级 SDK，两者可并存）
- **bundle 抽取**：任一装有 **UnityPy** 的 Python ≥3.9。
  本机注意：WSL1（内核 4.4）与新版 conda Python 不兼容（`socket()` ENOENT），
  使用 Windows Python 3.10 + `pip install UnityPy`；run_all.py 会自动探测。
- **其余步骤**：纯标准库，任意 Python 3.8+。

## 脚本清单

| 脚本 | 作用 |
|---|---|
| `run_all.py` | 总控：复制输入 → 反编译 → 三步处理 |
| `extract_raw.py` | RawData 补丁 → `processed_data/raw/`（原样镜像 + 索引） |
| `extract_unity_pools.py` | bundle → `processed_data/unity_dump/`（怪物/难度/触发器预制体） |
| `build_db.py` | 四路合并 → `processed_data/out/`（人读格式，见下） |
| `cast_logic.py` | （被 build_db 调用）反编译 AI 类 → 每招式 if 链伪代码 |
| `probe_unity.py` | 诊断工具：探测 Unity 资产文件内容（版本升级/换包时先用它） |

## 产物格式要点

- 每文件对应一个难度 × 变体组合，自包含：stats 已按难度调整、冷却为最终值
- `moves[]` 单列表：`selection.mode` ∈ `pool`（order=优先级, chance, hp_window）/
  `basic_attack` / `reactive` / `ai_class`；`cast_logic` 为内联数值的伪代码
- 伪代码记号：`ready<T>()` 冷却就绪、`in_range<T>()` 在射程、`CAST<T>()` 施放、
  `rand` 0..1、`dt` 帧间隔、`hp_ratio` 血量比例
- 中间产物（raw/ unity_dump/）为机器格式，仅供复查与再生成
