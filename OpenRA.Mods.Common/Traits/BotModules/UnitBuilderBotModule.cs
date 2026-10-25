#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using OpenRA.Mods.Common.Warheads;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[TraitLocation(SystemActors.Player)]
	[Desc("Controls AI unit production.")]
	public class UnitBuilderBotModuleInfo : ConditionalTraitInfo
	{
		// TODO: Investigate whether this might the (or at least one) reason why bots occasionally get into a state of doing nothing.
		// Reason: If this is less than SquadSize, the bot might get stuck between not producing more units due to this,
		// but also not creating squads since there aren't enough idle units.
		[Desc("If > 0, only produce units as long as there are less than this amount of units idling inside the base.",
			"Beware: if it is less than squad size, e.g. the `SquadSize` from `SquadManagerBotModule`, " +
			"the bot might get stuck as there aren't enough idle units to create squad.")]
		public readonly int IdleBaseUnitsMaximum = -1;

		[Desc("Production queues AI uses for producing units.")]
		public readonly ImmutableArray<string> UnitQueues = ["Vehicle", "Infantry", "Plane", "Ship", "Aircraft"];

		[Desc("What units to the AI should build.", "What relative share of the total army must be this type of unit.")]
		public readonly FrozenDictionary<string, int> UnitsToBuild = null;

		[Desc("What units should the AI have a maximum limit to train.")]
		public readonly FrozenDictionary<string, int> UnitLimits = null;

		[Desc("When should the AI start train specific units.")]
		public readonly FrozenDictionary<string, int> UnitDelays = null;

		[Desc("Only queue construction of a new unit when above this requirement.")]
		public readonly int ProductionMinCashRequirement = 500;

		[Desc("How strongly (in percent) the AI adapts its unit choice to counter the enemy units it has seen.",
			"Units that deal more damage to the observed enemy armor and target types get a bigger share of `UnitsToBuild`.",
			"0 disables this and keeps the plain `UnitsToBuild` shares.")]
		public readonly int CounterWeight = 0;

		[Desc("Lower limit (in percent) of how much countering may scale down the share of a unit.")]
		public readonly int MinimumCounterShareModifier = 10;

		[Desc("Upper limit (in percent) of how much countering may scale up the share of a unit.")]
		public readonly int MaximumCounterShareModifier = 300;

		public override object Create(ActorInitializer init) { return new UnitBuilderBotModule(init.Self, this); }
	}

	public class UnitBuilderBotModule : ConditionalTrait<UnitBuilderBotModuleInfo>,
		IBotTick, IBotNotifyIdleBaseUnits, IBotRequestUnitProduction, IGameSaveTraitData, INotifyActorDisposing
	{
		public const int FeedbackTime = 30; // ticks; = a bit over 1s. must be >= netlag.

		readonly World world;
		readonly Player player;

		readonly List<string> queuedBuildRequests = [];
		readonly ActorIndex.OwnerAndNames unitsToBuild;

		// Enemy combat actors this bot has actually seen, used to pick counter units.
		readonly HashSet<Actor> knownEnemies = [];

		IBotRequestPauseUnitProduction[] requestPause;
		int idleUnitCount;
		int currentQueueIndex = 0;
		PlayerResources playerResources;

		int ticks;

		public UnitBuilderBotModule(Actor self, UnitBuilderBotModuleInfo info)
			: base(info)
		{
			world = self.World;
			player = self.Owner;
			unitsToBuild = new ActorIndex.OwnerAndNames(world, info.UnitsToBuild.Keys, player);
		}

		protected override void Created(Actor self)
		{
			requestPause = self.Owner.PlayerActor.TraitsImplementing<IBotRequestPauseUnitProduction>().ToArray();
			playerResources = self.Owner.PlayerActor.Trait<PlayerResources>();
		}

		void IBotNotifyIdleBaseUnits.UpdatedIdleBaseUnits(List<Actor> idleUnits)
		{
			idleUnitCount = idleUnits.Count;
		}

		void IBotTick.BotTick(IBot bot)
		{
			// PERF: We shouldn't be queueing new units when we're low on cash
			if (playerResources.GetCashAndResources() < Info.ProductionMinCashRequirement || requestPause.Any(rp => rp.PauseUnitProduction))
				return;

			ticks++;

			if (ticks % FeedbackTime == 0)
			{
				ILookup<string, ProductionQueue> queuesByCategory = null;

				var buildRequest = queuedBuildRequests.FirstOrDefault();
				if (buildRequest != null)
				{
					queuesByCategory ??= AIUtils.FindQueuesByCategory(player);
					BuildUnit(bot, buildRequest, queuesByCategory);
					queuedBuildRequests.Remove(buildRequest);
				}

				if (Info.IdleBaseUnitsMaximum <= 0 || Info.IdleBaseUnitsMaximum > idleUnitCount)
				{
					queuesByCategory ??= AIUtils.FindQueuesByCategory(player);
					for (var i = 0; i < Info.UnitQueues.Length; i++)
					{
						if (++currentQueueIndex >= Info.UnitQueues.Length)
							currentQueueIndex = 0;

						var category = Info.UnitQueues[currentQueueIndex];
						var queues = queuesByCategory[category].ToArray();
						if (queues.Length != 0)
						{
							// PERF: We tick only one type of valid queue at a time
							// if AI gets enough cash, it can fill all of its queues with enough ticks
							BuildRandomUnit(bot, queues);
							break;
						}
					}
				}
			}
		}

		void IBotRequestUnitProduction.RequestUnitProduction(IBot bot, string requestedActor)
		{
			queuedBuildRequests.Add(requestedActor);
		}

		int IBotRequestUnitProduction.RequestedProductionCount(IBot bot, string requestedActor)
		{
			return queuedBuildRequests.Count(r => r == requestedActor);
		}

		void BuildRandomUnit(IBot bot, ProductionQueue[] queues)
		{
			if (Info.UnitsToBuild.Count == 0)
				return;

			// Pick a free queue
			var queue = queues.FirstOrDefault(q => !q.AllQueued().Any());
			if (queue == null)
				return;

			var unit = ChooseRandomUnitToBuild(queue);

			if (unit == null)
				return;

			bot.QueueOrder(Order.StartProduction(queue.Actor, unit.Name, 1));
		}

		// In cases where we want to build a specific unit but don't know the queue name (because there's more than one possibility)
		void BuildUnit(IBot bot, string name, ILookup<string, ProductionQueue> queuesByCategory)
		{
			var actorInfo = world.Map.Rules.Actors[name];
			if (actorInfo == null)
				return;

			var buildableInfo = actorInfo.TraitInfoOrDefault<BuildableInfo>();
			if (buildableInfo == null)
				return;

			ProductionQueue queue = null;
			foreach (var pq in buildableInfo.Queue)
			{
				queue = queuesByCategory[pq].FirstOrDefault(q => !q.AllQueued().Any());
				if (queue != null)
					break;
			}

			if (queue != null)
			{
				bot.QueueOrder(Order.StartProduction(queue.Actor, name, 1));
				AIUtils.BotDebug("{0} decided to build {1} (external request)", queue.Actor.Owner, name);
			}
		}

		ActorInfo ChooseRandomUnitToBuild(ProductionQueue queue)
		{
			var buildableThings = queue.BuildableItems().Shuffle(world.LocalRandom).ToArray();
			if (buildableThings.Length == 0)
				return null;

			var allUnits = unitsToBuild.Actors.Where(a => !a.IsDead).ToArray();

			if (Info.CounterWeight > 0)
			{
				var counterUnit = ChooseCounterUnitToBuild(buildableThings, allUnits);
				if (counterUnit != null)
					return counterUnit;
			}

			ActorInfo desiredUnit = null;
			var desiredError = int.MaxValue;
			foreach (var unit in buildableThings)
			{
				if (!Info.UnitsToBuild.TryGetValue(unit.Name, out var share) ||
					(Info.UnitDelays != null && Info.UnitDelays.TryGetValue(unit.Name, out var delay) && delay > world.WorldTick))
					continue;

				var unitCount = allUnits.Count(a => a.Info.Name == unit.Name);
				if (Info.UnitLimits != null && Info.UnitLimits.TryGetValue(unit.Name, out var count) && unitCount >= count)
					continue;

				var error = allUnits.Length > 0 ? unitCount * 100 / allUnits.Length - share : -1;
				if (error < 0)
					return HasAdequateAirUnitReloadBuildings(unit) ? unit : null;

				if (error < desiredError)
				{
					desiredError = error;
					desiredUnit = unit;
				}
			}

			return desiredUnit != null ? (HasAdequateAirUnitReloadBuildings(desiredUnit) ? desiredUnit : null) : null;
		}

		// Returns null when there is no known enemy army to counter, so the caller can fall back to the plain shares.
		ActorInfo ChooseCounterUnitToBuild(ActorInfo[] buildableThings, Actor[] allUnits)
		{
			var enemyProfiles = UpdateKnownEnemies();
			if (enemyProfiles.Count == 0)
				return null;

			var candidates = new List<(ActorInfo Unit, int Share, int Count, int Effectiveness)>();
			foreach (var unit in buildableThings)
			{
				if (!Info.UnitsToBuild.TryGetValue(unit.Name, out var share) || share <= 0 ||
					(Info.UnitDelays != null && Info.UnitDelays.TryGetValue(unit.Name, out var delay) && delay > world.WorldTick))
					continue;

				var unitCount = allUnits.Count(a => a.Info.Name == unit.Name);
				if (Info.UnitLimits != null && Info.UnitLimits.TryGetValue(unit.Name, out var count) && unitCount >= count)
					continue;

				if (!HasAdequateAirUnitReloadBuildings(unit))
					continue;

				candidates.Add((unit, share, unitCount, EffectivenessAgainst(unit, enemyProfiles)));
			}

			// Units without damaging weapons (harvesters, engineers, medics, ...) keep their plain share
			// and are not taken into account for the average effectiveness.
			var combatEffectiveness = candidates.Where(c => c.Effectiveness >= 0).Select(c => c.Effectiveness).ToArray();
			if (combatEffectiveness.Length == 0)
				return null;

			var averageEffectiveness = combatEffectiveness.Sum() / combatEffectiveness.Length;
			if (averageEffectiveness <= 0)
				return null;

			var adjustedShares = candidates.Select(c =>
			{
				if (c.Effectiveness < 0)
					return c.Share;

				var relativeEffectiveness = c.Effectiveness * 100 / averageEffectiveness;
				var modifier = (100 + Info.CounterWeight * (relativeEffectiveness - 100) / 100)
					.Clamp(Info.MinimumCounterShareModifier, Info.MaximumCounterShareModifier);

				return c.Share * modifier / 100;
			}).ToArray();

			var totalShare = adjustedShares.Sum();
			var totalCount = candidates.Sum(c => c.Count);
			if (totalShare <= 0)
				return null;

			// Pick the unit that is furthest below its adjusted share once one more unit has been built.
			// buildableThings is shuffled, so ties are broken randomly.
			var bestIndex = -1;
			var bestDeficit = long.MinValue;
			for (var i = 0; i < candidates.Count; i++)
			{
				var deficit = (long)adjustedShares[i] * (totalCount + 1) - (long)candidates[i].Count * totalShare;
				if (deficit > bestDeficit)
				{
					bestDeficit = deficit;
					bestIndex = i;
				}
			}

			if (bestIndex < 0)
				return null;

			var (chosenUnit, chosenShare, _, chosenEffectiveness) = candidates[bestIndex];
			AIUtils.BotDebug("{0} decided to build {1}: counter share {2} (base {3}, effectiveness {4}% vs average {5}%)",
				player, chosenUnit.Name, adjustedShares[bestIndex], chosenShare, chosenEffectiveness, averageEffectiveness);

			return chosenUnit;
		}

		// Remembers every enemy combat actor the bot can currently see and returns
		// the remembered army as total cost per (actor type, target types).
		Dictionary<(ActorInfo Info, BitSet<TargetableType> TargetTypes), int> UpdateKnownEnemies()
		{
			knownEnemies.RemoveWhere(a => a.IsDead || !a.IsInWorld || player.RelationshipWith(a.Owner) != PlayerRelationship.Enemy);

			foreach (var a in world.ActorsHavingTrait<Armament>())
			{
				if (a.IsDead || !a.IsInWorld || player.RelationshipWith(a.Owner) != PlayerRelationship.Enemy ||
					a.Info.HasTraitInfo<HuskInfo>() || !a.CanBeViewedByPlayer(player))
					continue;

				knownEnemies.Add(a);
			}

			var profiles = new Dictionary<(ActorInfo Info, BitSet<TargetableType> TargetTypes), int>();
			foreach (var a in knownEnemies)
			{
				var targetTypes = a.GetEnabledTargetTypes();
				if (targetTypes.IsEmpty)
					continue;

				var key = (a.Info, targetTypes);
				var cost = a.Info.TraitInfoOrDefault<ValuedInfo>()?.Cost ?? 0;
				profiles[key] = profiles.GetValueOrDefault(key) + Math.Max(cost, 1);
			}

			return profiles;
		}

		// Cost-weighted average of how well the unit's best weapon suits the enemy army, from 0 to 100.
		// Versus values are relative to each warhead's own base damage, so they are normalized by the
		// warhead's best Versus value to make different units comparable. Returns -1 for units that have no damaging weapon.
		static int EffectivenessAgainst(ActorInfo unit, Dictionary<(ActorInfo Info, BitSet<TargetableType> TargetTypes), int> enemyProfiles)
		{
			var damageWarheads = unit.TraitInfos<ArmamentInfo>()
				.Where(a => a.WeaponInfo != null)
				.SelectMany(a => a.WeaponInfo.Warheads.OfType<DamageWarhead>()
					.Where(w => w.Damage > 0)
					.Select(w => (a.WeaponInfo, Warhead: w, BestVersus: w.Versus.Values.Append(100).Max())))
				.ToArray();

			if (damageWarheads.Length == 0)
				return -1;

			long totalWeight = 0;
			long totalEffectiveness = 0;
			foreach (var (enemy, weight) in enemyProfiles)
			{
				var armorTypes = enemy.Info.TraitInfos<ArmorInfo>().Where(a => a.Type != null).Select(a => a.Type).ToArray();
				var best = 0;
				foreach (var (weapon, warhead, bestVersus) in damageWarheads)
				{
					if (!weapon.IsValidTarget(enemy.TargetTypes) ||
						!warhead.ValidTargets.Overlaps(enemy.TargetTypes) || warhead.InvalidTargets.Overlaps(enemy.TargetTypes))
						continue;

					var versus = Util.ApplyPercentageModifiers(100, armorTypes.Where(warhead.Versus.ContainsKey).Select(t => warhead.Versus[t]));
					best = Math.Max(best, versus * 100 / bestVersus);
				}

				totalWeight += weight;
				totalEffectiveness += (long)best * weight;
			}

			return totalWeight > 0 ? (int)(totalEffectiveness / totalWeight) : -1;
		}

		// For mods like RA (number of RearmActors must match the number of aircraft)
		bool HasAdequateAirUnitReloadBuildings(ActorInfo actorInfo)
		{
			var aircraftInfo = actorInfo.TraitInfoOrDefault<AircraftInfo>();
			if (aircraftInfo == null)
				return true;

			// If actor isn't Rearmable, it doesn't need a RearmActor to reload
			var rearmableInfo = actorInfo.TraitInfoOrDefault<RearmableInfo>();
			if (rearmableInfo == null)
				return true;

			var countOwnAir = AIUtils.CountActorsWithNameAndTrait<IPositionable>(actorInfo.Name, player);
			var countBuildings = rearmableInfo.RearmActors.Sum(b => AIUtils.CountActorsWithNameAndTrait<Building>(b, player));
			if (countOwnAir >= countBuildings)
				return false;

			return true;
		}

		List<MiniYamlNode> IGameSaveTraitData.IssueTraitData(Actor self)
		{
			if (IsTraitDisabled)
				return null;

			return
			[
				new("QueuedBuildRequests", FieldSaver.FormatValue(queuedBuildRequests.ToArray())),
				new("IdleUnitCount", FieldSaver.FormatValue(idleUnitCount))
			];
		}

		void IGameSaveTraitData.ResolveTraitData(Actor self, MiniYaml data)
		{
			if (self.World.IsReplay)
				return;

			var queuedBuildRequestsNode = data.NodeWithKeyOrDefault("QueuedBuildRequests");
			if (queuedBuildRequestsNode != null)
			{
				queuedBuildRequests.Clear();
				queuedBuildRequests.AddRange(FieldLoader.GetValue<ImmutableArray<string>>("QueuedBuildRequests", queuedBuildRequestsNode.Value.Value));
			}

			var idleUnitCountNode = data.NodeWithKeyOrDefault("IdleUnitCount");
			if (idleUnitCountNode != null)
				idleUnitCount = FieldLoader.GetValue<int>("IdleUnitCount", idleUnitCountNode.Value.Value);
		}

		void INotifyActorDisposing.Disposing(Actor self)
		{
			unitsToBuild.Dispose();
		}
	}
}
