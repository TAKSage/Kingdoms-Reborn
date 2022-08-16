﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using DarienEngine;
using DarienEngine.Clustering;
using DarienEngine.AI;

public class AIPlayer : MonoBehaviour
{
    private UnitCategories currentNeed;
    private List<UnitCategories> allCurrentNeeds;
    public PlayerNumbers playerNumber;
    public Transform playerStartPosition;
    public TeamNumbers teamNumber;
    public Factions playerFaction;
    public InventoryAI inventory;
    public NeedInfo needInfo;
    // limit 3 armies at a time
    public List<Army> _Armies = new List<Army>();
    public float timeSinceLastArmyOrders = 0;
    public float armyOrdersDelay = 100.0f;

    public void Init(InventoryAI inv)
    {
        inventory = inv;
        // @TODO: move or spawn monarch or sole builder to start position
    }

    void Update()
    {
        // Build units until limit reached
        if (inventory.totalUnits.Count < inventory.unitLimit)
        {
            // Determine what we should be building now
            needInfo = DetermineNeedState();
            // Order builders and factories to construct appropriate units
            IssueFactoryOrders(inventory.GetUnitsByTypes(UnitCategories.FactoryTier1, UnitCategories.FactoryTier2));
            IssueBuilderOrders(inventory.GetUnitsByTypes(UnitCategories.BuilderTier1, UnitCategories.BuilderTier2));
        }
        // Handle sending armies when they are ready
        IssueArmyOrders();
    }

    // Give orders to builders
    private void IssueBuilderOrders(List<RTSUnit> builders)
    {
        foreach (BaseUnitAI builder in builders)
        {
            BuilderAI builderAI = builder.gameObject.GetComponent<BuilderAI>();
            // Only queue builders who are not in a roaming interval, and not in a build routine with at least 1 unit in the queue
            if (!builderAI.isInRoamInterval && !builderAI.isBuilding && !builderAI.baseUnit.isParking && builderAI.baseUnit.commandQueue.Count < 1)
            {
                BuildUnit[] validUnits = builderAI.buildUnitPrefabs.Where(x => x.unitCategory == needInfo.builderNeed).ToArray();
                if (validUnits != null && validUnits.Count() > 0)
                {
                    // @TODO: AI builders should avoid building gates and walls, which belong to FortTier1
                    BuildUnit unitToBuild = validUnits[Random.Range(0, validUnits.Count())];
                    builderAI.QueueBuild(unitToBuild);
                }
            }
        }
    }

    // Tell factories to start building some units
    private void IssueFactoryOrders(List<RTSUnit> factories)
    {
        foreach (BaseUnitAI factory in factories)
        {
            FactoryAI factoryAI = factory.gameObject.GetComponent<FactoryAI>();
            if (!factoryAI.isBuilding)
            {
                // If this builder is not already conjuring something, pick a unit that meets the current need
                BuildUnit[] validUnits = factoryAI.buildUnitPrefabs.Where(x => x.unitCategory == needInfo.factoryNeed).ToArray();
                if (validUnits != null && validUnits.Count() > 0)
                {
                    BuildUnit unitToBuild = validUnits[Random.Range(0, validUnits.Count())];
                    factoryAI.QueueBuild(unitToBuild);
                }
            }
        }
    }

    // Army attack routine
    private void IssueArmyOrders()
    {
        // @Note: this is only handling ground units, special Dragon, other flyers, and naval units must be handled separately
        List<RTSUnit> groundUnits = inventory.GetUnitsByTypes(
            UnitCategories.InfantryTier1,
            UnitCategories.InfantryTier2,
            UnitCategories.SiegeTier1,
            UnitCategories.SiegeTier2,
            UnitCategories.StalwartTier1,
            UnitCategories.StalwartTier2
        );
        List<RTSUnit> armyUnits = new List<RTSUnit>();
        foreach (Army army in _Armies)
            armyUnits.Concat(army.units);
        // Must pick units that are not already in an army
        List<RTSUnit> validUnits = groundUnits.Except(armyUnits).ToList();

        // @TODO: army size threshold should increase with number of lodestones/factories
        int armySize = 7;
        // If size threshold met, time passed since delay, and armies count less than three
        if (validUnits.Count >= armySize && timeSinceLastArmyOrders > armyOrdersDelay && _Armies.Count < 3)
        {
            armySize = (_Armies.Count + 1) * 7;
            CreateNewArmy(validUnits, armySize);
            timeSinceLastArmyOrders = 0;
        }
        timeSinceLastArmyOrders += Time.deltaTime;
        // Update Army behavior
        foreach (Army army in _Armies)
            army.HandleUpdate();
        // Remove armies that have issued their retreat or have no units left
        _Armies.RemoveAll(army => army.retreatOrdersIssued || army.units.Count == 0);
        // Debug.Log("Armies info: " + string.Join<Army>(", ", _Armies.ToArray()));
    }

    private void CreateNewArmy(List<RTSUnit> units, int armySize)
    {
        Army army = new Army(units, armySize);
        inventory.OnUnitsChanged += army.HandleUnitChange;
        army.PlayerConditions(playerNumber, PlayerNumbers.Player1);
        army.FormUp();
        army.ordersIssued = true;
        Debug.Log("Army called upon.");
        _Armies.Add(army);
    }

    // Return a list of need types with weighted values to pick randomly
    public List<RandomNeed> GetInfantrySpread()
    {
        // This would be for Factory
        return new List<RandomNeed>
        {
            new RandomNeed {categoryLabel = UnitCategories.InfantryTier1, frequency = 0.6f },
            new RandomNeed {categoryLabel = UnitCategories.StalwartTier1, frequency = 0.2f },
            new RandomNeed {categoryLabel = UnitCategories.SiegeTier1, frequency =  0.1f },
            new RandomNeed {categoryLabel = UnitCategories.BuilderTier1, frequency = 0.1f}
        };
    }

    // Builder spread with emphasis on LodestoneTier1
    public List<RandomNeed> GetLodestoneSpread()
    {
        return new List<RandomNeed>
        {
            new RandomNeed {categoryLabel = UnitCategories.LodestoneTier1, frequency = 0.5f },
            new RandomNeed {categoryLabel = UnitCategories.FactoryTier1, frequency = 0.3f },
            new RandomNeed {categoryLabel = UnitCategories.FortTier1, frequency =  0.1f },
            new RandomNeed {categoryLabel = UnitCategories.NavalTier1, frequency = 0.1f}
        };
    }

    public UnitCategories PickRandomNeedType(List<RandomNeed> needs)
    {
        float slider = 0.0f;
        // Setup frequency range to check against
        foreach (RandomNeed need in needs)
        {
            need.frequencyRange = new List<float> { slider, need.frequency + slider };
            slider = need.frequency;
        }
        // Pick need randomly but based on frequency, e.g. InfantryTier1 should be picked 60% of the time, over SiegeTier1 10% of the time
        float randomBetween0And1 = Random.Range(0.0f, 1.0f);
        foreach (RandomNeed need in needs)
        {
            if (randomBetween0And1 >= need.frequencyRange[0] && randomBetween0And1 <= need.frequencyRange[1])
                return need.categoryLabel;
        }
        return needs[0].categoryLabel;
    }

    public class RandomNeed
    {
        public UnitCategories categoryLabel;
        public float frequency;
        public List<float> frequencyRange;
    }
    public class NeedInfo
    {
        public UnitCategories builderNeed;
        public UnitCategories factoryNeed;
    }
    private NeedInfo DetermineNeedState()
    {
        // Compile all needs
        needInfo = new NeedInfo();

        // @TODO: monarch can substitute need for builder at the beginning

        List<RTSUnit> factoryTier1 = inventory.GetUnitsByType(UnitCategories.FactoryTier1);
        List<RTSUnit> builderTier1 = inventory.GetUnitsByType(UnitCategories.BuilderTier1);

        // If at least one builder but no factories
        if (factoryTier1.Count == 0 && builderTier1.Count > 0)
        {
            // Factory takes priority
            needInfo.builderNeed = UnitCategories.FactoryTier1;
        }
        // If at least one factory but no builders
        else if (factoryTier1.Count > 0 && builderTier1.Count == 0)
        {
            // Builder takes priority
            needInfo.factoryNeed = UnitCategories.BuilderTier1;
        }
        // If both builder(s) and factory(s) exist
        else if (factoryTier1.Count > 0 && builderTier1.Count > 0)
        {
            // Infantry takes priority for Factories, also Stalwart and Siege
            needInfo.factoryNeed = PickRandomNeedType(GetInfantrySpread());
            // Builders should roam and build Lodestones, Factories, and Forts. 
            // @TODO: Number of lodestones informs which takes priority
            needInfo.builderNeed = PickRandomNeedType(GetLodestoneSpread());
        }

        return needInfo;
    }
}
