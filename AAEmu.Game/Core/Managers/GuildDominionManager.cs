using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.StaticValues;

using NLog;

namespace AAEmu.Game.Core.Managers;

/// <summary>
/// Old (guild-owned) castle system - Exeloch/Sungold Fields (zone groups 54/56) only. Split out of
/// DominionManager 2026-08-24 as a genuinely separate feature - own `guild_dominions` MySQL table, own
/// `guild_dominion_housings` housing-template registry (HousingGameData.IsGuildDominionHousingTemplate), own
/// in-memory state. Reuses DominionData (the wire-format model) and SCDominionDataPacket (the proven, byte-
/// correct sender) rather than re-deriving wire serialization from scratch - see GuildDominionManager's own
/// design note in the 2026-08-24 session for why duplicating that specific code was judged too risky to redo
/// blind. Deliberately does not cover Hero/faction-only concepts that never applied to the guild system anyway
/// (siege timers, national monuments, nation transfer, weekly tax payout) - those remain DominionManager's
/// responsibility for the 4 Hero/faction territories.
/// </summary>
public class GuildDominionManager(IExpeditionManager expeditionManager, IGameDataManager gameDataManager) : Singleton<GuildDominionManager>, IGuildDominionManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    // Unused beyond the constructor - ordering-only dependency so ManagerOrchestrator runs GameDataManager.Load()
    // (which populates SiegeGameData/HousingGameData) before this one. Same pattern DominionManager itself uses
    // and needed for the same reason (BuildTerritoryData/GetDesignByItemId read those game-data singletons).
    private readonly IGameDataManager _gameDataManager = gameDataManager;

    private Dictionary<ushort, DominionData> _guildDominions = [];
    private Dictionary<ushort, uint> _guardTowerSettingIdByZone = [];
    private Dictionary<ushort, int> _guardTowerStepByZone = [];
    private Dictionary<ushort, int> _castleTierByZone = [];
    private readonly Dictionary<ushort, HashSet<uint>> _builtStructuresByZone = [];

    public IEnumerable<DominionData> GuildDominions => _guildDominions.Values;

    public DominionData GetByZoneId(ushort zoneId) => _guildDominions.GetValueOrDefault(zoneId);

    public DominionData GetDominionAtPosition(ushort zoneId, float x, float y)
    {
        if (!_guildDominions.TryGetValue(zoneId, out var dominion))
            return null;

        var dx = x - dominion.X;
        var dy = y - dominion.Y;
        var radius = dominion.TerritoryData?.RadiusDominion ?? 0;
        return dx * dx + dy * dy <= (float)radius * radius ? dominion : null;
    }

    public void Load()
    {
        _guildDominions = [];
        _guardTowerSettingIdByZone = [];
        _guardTowerStepByZone = [];
        _castleTierByZone = [];

        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM guild_dominions";
        command.Prepare();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var zoneId = (ushort)reader.GetInt32(reader.GetOrdinal("zone_id"));
            var guardTowerSettingId = (uint)reader.GetInt32(reader.GetOrdinal("guard_tower_setting_id"));
            _guardTowerStepByZone[zoneId] = reader.GetInt32(reader.GetOrdinal("guard_tower_step"));
            _castleTierByZone[zoneId] = reader.GetInt32(reader.GetOrdinal("castle_tier"));

            var expeditionId = (uint)reader.GetInt32(reader.GetOrdinal("expedition_id"));

            var dominion = new DominionData
            {
                ZoneId = zoneId,
                ExpeditionId = expeditionId,
                OwningFactionId = 0,
                FactionId = ResolveOwningFaction(expeditionId),
                House = (uint)reader.GetInt32(reader.GetOrdinal("house")),
                TaxRate = reader.GetInt32(reader.GetOrdinal("tax_rate")),
                X = reader.GetFloat(reader.GetOrdinal("x")),
                Y = reader.GetFloat(reader.GetOrdinal("y")),
                Z = reader.GetFloat(reader.GetOrdinal("z")),
                CurHouseTaxMoney = reader.GetInt32(reader.GetOrdinal("cur_house_tax_money")),
                CurHuntTaxMoney = reader.GetInt32(reader.GetOrdinal("cur_hunt_tax_money")),
                PeaceTaxMoney = reader.GetInt32(reader.GetOrdinal("peace_tax_money")),
                CurHouseTaxAaPoint = reader.GetInt32(reader.GetOrdinal("cur_house_tax_aa_point")),
                PeaceTaxAaPoint = reader.GetInt32(reader.GetOrdinal("peace_tax_aa_point")),
                LastPaidTime = reader.GetDateTime(reader.GetOrdinal("last_paid_time")),
                LastSiegeEndTime = reader.GetDateTime(reader.GetOrdinal("last_siege_end_time")),
                ReignStartTime = reader.GetDateTime(reader.GetOrdinal("reign_start_time")),
                LastTaxRateChangedTime = reader.GetDateTime(reader.GetOrdinal("last_tax_rate_changed_time")),
                ObjId = 0,
                TerritoryData = DominionManager.BuildTerritoryData(guardTowerSettingId),
                SiegeTimers = new DominionSiegeTimers
                {
                    Durations = [0, 0, 0, 0, 0],
                    Started = DateTime.MinValue,
                    Fixed = DateTime.MinValue,
                    Bdm = 0,
                    SiegePeriod = (byte)reader.GetInt32(reader.GetOrdinal("siege_period")),
                    UnkData = DominionManager.EmptyUnkData(),
                    Unk2Data = DominionManager.EmptyUnkData()
                },
                NonPvPStart = reader.GetDateTime(reader.GetOrdinal("non_pvp_start")),
                NonPvPDuration = (ushort)reader.GetInt32(reader.GetOrdinal("non_pvp_duration"))
            };

            _guildDominions[zoneId] = dominion;
            _guardTowerSettingIdByZone[zoneId] = guardTowerSettingId;
        }

        Logger.Info("Loaded {0} guild dominions", _guildDominions.Count);
    }

    /// <summary>Same resolution DominionManager.ResolveOwningFaction(uint) uses - duplicated (not shared) since it's a short, simple instance method with its own expeditionManager dependency.</summary>
    private FactionsEnum ResolveOwningFaction(uint expeditionId)
    {
        var expedition = expeditionManager.Expeditions.FirstOrDefault(e => (uint)e.Id == expeditionId);
        var ownerMember = expedition?.GetMember(expedition.OwnerId) ?? expedition?.Members.FirstOrDefault();
        if (ownerMember == null)
            return FactionsEnum.Invalid;

        var raceFaction = FactionManager.Instance.GetFaction(ownerMember.FactionId);
        return raceFaction != null && raceFaction.MotherId != FactionsEnum.Invalid
            ? raceFaction.MotherId
            : ownerMember.FactionId;
    }

    public void SendAllDominionsTo(GameConnection connection)
    {
        var character = connection?.ActiveChar;
        if (character == null)
            return;

        foreach (var dominion in _guildDominions.Values)
            character.SendPacket(new SCDominionDataPacket(dominion, false, true));
    }

    public int GetGuardTowerStep(ushort zoneId) => _guardTowerStepByZone.GetValueOrDefault(zoneId, 0);

    public int AdvanceGuardTowerStep(ushort zoneId)
    {
        if (!_guardTowerSettingIdByZone.TryGetValue(zoneId, out var guardTowerSettingId))
            return 0;

        var currentStep = GetGuardTowerStep(zoneId);
        var maxStep = SiegeGameData.Instance.GetMaxGuardTowerStep(guardTowerSettingId);
        if (currentStep >= maxStep)
            return currentStep;

        var nextStep = currentStep + 1;
        var stepRow = SiegeGameData.Instance.GetGuardTowerSteps(guardTowerSettingId).FirstOrDefault(s => s.Step == nextStep);
        if (stepRow == null)
            return currentStep;

        _guardTowerStepByZone[zoneId] = nextStep;

        using (var connection = MySQL.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE guild_dominions SET guard_tower_step = @step WHERE zone_id = @zoneId";
            command.Parameters.AddWithValue("@step", nextStep);
            command.Parameters.AddWithValue("@zoneId", zoneId);
            command.Prepare();
            command.ExecuteNonQuery();
        }

        if (_guildDominions.TryGetValue(zoneId, out var dominion) && stepRow.BuffId != 0)
        {
            var house = HousingManager.Instance.GetHouseById(dominion.House);
            house?.Buffs.AddBuff(stepRow.BuffId, house);
        }

        Logger.Info("Guild dominion zone {0} guard tower advanced to step {1} ({2} gates, {3} walls)", zoneId, nextStep, stepRow.NumGates, stepRow.NumWalls);
        return nextStep;
    }

    public int GetCastleTier(ushort zoneId) => _castleTierByZone.GetValueOrDefault(zoneId, 0);

    public int AdvanceCastleTier(ushort zoneId, int targetTier)
    {
        if (!_guildDominions.ContainsKey(zoneId))
            return GetCastleTier(zoneId);

        _castleTierByZone[zoneId] = targetTier;

        using (var connection = MySQL.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE guild_dominions SET castle_tier = @tier WHERE zone_id = @zoneId";
            command.Parameters.AddWithValue("@tier", targetTier);
            command.Parameters.AddWithValue("@zoneId", zoneId);
            command.Prepare();
            command.ExecuteNonQuery();
        }

        Logger.Info("Guild dominion zone {0} castle tier advanced to {1}", zoneId, targetTier);
        return targetTier;
    }

    private static void UpsertRow(DominionData dominion, uint guardTowerSettingId, int guardTowerStep, int castleTier)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            REPLACE INTO guild_dominions
                (zone_id, expedition_id, house, guard_tower_setting_id, guard_tower_step, castle_tier, tax_rate, x, y, z,
                 cur_house_tax_money, cur_hunt_tax_money, peace_tax_money, cur_house_tax_aa_point, peace_tax_aa_point,
                 last_paid_time, last_siege_end_time, reign_start_time, last_tax_rate_changed_time, siege_period,
                 non_pvp_start, non_pvp_duration)
            VALUES
                (@zoneId, @expeditionId, @house, @guardTowerSettingId, @guardTowerStep, @castleTier, @taxRate, @x, @y, @z,
                 @curHouseTaxMoney, @curHuntTaxMoney, @peaceTaxMoney, @curHouseTaxAaPoint, @peaceTaxAaPoint,
                 @lastPaidTime, @lastSiegeEndTime, @reignStartTime, @lastTaxRateChangedTime, @siegePeriod,
                 @nonPvPStart, @nonPvPDuration)
            """;
        command.Parameters.AddWithValue("@zoneId", dominion.ZoneId);
        command.Parameters.AddWithValue("@expeditionId", dominion.ExpeditionId);
        command.Parameters.AddWithValue("@house", dominion.House);
        command.Parameters.AddWithValue("@guardTowerSettingId", guardTowerSettingId);
        command.Parameters.AddWithValue("@guardTowerStep", guardTowerStep);
        command.Parameters.AddWithValue("@castleTier", castleTier);
        command.Parameters.AddWithValue("@taxRate", dominion.TaxRate);
        command.Parameters.AddWithValue("@x", dominion.X);
        command.Parameters.AddWithValue("@y", dominion.Y);
        command.Parameters.AddWithValue("@z", dominion.Z);
        command.Parameters.AddWithValue("@curHouseTaxMoney", dominion.CurHouseTaxMoney);
        command.Parameters.AddWithValue("@curHuntTaxMoney", dominion.CurHuntTaxMoney);
        command.Parameters.AddWithValue("@peaceTaxMoney", dominion.PeaceTaxMoney);
        command.Parameters.AddWithValue("@curHouseTaxAaPoint", dominion.CurHouseTaxAaPoint);
        command.Parameters.AddWithValue("@peaceTaxAaPoint", dominion.PeaceTaxAaPoint);
        command.Parameters.AddWithValue("@lastPaidTime", dominion.LastPaidTime);
        command.Parameters.AddWithValue("@lastSiegeEndTime", dominion.LastSiegeEndTime);
        command.Parameters.AddWithValue("@reignStartTime", dominion.ReignStartTime);
        command.Parameters.AddWithValue("@lastTaxRateChangedTime", dominion.LastTaxRateChangedTime);
        command.Parameters.AddWithValue("@siegePeriod", dominion.SiegeTimers.SiegePeriod);
        command.Parameters.AddWithValue("@nonPvPStart", dominion.NonPvPStart);
        command.Parameters.AddWithValue("@nonPvPDuration", dominion.NonPvPDuration);
        command.Prepare();
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 2026-08-24: real, live regression fix, not just a GM-command nicety. DeclareDominion.cs (the actual
    /// skill-driven claim path players use to plant/re-plant a Guard Tower) called DominionManager.Declare for
    /// the guild-owned branch (54/56) unconditionally - after tonight's split moved those two zones' live state
    /// into this manager, that call's own "already claimed" guard (_dominions.ContainsKey) could never see them,
    /// risking a duplicate claim written into the wrong table. This is DominionManager.Declare's guild-only
    /// branch, moved here verbatim (House model-swap/buff-apply/broadcast/save plumbing is identical - only
    /// which store the resulting claim lands in, and the Hero/faction-only fields (national tax/monument,
    /// Territory Agent NPC - guild zones have no siege_zones row so no dominion_merchant_id exists for them
    /// anyway, confirmed via EnsureTerritoryAgentNpc's own early-return) differ).
    /// </summary>
    public DominionData Declare(ushort zoneId, uint expeditionId, House lodestone, Models.Game.Char.Character declarer)
    {
        if (_guildDominions.ContainsKey(zoneId))
        {
            declarer?.SendErrorMessage(ErrorMessageType.DominionAlreadyDedclared);
            return null;
        }

        var guardTowerSettingId = lodestone.Template?.GuardTowerSettingId ?? 0;
        var now = DateTime.UtcNow;

        var dominion = new DominionData
        {
            ZoneId = zoneId,
            ExpeditionId = expeditionId,
            OwningFactionId = 0,
            FactionId = DominionManager.ResolveOwningFaction(declarer) is var declarerFaction && declarerFaction != FactionsEnum.Invalid
                ? declarerFaction
                : ResolveOwningFaction(expeditionId),
            House = lodestone.Id,
            TaxRate = 0,
            X = lodestone.Transform.World.Position.X,
            Y = lodestone.Transform.World.Position.Y,
            Z = lodestone.Transform.World.Position.Z,
            CurHouseTaxMoney = 0,
            CurHuntTaxMoney = 0,
            PeaceTaxMoney = 0,
            CurHouseTaxAaPoint = 0,
            PeaceTaxAaPoint = 0,
            LastPaidTime = now,
            LastSiegeEndTime = now,
            ReignStartTime = now,
            LastTaxRateChangedTime = now,
            ObjId = 0,
            TerritoryData = DominionManager.BuildTerritoryData(guardTowerSettingId),
            SiegeTimers = new DominionSiegeTimers
            {
                Bdm = 0,
                Durations = [0, 0, 0, 0, 0],
                Fixed = DateTime.MinValue,
                Started = DateTime.MinValue,
                SiegePeriod = 1,
                UnkData = DominionManager.EmptyUnkData(),
                Unk2Data = DominionManager.EmptyUnkData()
            },
            NonPvPDuration = 0,
            NonPvPStart = now
        };

        _guildDominions[zoneId] = dominion;
        _guardTowerSettingIdByZone[zoneId] = guardTowerSettingId;
        _guardTowerStepByZone[zoneId] = 0;
        _castleTierByZone[zoneId] = 0;
        UpsertRow(dominion, guardTowerSettingId, 0, 0);
        DominionManager.NotifyZoneDominionClaimed(dominion, lodestone.Transform.ZoneId);

        if (declarer != null)
        {
            lodestone.OwnerId = declarer.Id;
            lodestone.CoOwnerId = declarer.Id;
            lodestone.AccountId = declarer.AccountId;
            lodestone.BroadcastPacket(new SCHouseDataPacket([lodestone]), true);
            if (WorldIntegration.ZoneAuthority)
                HousingZoneBridge.NotifyZoneHouseCreated(lodestone);
        }

        if (lodestone.CurrentStep != -1)
        {
            lodestone.AddBuildAction();
            lodestone.BroadcastPacket(
                new SCHouseBuildProgressPacket(
                    lodestone.TlId,
                    lodestone.ModelId,
                    lodestone.AllAction,
                    lodestone.CurrentStep == -1 ? lodestone.AllAction : lodestone.CurrentAction
                ),
                true
            );
            HousingZoneBridge.NotifyZoneHouseBuildState(lodestone);

            if (lodestone.CurrentStep == -1)
            {
                foreach (var doodad in lodestone.AttachedDoodads.ToArray())
                    doodad.Spawn();
            }
        }

        if (dominion.TerritoryData?.Id2 is { } initialBuffId and > 0)
            lodestone.Buffs.AddBuff(initialBuffId, lodestone);

        var expedition = expeditionManager.Expeditions.FirstOrDefault(e => (uint)e.Id == expeditionId);
        var territoryName = lodestone.Template?.Name;
        var guildName = expedition?.Name ?? "A guild";
        var placeName = string.IsNullOrEmpty(territoryName) ? "a territory" : territoryName;
        var announcement = $"|cffe7ce25{guildName}|r |cFF87CEFAhas successfully claimed |cFFFFFFFF{placeName}|r|cFF87CEFA!|r";
        WorldManager.Instance.BroadcastPacketToServer(new SCWorldMessagePacket(0, 1, announcement));
        WorldManager.Instance.BroadcastPacketToServer(new SCDominionDataPacket(dominion, true, true));

        SaveLodestoneNow(lodestone);
        return dominion;
    }

    private static void SaveLodestoneNow(House lodestone)
    {
        using var connection = MySQL.CreateConnection();
        using var transaction = connection.BeginTransaction();
        lodestone.Save(connection, transaction);
        transaction.Commit();
    }

    /// <summary>GM/testing counterpart to DominionManager.ClaimTerritory, for the guild-owned zones only.</summary>
    public DominionData ClaimTerritory(ushort zoneId, Models.Game.Expeditions.Expedition expedition, Models.Game.Char.Character declarer)
    {
        if (expedition == null || _guildDominions.ContainsKey(zoneId))
            return null;

        var templateId = SiegeGameData.Instance.GetLodestoneHousingTemplateId(zoneId);
        if (templateId == null)
            return null;

        var lodestone = HousingManager.Instance.GetAllHouses().FirstOrDefault(h => h.TemplateId == templateId.Value);
        if (lodestone == null)
            return null;

        return Declare(zoneId, (uint)expedition.Id, lodestone, declarer);
    }

    /// <summary>GM/testing tool - see DominionManager.UnclaimTerritory's doc comment for the full behavior this mirrors (minus Territory Agent NPC cleanup, which never applies to guild zones - see Declare's doc comment).</summary>
    public bool UnclaimTerritory(ushort zoneId)
    {
        if (!_guildDominions.TryGetValue(zoneId, out var dominion))
            return false;

        var house = HousingManager.Instance.GetHouseById(dominion.House);
        if (house != null)
        {
            if (_guardTowerSettingIdByZone.TryGetValue(zoneId, out var guardTowerSettingId))
            {
                var currentStep = GetGuardTowerStep(zoneId);
                foreach (var stepRow in SiegeGameData.Instance.GetGuardTowerSteps(guardTowerSettingId))
                {
                    if (stepRow.Step <= currentStep && stepRow.BuffId != 0)
                        house.Buffs.RemoveBuff(stepRow.BuffId, false);
                }
            }

            if (dominion.TerritoryData?.Id2 is { } initialBuffId and > 0)
                house.Buffs.RemoveBuff(initialBuffId, false);

            house.OwnerId = 0;
            house.CoOwnerId = 0;
            house.AccountId = 0;
            house.NumAction = 0;
            house.CurrentStep = 0;

            house.BroadcastPacket(new SCHouseDataPacket([house]), true);
            house.BroadcastPacket(
                new SCHouseBuildProgressPacket(house.TlId, house.ModelId, house.AllAction, house.CurrentAction),
                true);

            if (WorldIntegration.ZoneAuthority)
            {
                HousingZoneBridge.NotifyZoneHouseCreated(house);
                HousingZoneBridge.NotifyZoneHouseBuildState(house);
            }

            SaveLodestoneNow(house);
        }
        else
        {
            // 2026-08-25: see the identical fix/comment in DominionManager.UnclaimTerritory - same copy-pasted
            // bug, confirmed live on Exeloch's lodestone (House id 11), fixed by hand via direct DB UPDATE.
            Logger.Error(
                "UnclaimTerritory: House {0} for zone {1} not found via HousingManager.GetHouseById - " +
                "the dominion record is being removed but the House itself CANNOT be reset (owner/model will " +
                "stay stuck in its last claimed state). This is a data-integrity risk, needs manual DB fixup.",
                dominion.House, zoneId);
        }

        _guildDominions.Remove(zoneId);
        _guardTowerSettingIdByZone.Remove(zoneId);
        _guardTowerStepByZone.Remove(zoneId);
        _castleTierByZone.Remove(zoneId);
        _builtStructuresByZone.Remove(zoneId);

        using (var connection = MySQL.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM guild_dominions WHERE zone_id = @zoneId";
            command.Parameters.AddWithValue("@zoneId", zoneId);
            command.Prepare();
            command.ExecuteNonQuery();
        }

        var cleared = new DominionData
        {
            ZoneId = zoneId,
            ExpeditionId = 0,
            FactionId = FactionsEnum.Invalid,
            House = 0,
            TaxRate = 0,
            X = 0,
            Y = 0,
            Z = 0,
            CurHouseTaxMoney = 0,
            CurHuntTaxMoney = 0,
            PeaceTaxMoney = 0,
            CurHouseTaxAaPoint = 0,
            PeaceTaxAaPoint = 0,
            LastPaidTime = DateTime.MinValue,
            LastSiegeEndTime = DateTime.MinValue,
            ReignStartTime = DateTime.MinValue,
            LastTaxRateChangedTime = DateTime.MinValue,
            ObjId = 0,
            TerritoryData = new DominionTerritoryData(),
            SiegeTimers = new DominionSiegeTimers
            {
                Durations = [0, 0, 0, 0, 0],
                Started = DateTime.MinValue,
                Fixed = DateTime.MinValue,
                Bdm = 0,
                SiegePeriod = 0,
                UnkData = DominionManager.EmptyUnkData(),
                Unk2Data = DominionManager.EmptyUnkData()
            },
            NonPvPStart = DateTime.MinValue,
            NonPvPDuration = 0
        };
        WorldManager.Instance.BroadcastPacketToServer(new SCDominionDataPacket(cleared, true, true));

        Logger.Info("Guild dominion zone {0} unclaimed via GM tool (was Expedition {1})", zoneId, dominion.ExpeditionId);
        return true;
    }

    /// <summary>Guild-system counterpart to DominionManager.RelayAllToZone - re-announces claim state to a (re)loaded Zone. No Territory Agent NPC re-ensure here, see Declare's doc comment for why guild zones never have one.</summary>
    public void RelayAllToZone(uint rawZoneId)
    {
        if (!WorldIntegration.ZoneAuthority)
            return;

        foreach (var dominion in _guildDominions.Values)
        {
            var house = HousingManager.Instance.GetHouseById(dominion.House);
            if (house?.Transform == null || house.Transform.ZoneId != rawZoneId)
                continue;

            DominionManager.NotifyZoneDominionClaimed(dominion, rawZoneId);
        }
    }

    /// <summary>
    /// 2026-08-27 fix: CSUpdateDominionTaxRatePacket called DominionManager.Instance.UpdateTaxRate
    /// unconditionally, with no equivalent here - since the 2026-08-24 split moved zone groups 54/56 out of
    /// DominionManager._dominions and into this manager, that call's lookup could never find them, so tax
    /// rate changes on Exeloch/Sungold silently no-op'd. Same simple Expedition-membership check
    /// DominionManager.UpdateTaxRate/AdvanceGuardTowerStep already use for the guild-owned branch - no Hero
    /// concept applies here, this system has none.
    /// </summary>
    public void UpdateTaxRate(GameConnection connection, ushort zoneId, int taxRate)
    {
        var character = connection?.ActiveChar;
        if (character == null)
            return;

        if (!_guildDominions.TryGetValue(zoneId, out var dominion))
            return;

        if (taxRate < 0)
        {
            character.SendErrorMessage(ErrorMessageType.Invalid);
            return;
        }

        // 2026-08-27: tightened to guild-leader-only, per the user's explicit design call - matches the same
        // gate now applied to build placement/demolish/guard-tower/castle-tier for this territory.
        if (character.Expedition == null || (uint)character.Expedition.Id != dominion.ExpeditionId
            || character.Id != character.Expedition.OwnerId)
        {
            character.SendErrorMessage(ErrorMessageType.NoPerm);
            return;
        }

        dominion.TaxRate = taxRate;
        dominion.LastTaxRateChangedTime = DateTime.UtcNow;

        using (var mysqlConnection = MySQL.CreateConnection())
        using (var command = mysqlConnection.CreateCommand())
        {
            command.CommandText = "UPDATE guild_dominions SET tax_rate = @taxRate, last_tax_rate_changed_time = @changed WHERE zone_id = @zoneId";
            command.Parameters.AddWithValue("@taxRate", dominion.TaxRate);
            command.Parameters.AddWithValue("@changed", dominion.LastTaxRateChangedTime);
            command.Parameters.AddWithValue("@zoneId", zoneId);
            command.Prepare();
            command.ExecuteNonQuery();
        }

        WorldManager.Instance.BroadcastPacketToServer(new SCDominionTaxRatePacket(zoneId, dominion.TaxRate));
    }

    public House TryBuildDominionStructure(ushort zoneId, uint itemTemplateId, Character declarer)
    {
        if (!_guildDominions.ContainsKey(zoneId))
            return null;

        var designId = HousingGameData.Instance.GetDesignByItemId(itemTemplateId);
        if (designId == 0)
            return null;

        if (!_builtStructuresByZone.TryGetValue(zoneId, out var built))
            _builtStructuresByZone[zoneId] = built = [];

        var isUniqueDesign = SiegeGameData.Instance.IsUniqueDominionHousingDesign(designId);
        if (isUniqueDesign && built.Contains(designId))
        {
            declarer.SendErrorMessage(ErrorMessageType.Invalid);
            return null;
        }

        var pos = declarer.Transform.World.Position;
        var house = HousingManager.Instance.CreateDominionHouse(designId, declarer, declarer.ParentWorld, pos.X, pos.Y, pos.Z);
        if (house == null)
            return null;

        built.Add(designId);
        Logger.Info("Guild dominion zone {0}: {1} built structure design {2} (item {3}) as house {4}",
            zoneId, declarer.Name, designId, itemTemplateId, house.Id);
        return house;
    }
}
