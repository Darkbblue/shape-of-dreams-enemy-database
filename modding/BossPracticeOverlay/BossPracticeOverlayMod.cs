using System;
using System.Collections.Generic;
using UnityEngine;

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
		_hudBoss = boss.GetOriginalName();
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

	private static Dictionary<string, List<string>> _bossRoomIndex;
	private static List<string> _bossRoster;
	private static Dictionary<string, Monster> _bossPrefabs;

	private JumpState _jumpState;

	private string _jumpBossName;

	private bool _jumpHard;

	private float _jumpDeadline;

	private bool _variantMarked;

	private StatBonus _lockBonus;

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
		if (_jumpState != JumpState.Idle)
		{
			Debug.Log($"{Tag} JumpToBoss: another jump is in progress, ignoring.");
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
		instance.isAlteringGameplay = true;
		ApplyLock();
		_jumpBossName = text2;
		_jumpHard = flag;
		ZoneManager zoneManager = NetworkedManagerBase<ZoneManager>.instance;
		if (zoneManager == null)
		{
			HudMessage($"{Tag} JumpToBoss: not in a game.");
			return;
		}
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
		_bossRoomIndex.TryGetValue(_jumpBossName, out List<string> value);
		if (value == null || value.Count == 0)
		{
			HudMessage($"{Tag} JumpToBoss: no boss scene found for {_jumpBossName}, spawning it in the current room...");
			MarkVariant(_jumpHard);
			bool flag = SpawnBossDirect();
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
		_jumpDeadline = Time.unscaledTime + 10f;
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
			if (FindLiveBoss(_jumpBossName) != null)
			{
				FinishJump(ok: true, "boss scene ready");
			}
			else if (Time.unscaledTime > _jumpDeadline)
			{
				HudMessage($"{Tag} JumpToBoss: boss did not spawn on its own, spawning it directly...");
				bool flag = SpawnBossDirect();
				FinishJump(flag, flag ? "spawned by fallback" : "fallback spawn failed");
			}
			break;
		}
	}

	private void FinishJump(bool ok, string note)
	{
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

	private bool SpawnBossDirect()
	{
		if (FindLiveBoss(_jumpBossName) != null)
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

	private void PrintBossList()
	{
		Debug.Log($"{Tag} Boss list ({_bossRoster.Count}) - usage: JumpToBoss <name> or <name>(hard):");
		foreach (string boss in _bossRoster)
		{
			string text = ((_bossRoomIndex.TryGetValue(boss, out List<string> value) && value.Count > 0) ? string.Join("/", value.GetRange(0, Math.Min(2, value.Count))) + (value.Count > 2 ? "/..." : "") : "no-scene (direct spawn)");
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
			Debug.Log($"{Tag} debug: rooms-in-resources={num} (expected 0: rooms are standalone scenes, not assets), zones={zoneCount}, roomScenes={roomScenes.Count}");
			List<Zone> list2 = new List<Zone>();
			foreach (Zone allByType2 in DewResources.FindAllByType<Zone>())
			{
				if (!(allByType2 == null))
				{
					list2.Add(allByType2);
					if (list2.Count >= 3)
					{
						break;
					}
				}
			}
			foreach (Zone item in list2)
			{
				Debug.Log($"{Tag} debug: zone {item.name} bossRooms=[{string.Join(", ", item.bossRooms)}]");
			}
			Debug.Log($"{Tag} debug: first room scenes: {string.Join(", ", list.GetRange(0, Math.Min(15, list.Count)))}");
			foreach (string boss in _bossRoster)
			{
				string text = ((_bossRoomIndex.TryGetValue(boss, out List<string> value) && value.Count > 0) ? string.Join(", ", value) : "NONE (direct spawn fallback)");
				Debug.Log($"{Tag} debug: {boss} -> {text}");
			}
			HudMessage($"{Tag} diagnostics written to the log ({roomScenes.Count} scenes, {_bossRoomIndex.Count}/{_bossRoster.Count} bosses mapped).");
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
		foreach (Zone allByType in DewResources.FindAllByType<Zone>())
		{
			if (allByType == null)
			{
				continue;
			}
			zoneCount++;
			CollectNames(hashSet, allByType.rooms);
			CollectNames(hashSet, allByType.combatRooms);
			CollectNames(hashSet, allByType.startRooms);
			CollectNames(hashSet, allByType.bossRooms);
			CollectNames(hashSet, allByType.shopRooms);
			CollectNames(hashSet, allByType.eventRooms);
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

	private static void EnsureBossIndex()
	{
		if (_bossRoster != null)
		{
			return;
		}
		_bossRoster = new List<string>();
		_bossPrefabs = new Dictionary<string, Monster>(StringComparer.OrdinalIgnoreCase);
		_bossRoomIndex = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
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
		// DewNetworkManager.LoadSceneAsync(node.room) — ZoneManager.cs:1436), so there is
		// nothing to scan via FindAllByType<Room>(). Boss->scene association is derived at
		// runtime from the zone room-name tables plus a name match (strip "Mon_" prefix).
		int zoneCount = 0;
		HashSet<string> hashSet = CollectRoomScenes(ref zoneCount);
		foreach (string boss in _bossRoster)
		{
			string text = ((boss.Length > 4 && boss.StartsWith("Mon_", StringComparison.OrdinalIgnoreCase)) ? boss.Substring(4) : boss);
			List<string> list = new List<string>();
			foreach (string item in hashSet)
			{
				if (item.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					list.Add(item);
				}
			}
			if (list.Count > 0)
			{
				list.Sort(StringComparer.OrdinalIgnoreCase);
				_bossRoomIndex[boss] = list;
			}
		}
		Debug.Log($"{Tag} boss index ready: {_bossRoster.Count} bosses, {zoneCount} zones, {hashSet.Count} room scenes, {_bossRoomIndex.Count} bosses with a matched scene");
	}
}
