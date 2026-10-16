using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

// Boss Practice Overlay — shows the tracked boss + last cast as a minimal top-left HUD,
// and logs sparse diagnostics to the F1 viewer.
// Naming contract (must stay in sync with datamining/processed_data/out/<Mon_X>/<diff>.<variant>.json):
//   boss     -> "boss" key / folder name           (Monster.GetOriginalName())
//   trigger  -> moves[].name + moves[].config_index (EventInfoCast.trigger.GetOriginalName() + #configIndex)
//   diff     -> difficulty asset name               (GameManager.difficulty.name)
//   variant  -> "base" / "hard_variant" / "none"    (Monster.abilityPools.Count / currentPoolIndex)
public class BossPracticeOverlayMod : ModBehaviour
{
	private const string Tag = "[SOD]";

	private const float PollInterval = 0.25f;

	private Monster _tracked;

	private int _lastPoolIndex = int.MinValue;

	private float _nextPollAt;

	private bool _pollDisabled;

	private string _hudBoss = "";

	private string _hudCast = "";

	private string _hudMsg = "";

	private float _hudMsgUntil;

	private void Update()
	{
		if (_pollDisabled || Time.unscaledTime < _nextPollAt)
		{
			return;
		}
		_nextPollAt = Time.unscaledTime + PollInterval;
		try
		{
			Poll();
		}
		catch (Exception arg)
		{
			_pollDisabled = true;
			HudMessage($"{Tag} overlay polling disabled after error: {arg}");
		}
	}

	private void OnDestroy()
	{
		StopTracking(null);
	}

	private void HudMessage(string msg)
	{
		_hudMsg = msg;
		_hudMsgUntil = Time.unscaledTime + 8f;
		Debug.Log(msg);
	}

	private void OnGUI()
	{
		float num = 8f;
		if (!string.IsNullOrEmpty(_hudBoss))
		{
			DrawLabel(12f, num, _hudBoss, 24, Color.white);
			num += 36f;
			DrawLabel(12f, num, _hudCast, 24, Color.white);
			num += 36f;
		}
		if (!string.IsNullOrEmpty(_hudMsg) && Time.unscaledTime < _hudMsgUntil)
		{
			DrawLabel(12f, num, _hudMsg, 17, new Color(1f, 0.92f, 0.6f, 1f));
		}
	}

	private static void DrawLabel(float x, float y, string text, int fontSize, Color color)
	{
		GUIStyle val = new GUIStyle(GUI.skin.label);
		val.fontSize = fontSize;
		GUI.color = new Color(0f, 0f, 0f, 0.85f);
		GUI.Label(new Rect(x + 2f, y + 2f, 2000f, (float)fontSize + 10f), text, val);
		GUI.color = color;
		GUI.Label(new Rect(x, y, 2000f, (float)fontSize + 10f), text, val);
		GUI.color = Color.white;
	}

	private void Poll()
	{
		if (_jumpState != JumpState.Idle)
		{
			AdvanceJump();
		}
		ActorManager actorManager = ActorManager.instance;
		if (actorManager == null)
		{
			if (_tracked != null)
			{
				StopTracking($"{Tag} no boss (left game)");
			}
			return;
		}
		Monster monster = null;
		foreach (Entity allEntity in actorManager.allEntities)
		{
			if (!(allEntity is Monster monster2) || monster2.IsNullOrInactive() || monster2.type != Monster.MonsterType.Boss)
			{
				continue;
			}
			if (monster == null || monster2.persistentNetId < monster.persistentNetId)
			{
				monster = monster2;
			}
		}
		if (monster == null)
		{
			if (_tracked != null)
			{
				StopTracking($"{Tag} no boss (boss gone)");
			}
			return;
		}
		if (ReferenceEquals(monster, _tracked))
		{
			if (monster.abilityPools.Count > 1 && monster.currentPoolIndex != _lastPoolIndex)
			{
				_lastPoolIndex = monster.currentPoolIndex;
				_hudBoss = HudBossName(monster);
				HudMessage($"{Tag} Boss pool changed: {monster.GetOriginalName()} now variant={VariantKey(monster)}");
			}
			return;
		}
		StopTracking(null);
		StartTracking(monster);
	}

	private void StartTracking(Monster boss)
	{
		_tracked = boss;
		_lastPoolIndex = boss.currentPoolIndex;
		_hudBoss = HudBossName(boss);
		_hudCast = "-";
		try
		{
			boss.EntityEvent_OnCastComplete -= OnCast;
			boss.EntityEvent_OnCastComplete += OnCast;
		}
		catch (Exception arg)
		{
			HudMessage($"{Tag} subscribe failed: {arg}");
		}
		string text = DifficultyKey();
		string text2 = VariantKey(boss);
		bool flag = ZoneHardFlag();
		Debug.Log($"{Tag} Boss: {boss.GetOriginalName()} (diff={text}, variant={text2}, zoneHard={flag}, netId={boss.persistentNetId}) [entity.name={boss.name}]");
	}

	private void StopTracking(string reason)
	{
		if (_tracked != null)
		{
			try
			{
				_tracked.EntityEvent_OnCastComplete -= OnCast;
			}
			catch (Exception arg)
			{
				HudMessage($"{Tag} unsubscribe failed: {arg}");
			}
			_tracked = null;
			_hudBoss = "";
			_hudCast = "";
		}
		if (reason != null)
		{
			HudMessage(reason);
		}
	}

	private void OnCast(EventInfoCast cast)
	{
		if (_tracked == null)
		{
			return;
		}
		try
		{
			string text = ((cast.trigger != null) ? cast.trigger.GetOriginalName() : "<null>");
			_hudCast = $"{text}#{cast.configIndex}";
		}
		catch (Exception)
		{
		}
	}

	private static string DifficultyKey()
	{
		GameManager gameManager = NetworkedManagerBase<GameManager>.instance;
		if (gameManager == null)
		{
			return "?";
		}
		DewDifficultySettings difficulty = gameManager.difficulty;
		if (difficulty == null)
		{
			return "?";
		}
		return difficulty.name;
	}

	private static string VariantKey(Monster boss)
	{
		int count = boss.abilityPools.Count;
		if (count <= 1)
		{
			return "none";
		}
		if (boss.currentPoolIndex != 1)
		{
			return "base";
		}
		return "hard_variant";
	}

	private static string HudBossName(Monster boss)
	{
		if (VariantKey(boss) == "hard_variant")
		{
			return boss.GetOriginalName() + "(hard)";
		}
		return boss.GetOriginalName();
	}

	private static bool ZoneHardFlag()
	{
		ZoneManager zoneManager = NetworkedManagerBase<ZoneManager>.instance;
		if (zoneManager == null)
		{
			return false;
		}
		return zoneManager.isCurrentZoneHardVariant;
	}

	// ==================== JumpToBoss (practice teleport) ====================
	// See modding/BOSSJUMP_PLAN.md. Runtime-discovered boss<->room mapping, sidetrack
	// room travel with direct-spawn fallback, and a "visible" practice lock (HP pool +
	// armor + fast regen) that lives on the hero entity and dies with the run.

	private enum JumpState
	{
		Idle,
		WaitTransition,
		WaitBoss
	}

	private static HashSet<string> _bossScenes;

	private static int _sceneZoneCount;

	private static Dictionary<string, Monster> _bossPrefabs;

	private static List<string> _bossRoster;

	private static Dictionary<string, List<string>> _exactBossScenes;

	private static bool _exactBossSceneMapLoaded;

	[Serializable]
	private class BossSceneMapFile
	{
#pragma warning disable 649
		public int schema;

		public Dictionary<string, List<string>> boss_scenes;

		public Dictionary<string, List<string>> scene_bosses;
#pragma warning restore 649
	}

	private JumpState _jumpState;

	private string _jumpBossName;

	private bool _jumpHard;

	private float _jumpDeadline;

	private bool _variantMarked;

	private bool _poolsAttempted;

	private List<PoolSnapshot> _filteredPools;

	private Coroutine _scanRoutine;

	private string _scanCurrent;

	private StatBonus _lockBonus;

	private class PoolSnapshot
	{
		public MonsterPool pool;

		public List<MonsterPool.SpawnRuleEntry> original;
	}

	[ConsoleCommand("List all practice bosses. The query command for boss practice.")]
	private void JumpToBossList()
	{
		HandleJumpCommand(null);
	}

	// NOTE: the in-game console does not accept omitting optional parameters, so bossName
	// is a required argument here; use JumpToBossList for the zero-argument listing.
	[ConsoleCommand("Jump to a boss room with practice lock. Usage: JumpToBoss <Mon_X> OR JumpToBoss <Mon_X>(hard)")]
	private void JumpToBoss(string bossName)
	{
		try
		{
			HandleJumpCommand(bossName);
		}
		catch (Exception arg)
		{
			HudMessage($"{Tag} JumpToBoss failed: {arg}");
		}
	}

	private void HandleJumpCommand(string bossName)
	{
		EnsureBossIndex();
		if (string.IsNullOrWhiteSpace(bossName))
		{
			PrintBossList();
			return;
		}
		if (!MakeSureServer())
		{
			return;
		}
		bool flag = false;
		string text = bossName.Trim();
		if (text.EndsWith("(hard)", StringComparison.OrdinalIgnoreCase))
		{
			flag = true;
			text = text.Substring(0, text.Length - "(hard)".Length).Trim();
		}
		string text2 = ResolveBossName(text);
		if (text2 == null)
		{
			HudMessage($"{Tag} JumpToBoss: no boss matches '{bossName}'. Run JumpToBossList to list.");
			return;
		}
		Monster monster = _bossPrefabs[text2];
		if (flag && monster.abilityPools.Count <= 1)
		{
			HudMessage($"{Tag} JumpToBoss: {text2} has no hard variant.");
			return;
		}
		ZoneManager zoneManager = NetworkedManagerBase<ZoneManager>.instance;
		if (zoneManager == null)
		{
			HudMessage($"{Tag} JumpToBoss: not in a game.");
			return;
		}
		if (_jumpState != JumpState.Idle)
		{
			if (_jumpState == JumpState.WaitBoss && string.Equals(_jumpBossName, text2, StringComparison.OrdinalIgnoreCase))
			{
				Debug.Log($"{Tag} JumpToBoss: repeated while waiting -> force-spawning {_jumpBossName}...");
				bool flag2 = SpawnBossDirect(force: true);
				FinishJump(flag2, flag2 ? "force-spawned" : "force-spawn failed");
			}
			else
			{
				AbortJump("superseded by a new JumpToBoss");
				StartJump(text2, flag, zoneManager);
			}
			return;
		}
		StartJump(text2, flag, zoneManager);
	}

	private void StartJump(string bossName, bool hard, ZoneManager zoneManager)
	{
		instance.isAlteringGameplay = true;
		ApplyLock();
		_jumpBossName = bossName;
		_jumpHard = hard;
		_poolsAttempted = false;
		_filteredPools = new List<PoolSnapshot>();
		if (zoneManager.isSidetracking)
		{
			HudMessage($"{Tag} JumpToBoss: leaving current sidetrack room...");
			zoneManager.ReturnFromSidetracking();
			_jumpState = JumpState.WaitTransition;
			_jumpDeadline = Time.unscaledTime + 10f;
		}
		else
		{
			EnterBossRoom();
		}
	}

	private void EnterBossRoom()
	{
		ZoneManager zoneManager = NetworkedManagerBase<ZoneManager>.instance;
		if (zoneManager == null)
		{
			AbortJump("left the game");
			return;
		}
		List<string> value = CandidateScenes(_jumpBossName);
		if (value.Count == 0)
		{
			HudMessage($"{Tag} JumpToBoss: no boss scene found for {_jumpBossName}, spawning it in the current room...");
			MarkVariant(_jumpHard);
			bool flag = SpawnBossDirect(force: false);
			FinishJump(flag, flag ? "spawned in current room" : "direct spawn failed");
			return;
		}
		string text = value[0];
		if (value.Count > 1)
		{
			Debug.Log($"{Tag} JumpToBoss: {_jumpBossName} matches {value.Count} scenes, using {text} (others: {string.Join(", ", value.GetRange(1, Math.Min(3, value.Count - 1)))}{(value.Count > 4 ? ", ..." : "")})");
		}
		MarkVariant(_jumpHard);
		HudMessage($"{Tag} JumpToBoss: loading boss scene {text} (variant={(_jumpHard ? "hard_variant" : "base")})...");
		zoneManager.LoadSidetrackRoom(text);
		_jumpState = JumpState.WaitBoss;
		_jumpDeadline = Time.unscaledTime + 120f;
	}

	private void AdvanceJump()
	{
		switch (_jumpState)
		{
		case JumpState.WaitTransition:
		{
			ZoneManager zoneManager = NetworkedManagerBase<ZoneManager>.instance;
			if (zoneManager == null)
			{
				AbortJump("left the game");
			}
			else if (!zoneManager.isInRoomTransition)
			{
				EnterBossRoom();
			}
			else if (Time.unscaledTime > _jumpDeadline)
			{
				AbortJump("room transition timeout");
			}
			break;
		}
		case JumpState.WaitBoss:
		{
			if (!_poolsAttempted)
			{
				Room room = Room.instance;
				if (room != null)
				{
					_poolsAttempted = true;
					int num3 = FilterRoomPoolsToBoss(room, GetBossPrefab(_jumpBossName));
					if (num3 == 0)
					{
						HudMessage($"{Tag} JumpToBoss: this scene's spawn pool has no {_jumpBossName}; repeat JumpToBoss to force-spawn it here.");
					}
					else
					{
						Debug.Log($"{Tag} JumpToBoss: filtered {num3} spawn pool(s) to {_jumpBossName}; waiting for the scene to spawn it...");
					}
				}
			}
			if (FindLiveBoss(_jumpBossName) != null)
			{
				FinishJump(ok: true, "boss scene ready");
			}
			else if (Time.unscaledTime > _jumpDeadline)
			{
				// No auto-spawn: boss scenes often have a rest area before the arena, and a
				// timed spawn can end up with two bosses when the scene spawns its own.
				AbortJump("no boss detected within 120s; run JumpToBoss again to force-spawn");
			}
			break;
		}
		}
	}

	private void FinishJump(bool ok, string note)
	{
		RestorePools();
		UnmarkVariant();
		_jumpState = JumpState.Idle;
		string arg = (_jumpHard ? "hard_variant" : "base");
		if (ok)
		{
			HudMessage($"{Tag} JumpToBoss: READY - {_jumpBossName} ({arg}) {note}. Lock stays until you leave this run.");
		}
		else
		{
			HudMessage($"{Tag} JumpToBoss: FAILED - {_jumpBossName} ({arg}) {note}.");
		}
	}

	private void AbortJump(string reason)
	{
		RestorePools();
		UnmarkVariant();
		_jumpState = JumpState.Idle;
		HudMessage($"{Tag} JumpToBoss aborted: {reason}");
	}

	private void MarkVariant(bool hard)
	{
		ZoneManager zoneManager = NetworkedManagerBase<ZoneManager>.instance;
		if (zoneManager == null)
		{
			return;
		}
		int currentZoneIndex = zoneManager.currentZoneIndex;
		if (hard)
		{
			if (!zoneManager.hardVariantBossZoneIndices.Contains(currentZoneIndex))
			{
				zoneManager.hardVariantBossZoneIndices.Add(currentZoneIndex);
				_variantMarked = true;
			}
		}
		else
		{
			zoneManager.hardVariantBossZoneIndices.RemoveAll((int i) => i == currentZoneIndex);
		}
	}

	private void UnmarkVariant()
	{
		if (!_variantMarked)
		{
			return;
		}
		_variantMarked = false;
		ZoneManager zoneManager = NetworkedManagerBase<ZoneManager>.instance;
		if (zoneManager != null)
		{
			zoneManager.hardVariantBossZoneIndices.Remove(zoneManager.currentZoneIndex);
		}
	}

	private bool SpawnBossDirect(bool force)
	{
		if (!force && FindLiveBoss(_jumpBossName) != null)
		{
			return true;
		}
		Monster bossPrefab = GetBossPrefab(_jumpBossName);
		if (bossPrefab == null)
		{
			return false;
		}
		DewPlayer local = DewPlayer.local;
		Hero hero = ((local != null) ? local.hero : null);
		Vector3 val = Vector3.zero;
		Room room = Room.instance;
		if (room != null && room.playerPathablePoints != null && room.playerPathablePoints.Count > 0)
		{
			Vector3 val2 = ((hero != null) ? hero.agentPosition : Vector3.zero);
			float num = float.NegativeInfinity;
			foreach (Vector3 playerPathablePoint in room.playerPathablePoints)
			{
				float sqrMagnitude = (playerPathablePoint - val2).sqrMagnitude;
				if (sqrMagnitude > num)
				{
					num = sqrMagnitude;
					val = playerPathablePoint;
				}
			}
		}
		else if (hero != null)
		{
			val = hero.agentPosition;
		}
		ActorManager actorManager = ActorManager.instance;
		GameManager gameManager = NetworkedManagerBase<GameManager>.instance;
		if (actorManager == null || gameManager == null)
		{
			return false;
		}
		Monster monster = Dew.SpawnEntity(bossPrefab, val, Quaternion.identity, actorManager.serverActor, DewPlayer.creep, gameManager.ambientLevel, null);
		return monster != null;
	}

	private Monster FindLiveBoss(string bossName)
		{
			ActorManager actorManager = ActorManager.instance;
			if (actorManager == null)
			{
				return null;
			}
			foreach (Entity allEntity in actorManager.allEntities)
			{
				if (allEntity is Monster monster && !monster.IsNullOrInactive() && monster.type == Monster.MonsterType.Boss && string.Equals(monster.GetOriginalName(), bossName, StringComparison.OrdinalIgnoreCase))
				{
					return monster;
				}
			}
			return null;
		}

		// Boss identity is decided by the loaded scene's spawn rule pools (scene names like
		// Room_DarkCave_Boss_0 are generic). To make the scene spawn OUR boss, keep only the
		// target's entries in the in-memory pool assets; the original lists are snapshotted
		// and restored when the jump finishes so the session stays untouched afterwards.
		private int FilterRoomPoolsToBoss(Room room, Monster target)
		{
			if (target == null || _filteredPools == null)
			{
				return 0;
			}
			int num = 0;
			foreach (MonsterSpawnRule item in CollectLiveRules(room))
			{
				if (item == null || item.pool == null || item.pool.entries == null)
				{
					continue;
				}
				MonsterPool pool = item.pool;
				bool alreadyFiltered = false;
				foreach (PoolSnapshot filteredPool in _filteredPools)
				{
					if (filteredPool.pool == pool)
					{
						alreadyFiltered = true;
						break;
					}
				}
				if (alreadyFiltered)
				{
					continue;
				}
				bool flag = false;
				foreach (MonsterPool.SpawnRuleEntry entry in pool.entries)
				{
					if (entry != null && PoolEntryMatches(entry, target))
					{
						flag = true;
						break;
					}
				}
				if (!flag)
				{
					continue;
				}
				PoolSnapshot poolSnapshot = new PoolSnapshot
				{
					pool = pool,
					original = pool.entries
				};
				List<MonsterPool.SpawnRuleEntry> list = new List<MonsterPool.SpawnRuleEntry>();
				foreach (MonsterPool.SpawnRuleEntry entry2 in pool.entries)
				{
					if (entry2 != null && PoolEntryMatches(entry2, target))
					{
						list.Add(entry2);
					}
				}
				pool.entries = list;
				_filteredPools.Add(poolSnapshot);
				num++;
			}
			return num;
		}

		private void RestorePools()
		{
			if (_filteredPools == null)
			{
				return;
			}
			foreach (PoolSnapshot filteredPool in _filteredPools)
			{
				try
				{
					filteredPool.pool.entries = filteredPool.original;
				}
				catch (Exception ex)
				{
					Debug.LogError($"{Tag} pool restore failed: {ex}");
				}
			}
			_filteredPools.Clear();
		}

		private static bool PoolEntryMatches(MonsterPool.SpawnRuleEntry entry, Monster target)
		{
			try
			{
				return entry.monster.asset == target;
			}
			catch (Exception)
			{
				return false;
			}
		}

		private static List<MonsterSpawnRule> CollectLiveRules(Room room)
		{
			List<MonsterSpawnRule> list = new List<MonsterSpawnRule>();
			if (room.monsters != null && room.monsters.defaultRule != null)
			{
				list.Add(room.monsters.defaultRule);
			}
			Section_Monsters[] componentsInChildren = room.GetComponentsInChildren<Section_Monsters>(true);
			foreach (Section_Monsters section_Monsters in componentsInChildren)
			{
				if (section_Monsters != null && section_Monsters.ruleOverride != null)
				{
					list.Add(section_Monsters.ruleOverride);
				}
			}
			return list;
		}

	// Scene names encode only the biome (Room_<Biome>_Boss_0); boss identity lives inside
	// the scene's spawn pools, so candidates are biome-level and refined after loading —
	// unless an exact offline-extracted map (about/boss_scenes.json, built by
	// datamining/extract_room_boss_map.py) is bundled with the mod, which takes priority.
	private List<string> CandidateScenes(string bossName)
	{
		List<string> list = new List<string>();
		LoadExactBossSceneMap();
		if (_exactBossScenes != null && _exactBossScenes.TryGetValue(bossName, out List<string> exact) && exact.Count > 0)
		{
			return new List<string>(exact);
		}
		if (_bossScenes == null)
		{
			return list;
		}
		string[] array = bossName.Split('_');
		string text = ((array.Length > 1) ? array[1] : bossName);
		string text2 = "";
		for (int i = 0; i < array.Length; i++)
		{
			if (array[i] == "Boss" && i + 1 < array.Length)
			{
				text2 = array[i + 1];
			}
		}
		string item = "Room_" + text + "_Boss";
		foreach (string item2 in _bossScenes)
		{
			if (item2.StartsWith(item, StringComparison.OrdinalIgnoreCase) || (text2.Length > 0 && item2.IndexOf(text2, StringComparison.OrdinalIgnoreCase) >= 0))
			{
				list.Add(item2);
			}
		}
		list.Sort(StringComparer.OrdinalIgnoreCase);
		return list;
	}

	private void LoadExactBossSceneMap()
	{
		if (_exactBossSceneMapLoaded)
		{
			return;
		}
		_exactBossSceneMapLoaded = true;
		try
		{
			string text = Path.Combine(mod.path, "about", "boss_scenes.json");
			if (!File.Exists(text))
			{
				Debug.Log($"{Tag} no boss_scenes.json bundled; boss scene lookup falls back to the biome guess");
				return;
			}
			BossSceneMapFile bossSceneMapFile = DewPersistence.FromJson<BossSceneMapFile>(File.ReadAllText(text));
			if (bossSceneMapFile?.boss_scenes == null || bossSceneMapFile.boss_scenes.Count == 0)
			{
				Debug.Log($"{Tag} boss_scenes.json has no mappings; boss scene lookup falls back to the biome guess");
				return;
			}
			_exactBossScenes = bossSceneMapFile.boss_scenes;
			Debug.Log($"{Tag} loaded boss_scenes.json: {_exactBossScenes.Count} bosses with exact scene mapping");
		}
		catch (Exception arg)
		{
			Debug.LogError($"{Tag} failed to load boss_scenes.json: {arg}");
		}
	}

	private void ApplyLock()
	{
		DewPlayer local = DewPlayer.local;
		Hero hero = ((local != null) ? local.hero : null);
		if (hero == null || hero.IsNullOrInactive())
		{
			HudMessage($"{Tag} practice lock: no living hero found.");
			return;
		}
		EntityStatus status = hero.Status;
		if (_lockBonus != null)
		{
			status.RemoveStatBonus(_lockBonus);
			_lockBonus = null;
		}
		_lockBonus = new StatBonus
		{
			maxHealthFlat = Math.Max(0f, 2000f - status.maxHealth),
			armorFlat = 70f,
			healthRegenFlat = 150f
		};
		status.AddStatBonus(_lockBonus);
		hero.DoHeal(new HealData(2000f), hero);
		Debug.Log($"{Tag} practice lock ON: maxHP={hero.maxHealth:0} armor={status.armor:0} regen=+150/s. Damage and healing stay visible; cleared automatically when you leave this run.");
		HudMessage($"{Tag} practice lock ON (maxHP={hero.maxHealth:0}, armor+70, regen+150/s)");
	}

	// ==================== BuildBossSceneMap (offline map generator) ====================
	// Boss identity lives inside each room scene's spawn pools; scene names are generic
	// (Room_DarkCave_Boss_0) and offline UnityPy parsing of built scene typetrees is
	// unreliable, so this scans the scenes in-engine (additive load -> read pools ->
	// unload) and writes about/boss_scenes.json for CandidateScenes to consume.
	// Run in the MAIN MENU (no networking session allowed).


	[ConsoleCommand("Scan all boss room scenes and write about/boss_scenes.json. Run in MAIN MENU, takes a few minutes.")]
	private void BuildBossSceneMap()
	{
		if (_scanRoutine != null)
		{
			HudMessage($"{Tag} scene scan already running ({_scanCurrent ?? "..."})");
			return;
		}
		if (!MakeSureNotInGame())
		{
			return;
		}
		EnsureBossIndex();
		_scanRoutine = StartCoroutine(ScanRoutine());
	}

	private System.Collections.IEnumerator ScanRoutine()
	{
		yield return null;
		int count = SceneManager.sceneCountInBuildSettings;
		List<string> targets = new List<string>();
		for (int i = 0; i < count; i++)
		{
			string path = SceneUtility.GetScenePathByBuildIndex(i);
			string lower = path.ToLowerInvariant();
			string name = Path.GetFileNameWithoutExtension(path);
			if (string.IsNullOrEmpty(name) || targets.Contains(name))
			{
				continue;
			}
			if ((lower.Contains("/rooms/") && lower.Contains("boss")) || lower.Contains("room_special"))
			{
				targets.Add(name);
			}
		}
		HudMessage($"{Tag} scanning {targets.Count} boss scenes (stay in the menu)...");
		Dictionary<string, List<string>> map = new Dictionary<string, List<string>>();
		Dictionary<string, List<string>> sceneBosses = new Dictionary<string, List<string>>();
		int done = 0;
		foreach (string name in targets)
		{
			_scanCurrent = name;
			List<string> bosses = new List<string>();
			AsyncOperation op = null;
			try
			{
				op = SceneManager.LoadSceneAsync(name, LoadSceneMode.Additive);
			}
			catch (Exception arg2)
			{
				Debug.LogError($"{Tag} failed to load {name}: {arg2}");
			}
			while (op != null && !op.isDone)
			{
				yield return null;
			}
			try
			{
				Scene scene = SceneManager.GetSceneByName(name);
				if (scene.isLoaded)
				{
					GameObject[] rootObjects = scene.GetRootGameObjects();
					foreach (GameObject root in rootObjects)
					{
						RoomMonsters roomMonsters = root.GetComponentInChildren<RoomMonsters>(true);
						if (roomMonsters == null)
						{
							continue;
						}
						foreach (MonsterSpawnRule monsterSpawnRule in CollectLiveRules(roomMonsters))
						{
							if (monsterSpawnRule?.pool?.entries == null)
							{
								continue;
							}
							foreach (MonsterPool.SpawnRuleEntry entry in monsterSpawnRule.pool.entries)
							{
								try
								{
									string typeName = entry.monster.typeName;
									if (typeName != null && _bossPrefabs.ContainsKey(typeName) && !bosses.Contains(typeName))
									{
										bosses.Add(typeName);
									}
								}
								catch (Exception)
								{
								}
							}
						}
					}
				}
			}
			catch (Exception arg3)
			{
				Debug.LogError($"{Tag} failed to read {name}: {arg3}");
			}
			if (bosses.Count > 0)
			{
				sceneBosses[name] = bosses;
				foreach (string boss in bosses)
				{
					if (!map.TryGetValue(boss, out List<string> list))
					{
						list = new List<string>();
						map[boss] = list;
					}
					list.Add(name);
				}
			}
			AsyncOperation unload = SceneManager.UnloadSceneAsync(name);
			while (unload != null && !unload.isDone)
			{
				yield return null;
			}
			done++;
			HudMessage($"{Tag} scanned {done}/{targets.Count}: {name} -> {bosses.Count} boss(es)");
			yield return null;
		}
		_scanCurrent = null;
		try
		{
			BossSceneMapFile bossSceneMapFile = new BossSceneMapFile
			{
				schema = 1,
				boss_scenes = map,
				scene_bosses = sceneBosses
			};
			string path = Path.Combine(mod.path, "about", "boss_scenes.json");
			File.WriteAllText(path, DewPersistence.ToJson(bossSceneMapFile));
			_exactBossSceneMapLoaded = false;
			_exactBossScenes = null;
			HudMessage($"{Tag} boss scene map written: {map.Count}/{_bossRoster.Count} bosses over {sceneBosses.Count} scenes. Ready.");
		}
		catch (Exception arg4)
		{
			HudMessage($"{Tag} writing boss_scenes.json failed: {arg4}");
		}
		_scanRoutine = null;
	}

	private List<MonsterSpawnRule> CollectLiveRules(RoomMonsters roomMonsters)
	{
		List<MonsterSpawnRule> list = new List<MonsterSpawnRule>();
		if (roomMonsters != null && roomMonsters.defaultRule != null)
		{
			list.Add(roomMonsters.defaultRule);
		}
		Section_Monsters[] componentsInChildren = roomMonsters.GetComponentsInChildren<Section_Monsters>(true);
		foreach (Section_Monsters section_Monsters in componentsInChildren)
		{
			if (section_Monsters != null && section_Monsters.ruleOverride != null)
			{
				list.Add(section_Monsters.ruleOverride);
			}
		}
		return list;
	}

	private void PrintBossList()
	{
		Debug.Log($"{Tag} Boss list ({_bossRoster.Count}) - usage: JumpToBoss <name> or <name>(hard):");
		foreach (string boss in _bossRoster)
		{
			List<string> value = CandidateScenes(boss);
			string text = ((value.Count > 0) ? string.Join("/", value.GetRange(0, Math.Min(2, value.Count))) + (value.Count > 2 ? "/..." : "") : "no-scene (direct spawn)");
			if (_bossPrefabs[boss].abilityPools.Count > 1)
			{
				Debug.Log($"{Tag}  {boss}          scenes={text}");
				Debug.Log($"{Tag}  {boss}(hard)    scenes={text}");
			}
			else
			{
				Debug.Log($"{Tag}  {boss}          scenes={text}");
			}
		}
		HudMessage($"{Tag} {_bossRoster.Count} bosses listed in the log; full details in the log viewer.");
	}

	private string ResolveBossName(string input)
	{
		foreach (string boss in _bossRoster)
		{
			if (string.Equals(boss, input, StringComparison.OrdinalIgnoreCase))
			{
				return boss;
			}
		}
		List<string> list = new List<string>();
		foreach (string boss2 in _bossRoster)
		{
			if (boss2.IndexOf(input, StringComparison.OrdinalIgnoreCase) >= 0)
			{
				list.Add(boss2);
			}
		}
		if (list.Count == 1)
		{
			return list[0];
		}
		if (list.Count > 1)
		{
			Debug.LogError($"{Tag} Ambiguous boss '{input}': {string.Join(", ", list)}");
		}
		return null;
	}

	private static Monster GetBossPrefab(string bossName)
	{
		if (_bossPrefabs != null && _bossPrefabs.TryGetValue(bossName, out Monster value))
		{
			return value;
		}
		return null;
	}

	[ConsoleCommand("Print boss discovery diagnostics: zones, room scene names, per-boss scene matches.")]
	private void JumpToBossDebug()
	{
		try
		{
			EnsureBossIndex();
			int num = 0;
			foreach (Room allByType in DewResources.FindAllByType<Room>())
			{
				if (allByType != null)
				{
					num++;
				}
			}
			int zoneCount = 0;
			HashSet<string> roomScenes = CollectRoomScenes(ref zoneCount);
			List<string> list = new List<string>(roomScenes);
			List<Zone> list2 = new List<Zone>();
			foreach (Zone allByNameSubstring in DewResources.FindAllByNameSubstring<Zone>("Zone_"))
			{
				if (!(allByNameSubstring == null))
				{
					list2.Add(allByNameSubstring);
					if (list2.Count >= 3)
					{
						break;
					}
				}
			}
			int roomPrefabs = 0;
			foreach (Room item2 in DewResources.FindAllByNameSubstring<Room>("Room_"))
			{
				if (item2 != null)
				{
					roomPrefabs++;
				}
			}
			Debug.Log($"{Tag} debug: rooms-in-resources={num}, rooms-by-name('Room_')={roomPrefabs} (informational), zones-by-name('Zone_')={zoneCount}, roomScenes={roomScenes.Count}");
			foreach (Zone item in list2)
			{
				Debug.Log($"{Tag} debug: zone {item.name} bossRooms=[{string.Join(", ", item.bossRooms)}]");
			}
			Debug.Log($"{Tag} debug: first room scenes: {string.Join(", ", list.GetRange(0, Math.Min(15, list.Count)))}");
			foreach (string boss in _bossRoster)
			{
				List<string> value = CandidateScenes(boss);
				string text = ((value.Count > 0) ? string.Join(", ", value) : "NONE (direct spawn fallback)");
				Debug.Log($"{Tag} debug: {boss} -> {text}");
			}
			int mapped = 0;
			foreach (string boss in _bossRoster)
			{
				if (CandidateScenes(boss).Count > 0)
				{
					mapped++;
				}
			}
			HudMessage($"{Tag} diagnostics written to the log ({roomScenes.Count} scenes, {mapped}/{_bossRoster.Count} bosses matched by scene name).");
		}
		catch (Exception arg)
		{
			HudMessage($"{Tag} JumpToBossDebug failed: {arg}");
		}
	}

	private static HashSet<string> CollectRoomScenes(ref int zoneCount)
	{
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		zoneCount = 0;
		// Zones are NOT discoverable via FindAllByType (the type index only holds the
		// class-per-asset entries like monsters); the game itself discovers them by name:
		// DewResources.FindAllByNameSubstring<Zone>("Zone_") — ZoneManager.cs:3563.
		foreach (Zone zoneByName in DewResources.FindAllByNameSubstring<Zone>("Zone_"))
		{
			if (zoneByName == null)
			{
				continue;
			}
			zoneCount++;
			CollectNames(hashSet, zoneByName.rooms);
			CollectNames(hashSet, zoneByName.combatRooms);
			CollectNames(hashSet, zoneByName.startRooms);
			CollectNames(hashSet, zoneByName.bossRooms);
			CollectNames(hashSet, zoneByName.shopRooms);
			CollectNames(hashSet, zoneByName.eventRooms);
		}
		return hashSet;
	}

	private static void CollectNames(HashSet<string> into, List<string> names)
	{
		if (names == null)
		{
			return;
		}
		foreach (string item in names)
		{
			if (!string.IsNullOrEmpty(item))
			{
				into.Add(item);
			}
		}
	}

	private void EnsureBossIndex()
	{
		if (_bossRoster != null)
		{
			return;
		}
		LoadExactBossSceneMap();
		_bossRoster = new List<string>();
		_bossPrefabs = new Dictionary<string, Monster>(StringComparer.OrdinalIgnoreCase);
		foreach (Monster allByType in DewResources.FindAllByType<Monster>())
		{
			if (allByType == null || allByType.type != Monster.MonsterType.Boss)
			{
				continue;
			}
			string originalName = allByType.GetOriginalName();
			if (!_bossPrefabs.ContainsKey(originalName))
			{
				_bossPrefabs[originalName] = allByType;
				_bossRoster.Add(originalName);
			}
		}
		_bossRoster.Sort(StringComparer.OrdinalIgnoreCase);
		// Rooms are standalone scenes, not DewResources assets (LoadNode calls
		// DewNetworkManager.LoadSceneAsync(node.room) — ZoneManager.cs:1436), and zones are
		// not in the FindAllByType type index either; the game discovers them by name
		// (FindAllByNameSubstring<Zone>("Zone_") — ZoneManager.cs:3563).
		_bossScenes = CollectRoomScenes(ref _sceneZoneCount);
		Debug.Log($"{Tag} boss index ready: {_bossRoster.Count} bosses, {_sceneZoneCount} zones, {_bossScenes.Count} room scenes");
	}
}
