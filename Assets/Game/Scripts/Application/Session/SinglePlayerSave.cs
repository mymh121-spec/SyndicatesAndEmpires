using System;
using System.Collections.Generic;
using Game.Application.Turn;
using Game.Application.World;
using Game.Domain.Military;
using Game.Domain.World;

namespace Game.Application.Session
{
    // Append new values; never renumber actions in an existing save version.
    public enum SinglePlayerAction
    {
        Advance, Recruit, Equip, HireCommander, Move, Attack, CancelMove,
        CaptureMine, Survey, BuildMine, OccupationPolicy, SiegeAction,
        CaptureCastle, CastleRole, SupplyMission, EconomicOperation
    }

    [Serializable]
    public sealed class SinglePlayerInput
    {
        public string unitId;
        public bool hasCell;
        public int x;
        public int y;
        public string targetUnitId;
        public string trackedEnemyUnitId;
        public UnitArchetype archetype;
        public UnitWeaponType weapon;
        public ArmorClass armor;
        public bool hasRecruitmentOrigin;
        public int recruitmentX;
        public int recruitmentY;
        public int commanderIndex;
        public int operationIndex;
        public int operationAgentIndex;
        public int operationApproachIndex;
    }

    [Serializable]
    public sealed class SinglePlayerRecord
    {
        public SinglePlayerAction action;
        public int argument;
        public SinglePlayerInput input;
        public int mapSteps;
        public int completedDays;
        public double gameHours;
    }

    [Serializable]
    public sealed class SinglePlayerSave
    {
        public const int CurrentVersion = 1;
        // Change when simulation rules or action semantics change. Replays must
        // never silently run against a different ruleset.
        public const string CurrentRules = "single-player-2026-10-v1";
        public const int MaximumRecords = 100000;
        public int version = CurrentVersion;
        public string rules = CurrentRules;
        public string configuration;
        public string savedAtUtc;
        public MapSizePreset size;
        public MapResourceAbundance resources;
        public MapWaterLevel water;
        public int seed;
        public int neutralCastles;
        public bool wrap;
        public int turn;
        public string playerCash;
        public RealtimeClockState clock;
        public SinglePlayerInput selection;
        public float cameraX;
        public float cameraZ;
        public float cameraZoom;
        public List<SinglePlayerRecord> records = new List<SinglePlayerRecord>();

        public MapGenerationSettings CreateMapSettings() => new MapGenerationSettings(
            size, resources, water, seed, neutralCastles, wrap);

        public void AddAdvance(int mapSteps, int days, double hours)
        {
            if (mapSteps <= 0) return;
            SinglePlayerRecord last = records.Count == 0 ? null : records[records.Count - 1];
            // Preserve every economic boundary. Between boundaries the map
            // already iterates fixed steps, so adjacent advances can be batched.
            if (last != null && last.action == SinglePlayerAction.Advance &&
                last.completedDays == 0 && days == 0 && last.mapSteps + mapSteps <= 256)
            {
                last.mapSteps += mapSteps;
                last.gameHours = hours;
                return;
            }
            records.Add(new SinglePlayerRecord
            {
                action = SinglePlayerAction.Advance, mapSteps = mapSteps,
                completedDays = days, gameHours = hours
            });
        }

        public void Validate()
        {
            if (version != CurrentVersion || rules != CurrentRules)
                throw new InvalidOperationException("이 버전에서 불러올 수 없는 저장입니다.");
            if (!Enum.IsDefined(typeof(MapSizePreset), size) ||
                !Enum.IsDefined(typeof(MapResourceAbundance), resources) ||
                !Enum.IsDefined(typeof(MapWaterLevel), water) ||
                neutralCastles < 0 || neutralCastles > 24 || turn < 1 || turn > 360 ||
                string.IsNullOrEmpty(configuration) || records == null ||
                records.Count > MaximumRecords || clock == null || selection == null)
                throw new InvalidOperationException("저장 데이터가 올바르지 않습니다.");
            clock.Validate();
            double previousHours = 0;
            long steps = 0;
            int days = 0;
            foreach (SinglePlayerRecord record in records)
            {
                if (record == null || !Enum.IsDefined(typeof(SinglePlayerAction), record.action))
                    throw new InvalidOperationException("알 수 없는 저장 명령입니다.");
                if (record.action == SinglePlayerAction.Advance)
                {
                    if (record.mapSteps < 1 || record.mapSteps > 256 ||
                        record.completedDays < 0 || record.completedDays > 2 ||
                        double.IsNaN(record.gameHours) || double.IsInfinity(record.gameHours) ||
                        record.gameHours < previousHours || record.gameHours > 24 * 361)
                        throw new InvalidOperationException("저장된 시간 기록이 올바르지 않습니다.");
                    previousHours = record.gameHours;
                    steps += record.mapSteps;
                    days += record.completedDays;
                }
                else if (record.input == null)
                    throw new InvalidOperationException("저장된 행동 정보가 없습니다.");
            }
            if (steps > 1000000 || days > 360 ||
                Math.Abs(previousHours - clock.totalGameHours) > 0.000001)
                throw new InvalidOperationException("저장된 진행 시간이 일치하지 않습니다.");
        }
    }
}
