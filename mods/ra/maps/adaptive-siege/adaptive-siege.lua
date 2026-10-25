--[[
   Copyright (c) The OpenRA Developers and Contributors
   This file is part of OpenRA, which is free software. It is made
   available to you under the terms of the GNU General Public License
   as published by the Free Software Foundation, either version 3 of
   the License, or (at your option) any later version. For more
   information, see COPYING.
]]

-- Adaptive Siege: survive waves of attackers that are put together to counter
-- whatever army the defenders have built.

Settings =
{
	easy = { waves = 10, firstWave = DateTime.Minutes(5), interval = DateTime.Seconds(105), budget = 1500, growth = 500, adaptive = 50 },
	normal = { waves = 15, firstWave = DateTime.Minutes(4), interval = DateTime.Seconds(90), budget = 2500, growth = 800, adaptive = 70 },
	hard = { waves = 20, firstWave = DateTime.Minutes(3), interval = DateTime.Seconds(75), budget = 3500, growth = 1200, adaptive = 90 },
	endless = { waves = 0, firstWave = DateTime.Minutes(4), interval = DateTime.Seconds(90), budget = 2500, growth = 900, adaptive = 80 }
}

-- Defender units grouped by what the attackers should bring against them.
DefenderClasses =
{
	infantry = { "e1", "e2", "e3", "e4", "e6", "e7", "dog", "shok", "spy", "thf", "medi", "mech" },
	armor = { "1tnk", "2tnk", "3tnk", "4tnk", "ttnk", "ctnk", "qtnk", "stnk", "apc", "ftrk", "jeep", "arty", "v2rl", "mgg", "mrj", "dtrk" },
	aircraft = { "heli", "hind", "mh60", "yak", "mig" },
	defenses = { "pbox", "hbox", "gun", "ftur", "tsla", "agun", "sam" }
}
DefenderClassOrder = { "infantry", "armor", "aircraft", "defenses" }

-- Duplicates make a unit more likely to be picked.
CounterUnits =
{
	infantry = { "e1", "e1", "e4", "dog", "shok", "v2rl" },
	armor = { "e3", "e3", "3tnk", "3tnk", "4tnk" },
	aircraft = { "e3", "ftrk", "ftrk" },
	defenses = { "v2rl", "v2rl", "3tnk", "e3" }
}
BaselineUnits = { "e1", "e1", "e3", "3tnk", "apc" }
BossUnit = "4tnk"

AntiAirTypes = { "e3", "ftrk", "agun", "sam" }

MaxUnitsPerWave = 60
MinimumEntryDistance = 30
BossWaveEvery = 5
BomberWaveEvery = 3

Wave = 0
ClearedWaves = 0
NextWaveTicks = 0
EnemyFocus = nil

UnitCosts = { }
CostOf = function(type)
	if not UnitCosts[type] then
		UnitCosts[type] = Actor.Cost(type)
	end

	return UnitCosts[type]
end

DistanceSquared = function(a, b)
	local dx = a.X - b.X
	local dy = a.Y - b.Y
	return dx * dx + dy * dy
end

AliveDefenders = function()
	return Utils.Where(Defenders, function(p) return not Lost[p.InternalName] end)
end

-- Total cost of the defenders' army per class, plus their anti-air value.
AnalyseDefenders = function()
	local values = { infantry = 0, armor = 0, aircraft = 0, defenses = 0 }
	local total = 0
	local antiAir = 0

	Utils.Do(AliveDefenders(), function(player)
		Utils.Do(DefenderClassOrder, function(class)
			Utils.Do(player.GetActorsByTypes(DefenderClasses[class]), function(a)
				local cost = CostOf(a.Type)
				values[class] = values[class] + cost
				total = total + cost
			end)
		end)

		Utils.Do(player.GetActorsByTypes(AntiAirTypes), function(a)
			antiAir = antiAir + CostOf(a.Type)
		end)
	end)

	return values, total, antiAir
end

-- Picks random units from the list until the budget is spent.
SpendBudget = function(units, list, budget)
	local cheapest = CostOf(list[1])
	Utils.Do(list, function(type) cheapest = math.min(cheapest, CostOf(type)) end)

	while budget >= cheapest and #units < MaxUnitsPerWave do
		local type = Utils.Random(list)
		local cost = CostOf(type)
		if cost <= budget then
			units[#units + 1] = type
			budget = budget - cost
		end
	end
end

ComposeWave = function()
	local settings = Settings[Difficulty]
	local defenderCount = #AliveDefenders()
	local budget = (settings.budget + settings.growth * (Wave - 1)) * (100 + 60 * (defenderCount - 1)) / 100
	local values, total, antiAir = AnalyseDefenders()

	local units = { }
	local adaptiveBudget = 0
	EnemyFocus = nil

	if total > 0 then
		adaptiveBudget = budget * settings.adaptive / 100

		local focusValue = 0
		Utils.Do(DefenderClassOrder, function(class)
			SpendBudget(units, CounterUnits[class], adaptiveBudget * values[class] / total)
			if values[class] > focusValue then
				focusValue = values[class]
				EnemyFocus = class
			end
		end)
	end

	SpendBudget(units, BaselineUnits, budget - adaptiveBudget)

	if Wave % BossWaveEvery == 0 then
		for i = 1, Wave / BossWaveEvery do
			units[#units + 1] = BossUnit
		end
	end

	return units, total, antiAir
end

-- Entry points far away from every defender base. Falls back to the
-- entry that is furthest away from the closest defender.
ValidEntries = function()
	local defenders = AliveDefenders()
	local minimum = MinimumEntryDistance * MinimumEntryDistance
	local best = nil
	local bestDistance = -1

	local valid = Utils.Where(Entries, function(entry)
		local closest = nil
		Utils.Do(defenders, function(player)
			local d = DistanceSquared(entry.Location, player.HomeLocation)
			if not closest or d < closest then
				closest = d
			end
		end)

		local structures = Map.ActorsInCircle(entry.CenterPosition, WDist.FromCells(12), function(a)
			return a.Owner ~= Horde and a.Owner ~= Neutral and a.HasProperty("StartBuildingRepairs")
		end)

		if closest and closest > bestDistance and #structures == 0 then
			best = entry
			bestDistance = closest
		end

		return closest and closest >= minimum and #structures == 0
	end)

	if #valid == 0 then
		return { best or Utils.Random(Entries) }
	end

	return valid
end

SendGroup = function(entry, types, target)
	return Reinforcements.Reinforce(Horde, types, { entry.Location }, 15, function(a)
		if not a.HasProperty("AttackMove") then
			a.Hunt()
			return
		end

		a.AttackMove(target, 3)
		Trigger.OnIdle(a, function(unit)
			if unit.IsInWorld then
				unit.Hunt()
			end
		end)
	end)
end

RandomDefenderStructure = function()
	local structures = { }
	Utils.Do(AliveDefenders(), function(player)
		Utils.Do(player.GetActors(), function(a)
			if a.HasProperty("StartBuildingRepairs") then
				structures[#structures + 1] = a
			end
		end)
	end)

	if #structures == 0 then
		return nil
	end

	return Utils.Random(structures)
end

SendWave = function()
	Wave = Wave + 1
	local units, armyValue, antiAir = ComposeWave()
	local entries = ValidEntries()
	local groupCount = math.min(#entries, 1 + math.floor(Wave / 4), 3)
	local defenders = AliveDefenders()

	local groups = { }
	for i = 1, groupCount do
		groups[i] = { }
	end

	for i, type in ipairs(units) do
		local group = groups[(i - 1) % groupCount + 1]
		group[#group + 1] = type
	end

	local waveActors = { }
	entries = Utils.Shuffle(entries)
	for i, group in ipairs(groups) do
		local entry = entries[(i - 1) % #entries + 1]
		local target = Utils.Random(defenders).HomeLocation
		Utils.Do(SendGroup(entry, group, target), function(a)
			waveActors[#waveActors + 1] = a
		end)
	end

	local waveNumber = Wave
	Trigger.OnAllKilled(waveActors, function()
		WaveCleared(waveNumber)
	end)

	Utils.Do(defenders, function(player)
		Media.PlaySpeechNotification(player, "EnemyUnitsApproaching")
	end)

	local focus = EnemyFocus and UserInterface.GetFluentMessage("class-" .. EnemyFocus) or UserInterface.GetFluentMessage("class-none")
	Media.DisplayMessage(UserInterface.GetFluentMessage("wave-incoming", { ["wave"] = tostring(Wave), ["count"] = tostring(#units), ["focus"] = focus }))

	if Wave % BossWaveEvery == 0 then
		local structure = RandomDefenderStructure()
		if structure then
			Media.DisplayMessage(UserInterface.GetFluentMessage("boss-wave"))
			local aircraft = ParatroopersProxy.TargetParatroopers(structure.CenterPosition)
			Utils.Do(aircraft, function(a)
				Trigger.OnPassengerExited(a, function(_, passenger)
					passenger.Hunt()
				end)
			end)
		end
	end

	-- Punish defenders that ignore anti-air.
	if Wave >= 4 and Wave % BomberWaveEvery == 0 and armyValue > 0 and antiAir * 10 < armyValue then
		local structure = RandomDefenderStructure()
		if structure then
			Media.DisplayMessage(UserInterface.GetFluentMessage("no-anti-air"))
			ParabombsProxy.TargetAirstrike(structure.CenterPosition)
		end
	end

	if Settings[Difficulty].waves == 0 or Wave < Settings[Difficulty].waves then
		NextWaveTicks = Settings[Difficulty].interval
	else
		NextWaveTicks = -1
	end
end

WaveCleared = function(waveNumber)
	ClearedWaves = ClearedWaves + 1

	local reward = 300 + 100 * waveNumber
	Utils.Do(AliveDefenders(), function(player)
		player.Cash = player.Cash + reward
	end)

	Media.DisplayMessage(UserInterface.GetFluentMessage("wave-cleared", { ["wave"] = tostring(waveNumber), ["reward"] = tostring(reward) }))

	local total = Settings[Difficulty].waves
	if total > 0 and ClearedWaves >= total then
		Utils.Do(AliveDefenders(), function(player)
			player.MarkCompletedObjective(SurviveObjectives[player.InternalName])
		end)
	end
end

UpdateMissionText = function()
	local total = Settings[Difficulty].waves
	local waves = total > 0 and UserInterface.GetFluentMessage("wave-progress", { ["wave"] = tostring(Wave), ["total"] = tostring(total) })
		or UserInterface.GetFluentMessage("wave-progress-endless", { ["wave"] = tostring(Wave) })

	local text
	if NextWaveTicks > 0 then
		text = UserInterface.GetFluentMessage("next-wave-in", { ["waves"] = waves, ["time"] = Utils.FormatTime(NextWaveTicks) })
	else
		text = UserInterface.GetFluentMessage("final-wave", { ["waves"] = waves })
	end

	UserInterface.SetMissionText(text)
end

CheckDefenders = function()
	Utils.Do(Defenders, function(player)
		if not Lost[player.InternalName] and player.HasNoRequiredUnits() then
			Lost[player.InternalName] = true
			player.MarkFailedObjective(SurviveObjectives[player.InternalName])
		end
	end)
end

Tick = function()
	if NextWaveTicks > 0 then
		NextWaveTicks = NextWaveTicks - 1
		if NextWaveTicks == 0 then
			SendWave()
		end
	end

	if DateTime.GameTime % DateTime.Seconds(1) == 0 then
		CheckDefenders()
		UpdateMissionText()
	end
end

WorldLoaded = function()
	Horde = Player.GetPlayer("Horde")
	Neutral = Player.GetPlayer("Neutral")
	Entries = { Entry1, Entry2, Entry3, Entry4, Entry5, Entry6, Entry7, Entry8, Entry9, Entry10, Entry11, Entry12 }

	Defenders = Player.GetPlayers(function(p) return p.InternalName == "Multi0" or p.InternalName == "Multi1" end)
	Lost = { }
	SurviveObjectives = { }
	Utils.Do(Defenders, function(player)
		InitObjectives(player)
		SurviveObjectives[player.InternalName] = player.AddPrimaryObjective("survive-all-waves")
	end)

	ParatroopersProxy = Actor.Create("powerproxy.paratroopers", false, { Owner = Horde })
	ParabombsProxy = Actor.Create("powerproxy.parabombs", false, { Owner = Horde })

	NextWaveTicks = Settings[Difficulty].firstWave
	Media.DisplayMessage(UserInterface.GetFluentMessage("adaptive-siege-intro"))
end
