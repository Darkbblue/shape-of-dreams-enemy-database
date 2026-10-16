using System;
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
}
