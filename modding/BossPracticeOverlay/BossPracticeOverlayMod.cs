using System;
using System.Collections.Generic;
using UnityEngine;

// Boss Practice Overlay — logs tracked boss + every cast to the F1 log viewer.
// Naming contract (must stay in sync with datamining/processed_data/out/<Mon_X>/<diff>.<variant>.json):
//   boss     -> "boss" key / folder name           (Monster.GetOriginalName())
//   trigger  -> moves[].name + moves[].config_index (EventInfoCast.trigger.GetOriginalName() + #configIndex)
//   abi      -> moves[].instance                    (EventInfoCast.instance.GetOriginalName())
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

	private bool _castErrorLogged;

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
			Debug.LogError($"{Tag} overlay polling disabled after error: {arg}");
		}
	}

	private void OnDestroy()
	{
		StopTracking(null);
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
				Debug.Log($"{Tag} Boss pool changed: {monster.GetOriginalName()} now variant={VariantKey(monster)}");
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
		try
		{
			boss.EntityEvent_OnCastComplete -= OnCast;
			boss.EntityEvent_OnCastComplete += OnCast;
		}
		catch (Exception arg)
		{
			Debug.LogError($"{Tag} subscribe failed: {arg}");
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
				Debug.LogError($"{Tag} unsubscribe failed: {arg}");
			}
			_tracked = null;
		}
		if (reason != null)
		{
			Debug.Log(reason);
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
			string text2 = ((cast.instance != null) ? cast.instance.GetOriginalName() : "<null>");
			string text3 = "<none>";
			float num = 0f;
			float num2 = 0f;
			EntityAnimation animation = _tracked.Animation;
			if (animation != null)
			{
				EntityAnimation.AbilityAnimationStatus abilityAnimationStatus = animation.abilityAnimStatus;
				if (abilityAnimationStatus.isPlaying && abilityAnimationStatus.currentClip != null)
				{
					text3 = abilityAnimationStatus.currentClip.resourceId;
					num = abilityAnimationStatus.elapsedTime;
					num2 = abilityAnimationStatus.duration;
				}
			}
			float num3 = ((cast.trigger != null) ? cast.trigger.GetCooldownTimeMultiplier(cast.configIndex) : 1f);
			string text4 = null;
			if (cast.trigger != null && cast.trigger.configs != null && cast.configIndex >= 0 && cast.configIndex < cast.trigger.configs.Length)
			{
				TriggerConfig triggerConfig = cast.trigger.configs[cast.configIndex];
				if (triggerConfig != null)
				{
					if (triggerConfig.lockCastUntilKilled && triggerConfig.lockCooldownUntilKilled)
					{
						text4 = "lock_cast+lock_cd";
					}
					else if (triggerConfig.lockCastUntilKilled)
					{
						text4 = "lock_cast";
					}
					else if (triggerConfig.lockCooldownUntilKilled)
					{
						text4 = "lock_cd";
					}
				}
			}
			string text5 = ((text4 != null) ? $" locks={text4}" : "");
			Debug.Log($"{Tag} Cast: {text}#{cast.configIndex} abi={text2} anim={text3} ({num:0.00}s/{num2:0.00}s) cdMul={num3:0.###}{text5}");
		}
		catch (Exception arg2)
		{
			if (!_castErrorLogged)
			{
				_castErrorLogged = true;
				Debug.LogError($"{Tag} OnCast logging failed (suppressing repeats): {arg2}");
			}
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

	[ConsoleCommand("List all practice bosses. Same as JumpToBoss without arguments.")]
	private void JumpToBossList()
	{
		JumpToBoss(null);
	}

	[ConsoleCommand("Jump to a boss room with practice lock. Usage: JumpToBoss OR JumpToBoss <Mon_X> OR JumpToBoss <Mon_X>(hard)")]
	private void JumpToBoss(string bossName = null)
	{
		try
		{
			HandleJumpCommand(bossName);
		}
		catch (Exception arg)
		{
			Debug.LogError($"{Tag} JumpToBoss failed: {arg}");
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
			Debug.LogError($"{Tag} JumpToBoss: no boss matches '{bossName}'. Run JumpToBoss without arguments to list.");
			return;
		}
		Monster monster = _bossPrefabs[text2];
		if (flag && monster.abilityPools.Count <= 1)
		{
			Debug.LogError($"{Tag} JumpToBoss: {text2} has no hard variant.");
			return;
		}
		instance.isAlteringGameplay = true;
		ApplyLock();
		_jumpBossName = text2;
		_jumpHard = flag;
		ZoneManager zoneManager = NetworkedManagerBase<ZoneManager>.instance;
		if (zoneManager == null)
		{
			Debug.LogError($"{Tag} JumpToBoss: not in a game.");
			return;
		}
		if (zoneManager.isSidetracking)
		{
			Debug.Log($"{Tag} JumpToBoss: leaving current sidetrack room...");
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
			Debug.Log($"{Tag} JumpToBoss: no boss room found for {_jumpBossName}, spawning it in the current room...");
			MarkVariant(_jumpHard);
			bool flag = SpawnBossDirect();
			FinishJump(flag, flag ? "spawned in current room" : "direct spawn failed");
			return;
		}
		string text = value[0];
		if (value.Count > 1)
		{
			Debug.Log($"{Tag} JumpToBoss: {_jumpBossName} matches {value.Count} rooms, using {text} (others: {string.Join(", ", value.GetRange(1, Math.Min(3, value.Count - 1)))}{(value.Count > 4 ? ", ..." : "")})");
		}
		MarkVariant(_jumpHard);
		Debug.Log($"{Tag} JumpToBoss: loading boss room {text} (variant={(_jumpHard ? "hard_variant" : "base")})...");
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
				FinishJump(ok: true, "boss room ready");
			}
			else if (Time.unscaledTime > _jumpDeadline)
			{
				Debug.Log($"{Tag} JumpToBoss: boss did not spawn on its own, spawning it directly...");
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
			Debug.Log($"{Tag} JumpToBoss: READY - {_jumpBossName} ({arg}) {note}. Practice lock stays until you leave this run.");
		}
		else
		{
			Debug.LogError($"{Tag} JumpToBoss: FAILED - {_jumpBossName} ({arg}) {note}.");
		}
	}

	private void AbortJump(string reason)
	{
		UnmarkVariant();
		_jumpState = JumpState.Idle;
		Debug.LogError($"{Tag} JumpToBoss aborted: {reason}");
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
			Debug.LogError($"{Tag} practice lock: no living hero found.");
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
	}

	private void PrintBossList()
	{
		Debug.Log($"{Tag} Boss list ({_bossRoster.Count}) - usage: JumpToBoss <name> or <name>(hard):");
		foreach (string boss in _bossRoster)
		{
			string text = ((_bossRoomIndex.TryGetValue(boss, out List<string> value) && value.Count > 0) ? string.Join("/", value.GetRange(0, Math.Min(2, value.Count))) + (value.Count > 2 ? "/..." : "") : "no-room (direct spawn)");
			if (_bossPrefabs[boss].abilityPools.Count > 1)
			{
				Debug.Log($"{Tag}  {boss}          rooms={text}");
				Debug.Log($"{Tag}  {boss}(hard)    rooms={text}");
			}
			else
			{
				Debug.Log($"{Tag}  {boss}          rooms={text}");
			}
		}
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
		int num = 0;
		int num2 = 0;
		foreach (Room allByType2 in DewResources.FindAllByType<Room>())
		{
			if (allByType2 == null)
			{
				continue;
			}
			num++;
			string name = allByType2.name;
			foreach (MonsterSpawnRule item in CollectRoomRules(allByType2))
			{
				if (item == null || item.pool == null || item.pool.entries == null)
				{
					continue;
				}
				num2++;
				foreach (MonsterPool.SpawnRuleEntry entry in item.pool.entries)
				{
					try
					{
						if (entry == null)
						{
							continue;
						}
						Monster monster = entry.monster.asset;
						if (monster == null || monster.type != Monster.MonsterType.Boss)
						{
							continue;
						}
						string originalName2 = monster.GetOriginalName();
						if (!_bossRoomIndex.TryGetValue(originalName2, out List<string> value2))
						{
							value2 = new List<string>();
							_bossRoomIndex[originalName2] = value2;
						}
						if (!value2.Contains(name))
						{
							value2.Add(name);
						}
					}
					catch (Exception)
					{
					}
				}
			}
		}
		Debug.Log($"{Tag} boss index ready: {_bossRoster.Count} bosses, {num} rooms, {num2} pooled spawn rules");
	}

	private static List<MonsterSpawnRule> CollectRoomRules(Room room)
	{
		List<MonsterSpawnRule> list = new List<MonsterSpawnRule>();
		RoomMonsters roomMonsters = room.GetComponent<RoomMonsters>();
		if (roomMonsters != null && roomMonsters.defaultRule != null)
		{
			list.Add(roomMonsters.defaultRule);
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
}
