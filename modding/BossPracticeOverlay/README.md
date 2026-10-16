# Boss Practice Overlay (Shape of Dreams)

练习用 overlay mod：自动跟踪当前英雄首领（`Monster.type == Boss`），在**屏幕左上角 HUD** 常驻显示两行——
第一行 boss 名、第二行最近一次出招（`At_X#N`）——所有名字使用资源内部名，与
`datamining/processed_data/out/` 的 boss 数据库逐字段互查。另提供 `JumpToBoss` 一键传送练习（见下文）。

不做 Harmony patch。基础招式显示零玩法改动；`JumpToBoss` 属玩法改动，使用时会按官方 CoC 置 `isAlteringGameplay`。

## 画面显示（HUD）

- 左上角两行：`Mon_X` / `At_Y#N`（困难变体显示 `Mon_X(hard)`；boss 未出招前第二行显示 `-`）；无 boss 时自动隐藏。
- 下方临时消息行：跳转/锁血/错误反馈，8 秒后消失。
- 日志（F1 查看器）只保留低频事件（boss 行、跳转流程、锁血数值）；出招不再写日志。
- 提示：若 F1 日志查看器影响操作，可在设置里把控制台模式切为 **ShownWithoutPopup**
  （隐藏日志查看器，`` ` `` 键仍可开控制台输入命令）。

## JumpToBoss 快速练习（控制台命令）

按 **`** 打开控制台：

| 命令 | 行为 |
|------|------|
| `JumpToBossList` | **查询指令**：列出全部 boss（困难变体列为独立条目 `<Mon_X>(hard)`）及运行时发现的对应场景 |
| `JumpToBoss Mon_Ink_BossWhiteNight` | 传送到其 boss 场景（普通形态）+ 开锁血 |
| `JumpToBoss Mon_Ink_BossWhiteNight(hard)` | 同上，困难变体 |
| `JumpToBossDebug` | 诊断：打印 zone 清单、房间场景名全集、每个 boss 的场景匹配结果 |
| `BuildBossSceneMap` | **一次性生成精确映射**：**主菜单**执行（局内会拒绝），additive 逐个加载 boss 场景读取出怪池后卸载，几分钟内写出 `about/boss_scenes.json` |

注意：游戏内控制台不支持省略有默认值的参数，`JumpToBoss` 必须带参数；查询请用 `JumpToBossList`。

**场景映射的三级来源**（boss 身份不在场景名里——`Room_DarkCave_Boss_0` 是通用命名）：

1. **精确映射**：`about/boss_scenes.json`（由 `BuildBossSceneMap` 生成，或离线脚本
   `datamining/extract_room_boss_map.py`）——首跳自动加载，覆盖全部 14 boss 含 Special 系。
2. **biome 猜测**（无映射文件时的兜底）：`Mon_DarkCave_BossX` → 场景名含 `Room_DarkCave_Boss`。
   常规 boss 有效，**Special 系隐藏 boss 无效**（命名不含 biome）。
3. 传送后场景就位时读取真实出怪规则池（`RoomMonsters.defaultRule` + section 覆盖），
   **过滤成只剩目标 boss**（原始列表快照、跳转结束即还原），让场景自己刷出正确的
   boss——Ink 双 boss 也由此精确区分。池里无目标 boss 时提示可强刷。

首次使用请先在主菜单跑一次 `BuildBossSceneMap`。

其他行为细节：

- **变体强制**：加载前把当前区 index 写入/移出 `hardVariantBossZoneIndices`，boss 出生后移除并回读确认。
- **锁血（"看得见的锁血"）**：血量上限补至 ≥2000 + 护甲 +70 + 回血 +150/s，伤害照常结算
  （受击特效、掉血、回复全部可见）。绑定英雄实体，**退出一局自动消失**，无需手动关闭。
- 传送后**不自动刷 boss**（boss 场景多有休整区，定时刷新会造成双 boss）：120s 内未检测到目标
  boss 会提示放弃等待；此时**再执行一次 `JumpToBoss <同名>`** 即强制在房间内直接生成
  （难度缩放自动生效）。换目标直接输入新名字即可，会自动打断当前跳转。
- 所有跳转/生成均在 server 侧（单机练习即 host）；连续跳转支持（自动退出当前侧厅再进新侧厅）。
- 首次使用任意跳转后 `isAlteringGameplay = true`（对局大厅显示 MOD 标记，直到游戏进程重启）。

## 日志格式 ↔ 数据库对照

Boss 行（进入 boss 房 / 更换跟踪目标时输出一次，常驻到下一事件）：

```
[SOD] Boss: Mon_Ink_BossWhiteNight (diff=diffNormal, variant=base, zoneHard=False, netId=12345) [entity.name=Mon_Ink_BossWhiteNight(Clone)]
```

| 字段 | 含义 | 数据库对应 |
|------|------|-----------|
| `Mon_X` | boss 资源内部名（`GetOriginalName()`） | `out/<Mon_X>/` 目录名、JSON `boss` |
| `diff=` | 难度资产名 | 文件名 `<difficulty>.<variant>.json`（diffTutorial/diffEasy/diffNormal/diffHard/diffNightmare/diffLimbo） |
| `variant=` | 招式池选择：`base` / `hard_variant` / 无池为 `none` | 文件名 variant 段 |
| `zoneHard=` | `ZoneManager.isCurrentZoneHardVariant` 交叉验证 | — |
| `netId=` | `persistentNetId`（双 boss 房固定跟踪最小者） | — |
| `[entity.name=…]` | 原始 `Object.name` 兜底（核对命名契约） | — |

出招行已改为 HUD 显示（见上文"画面显示"），第二行格式 `At_X#N`：

| 字段 | 含义 | 数据库对应 |
|------|------|-----------|
| `At_X` | 招式 trigger 资源名 | `moves[].name` |
| `#N` | config 序号 | `moves[].config_index` |

其他行为：

- 无 boss / 杂兵战：HUD 隐藏。离开游戏或 boss 死亡后临时消息 `[SOD] no boss (…)`。
- 双 boss 房：跟踪 `persistentNetId` 最小者；其死亡后自动切换到另一只。
- 招式池中途变化（正常不会发生）会输出 `Boss pool changed` 消息。
- 全部轮询/事件包 try/catch：异常提示一次并停用自身，绝不上抛。

## 构建（游戏 PC，Windows）

需要 .NET SDK 8+（`dotnet --version`）。首次 build 会从 NuGet 拉
`Microsoft.NETFramework.ReferenceAssemblies`。

```bat
cd modding\BossPracticeOverlay
dotnet build -c Release -p:GameManaged="C:\Program Files (x86)\Steam\steamapps\common\Shape of Dreams\Shape of Dreams_Data\Managed"
```

目标框架是 **net48**：游戏自带程序集（如 0Harmony 2.3.6）按 .NET Framework 4.8 构建。
若把 csproj 改回 net472 会得到 MSB3274/MSB3275 警告并连带 CS0246（Dew.Core 解析失败）。

产物：`bin\Release\net48\BossPracticeOverlay.dll`，且 post-build 会把 DLL 复制到工程根目录
（`about/` 旁边），因此整个 `BossPracticeOverlay/` 文件夹本身就是可安装的 mod 目录。

> 只安装用**真实游戏 Managed 目录**构建出的 DLL。`tools/verify-local.sh`（见下）会在本机
> 用 stub 程序集做编译验证，其产物 **不可安装**（stub 的 `Debug.Log` 是空实现，装了也不输出）。

## 本机编译验证（无游戏目录时，WSL / Git Bash）

`tools/verify-local.sh` 用两份最小 stub（`UnityEngine.CoreModule`、`Mirror`）+ 仓库内的
`ref/gamefiles/managed/Dew.Core.dll`、`Dew.Contents.dll` 做一次完整类型检查：

```bash
modding/tools/verify-local.sh
```

两项检查：
1. `tools/build-check/`（check 工程）——mod 源码 × 真实 Dew DLL，专抓 API 名字/签名改动；
2. 用 stub 拼出的 fake Managed 跑通真实 csproj 的引用管线（跑完即清理产物）。

游戏版本更新后先跑这个，能提前发现改名字段。

## 安装

把 `modding/BossPracticeOverlay/` 整个文件夹（`BossPracticeOverlay.dll` + `about/`；csproj、
README 等其余文件对加载无影响，可留可删）复制为：

```
<游戏目录>/Mods/BossPracticeOverlay/
├── BossPracticeOverlay.dll      ← 真实 Managed 构建产物
└── about/
    ├── metadata.json
    └── description.txt
```

游戏内：**游戏性设置 → 开启开发者模式**，标题界面 Mods 管理器确认已加载；进游戏按 **F1**
打开日志查看器（` 键开控制台）。spawn boss（控制台命令或正常推进）即可看到输出。

## 实现要点（反编译已核对）

- 入口 `ModBehaviour`（`Dew.Core.dll`），DLL 经 `about/metadata.json` 的 `assemblies` 注入。
- boss 检测：`ActorManager.instance.allEntities` 过滤 `Monster` 且
  `type == MonsterType.Boss`（枚举 Lesser/Normal/MiniBoss/Boss；MiniBoss 天然排除）。
- 出招事件：`Entity.EntityEvent_OnCastComplete : SafeAction<EventInfoCast>`，事件直接给出
  `trigger`/`configIndex`/`instance`，无需动画反查；实体销毁时 SafeAction 自动 Clear，
  跟踪切换/OnDestroy 时显式 `-=` 退订。
- 变体：`monster.currentPoolIndex`（0=base，1=hard_variant，`SelectAbilityPool()` 写入的
  运行时值）；`abilityPools.Count <= 1` 视为无变体。
- 难度：`NetworkedManagerBase<GameManager>.instance.difficulty.name`（ScriptableObject 资产名，
  与 DB 文件名前缀一致）。

## 已知坑位（继承自 mod-notes.md）

- `At_Mon_Special_BossMaw_Catacylsm` 是游戏原生 typo，勿"顺手修正"。
- `Mon_SnowMountain_BossSkoll_Capture` 反编译无施法点（疑似预制体事件驱动）——它是否触发
  `OnCastComplete` 是待实测悬案；若实战中该招式不出日志，属于已知例外。
- 多人客户端（非 host）不执行 server 出招路径，只看得到 Boss 行看不到 Cast 行；练习场景
  本机即 host，不受影响。
