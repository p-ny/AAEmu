using System.Numerics;

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
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Tasks.Dominions;
using AAEmu.Game.Utils;

using NLog;

namespace AAEmu.Game.Core.Managers;

/// <summary>
/// Live Dominion (Castle) claim state — who owns which zone group, tax pools, national tax/monument. The
/// design/schedule tables (siege_plans, siege_zones, guard_tower_settings) in game_decrypted.sqlite3 are read-only
/// templates; this manager owns the actual save state, persisted to the `dominions` MySQL table (see
/// SQL/updates/2026-08-13_aaemu_game_dominions.sql).
///
/// SiegeTimers.SiegePeriod is driven by SiegeManager's schedule tick (see UpdateSiegePeriod); the rest of
/// SiegeTimers (durations/participant state) is still at its zeroed default - raid-team roster/score tracking
/// is not built yet, per the build-order in D:\aa\siege-castle-hero-nation-brief.md.
///
/// GuardTowerStep (see AdvanceGuardTowerStep) tracks guard_tower_steps progression - manager-side only, not part
/// of DominionData's wire format (SiegeTimers.Bdm/Durations are already-unconfirmed fields in that struct, not
/// safe to overload). STALE NOTE CORRECTED 2026-08-21: this WAS unwired as described below, but a 2026-08-19
/// session already connected it - see Skills/Effects/SpecialEffects/AdvanceGuardTowerStep.cs (SpecialEffectType
/// 197). CORRECTED AGAIN 2026-08-21 (deeper pass): skill 41079 is not scoped to 5 guard-tower items - EVERY
/// territory-construction blueprint shares it (24 items total: farmhouse/workshop/storage/supervisor-post,
/// their "완공"/completed variants, AND Guardian Altar 47335/47492), confirmed via skill_effects (skill 41079
/// has exactly one row, effect 102982 -> SpecialEffect 70575 -> this class - no per-item branching exists at
/// all). So using ANY of these 24 items, including a farmhouse, currently advances the same shared step
/// counter - almost certainly broader than intended, but not reworked here (would need real data on which
/// items SHOULD count, which doesn't exist - flagged, not guessed).
///
/// THREE CONFIRMED DATA GAPS, checked exhaustively 2026-08-21, none fixable without inventing ids:
/// 1. No doodad ever gets spawned when any of these 24 items is used - skill 41079's only effect is this one
///    (consume item + advance step + buff the House), confirmed via skill_effects. Live-confirmed too: zero
///    rows in MySQL `doodads` for item_template_id IN (47335, 47492) despite real in-game use. The client-side
///    "place a building in your territory" experience the user has is not backed by any spawn on this server.
/// 2. `item_spawn_doodads` (the real item->doodad-template table the generic CSCreateDoodadPacket/
///    DoodadManager.CreatePlayerDoodad placement path reads, see NationManager.PlaceNationalMonument for the
///    analogous siege_zones.MonumentDoodadId-driven pattern that DOES have real data) has 542 real rows for
///    other items, but ZERO for any of the 24 territory-blueprint items - the mapping was never authored.
/// 3. stepRow.NumGates/NumWalls (guard_tower_steps) are real numbers (e.g. 1 gate + 42-45 walls at step 4/5 of
///    settings 2/5/7/10) but neither `guard_tower_settings` nor `guard_tower_steps` has ANY doodad-template or
///    NPC-template id column - there is no data anywhere naming what a "gate"/"wall"/"guard" actually is.
/// Also checked: no quest_component anywhere ties a doodad-interaction ("pay respect") to this item or its
/// step progression - the only quest reference to 47335 is quest_act_obj_item_gathers id 4339, a simple
/// "possess 1x" objective, unrelated to placement/interaction. Conclusion: the castle-building visual/spawn
/// layer (altar doodad, gates, walls, guards, and a placed-building interact-to-progress loop) was never
/// authored in this dataset at all - a genuine upstream content gap, not a wiring gap this codebase can close
/// without spawning made-up template ids. Revisit only if better source data turns up.
///
/// Tax system deliberately DISABLED per the user, 2026-08-19: the castle system has no tax mechanic at all in
/// their recollection of real ArcheAge - reusing the generic per-House tax system for Dominion was the wrong
/// model. Declare() now seeds TaxRate/CurHouseTaxMoney/CurHuntTaxMoney/PeaceTaxMoney/NationalTaxRate at 0, which
/// makes PayoutTax's own `total &lt;= 0` guard a permanent no-op - no weekly "Dominion Tax" mail is ever sent.
/// Left PayoutTax/the weekly task scheduling and the tax fields IN DominionData's wire struct rather than
/// removing them - see the wire-format research note below, that struct is still not fully verified and pulling
/// fields out risks the same kind of silent byte-shift corruption already suspected elsewhere in it.
/// An earlier attempt at a real accrual formula (crediting a hostileTaxRate% share of personal house-tax
/// payments into the pool via HousingManager.PayWeeklyTax) was built, then reverted whole per this same feedback
/// - do not re-add it.
///
/// **Wire-format note, UPDATED 2026-08-19 (deep RE pass, resolved the stall this comment used to describe)**:
/// the "absurd territory income"/missing-circle symptom was root-caused, not just suspected - a real RTTI-traced
/// deserializer walk of x2game-dev.dll found `DominionData.Write()` sent 8 fields between ReignStartTime and
/// TerritoryData where the real client only reads 3, byte-desyncing everything from TerritoryData onward on
/// every send. The safe/confirmed half of that fix is applied (see Write() below - dropped
/// LastNationalTaxRateChagedTime, LastTaxRateChangedTime now a raw long). **Not yet fixed**: the remaining
/// fields in that same gap (NationalTaxRate/NationalMonumentDbId/X/Y/Z/ObjId) still need a live debugger session
/// to confirm the real replacement, not another static guess - `TerritoryData` itself was separately confirmed
/// byte-correct via the *Zone-side* deserializer (WZDominionData, see below), and `DominionSiegeTimers`'s real
/// wire shape was found to be completely different from this C# model (a per-siege-team roster, not
/// Durations[5]/Bdm) - a real, still-open content gap, not touched. Full trace in the aaemu-siege-castle-hero-
/// nation memory file/`D:\aa\siege-castle-hero-nation-brief.md`.
///
/// **Zone-facing wire format (WZDominionData, opcode 0x0060, World→Zone) - UPDATED 2026-08-20, rebuilt and
/// re-enabled after full ground-truth confirmation.** A 2026-08-19 guessed rebuild of this packet crashed the
/// live Zone process instantly (see the aaemu-zone-wire-format-danger memory - read it before ever touching
/// this again). Root cause: `x2game-dev.dll` and `x2game-dev_dedicate.dll` (the actual AAEmu.ZoneHost.exe
/// binary) are NOT the same file - the original guess was researched against the wrong binary. Redone against
/// the correct one, then fully re-verified field-by-field on 2026-08-20 by reading the raw decompiled C of
/// `DominionData::Read()` and all 5 of its sub-functions directly (not summarized from an intermediate research
/// pass - see `WZDominionDataPacket`'s own doc comment in `WZWorldStatePackets.cs` for the exact function list
/// and offsets). Every field's width/encoding is now ground-truth-confirmed; a small number of scalar fields
/// have unconfirmed *semantic meaning* only (sent as 0, which is safe - see that packet's doc comment for why).
/// `NotifyZoneDominionClaimed` is re-enabled below. **Still treat any further change to this packet with the
/// same caution as before** - this is the exact opcode that already took down a live Zone process once.
/// </summary>
public class DominionManager(ITaskManager taskManager, IExpeditionManager expeditionManager, IGameDataManager gameDataManager, IGuildDominionManager guildDominionManager) : Singleton<DominionManager>, IDominionManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private static readonly TimeSpan TaxPayoutInterval = TimeSpan.FromDays(7);

    // Unused beyond the constructor - its only purpose is telling ManagerOrchestrator's dependency-graph sort
    // that DominionManager.Load() must run in a LATER batch than GameDataManager.Load() (which is what actually
    // populates SiegeGameData.Instance). See the 2026-08-20 fix note on Load() below for the real bug this
    // closes - suppresses CS9113 (unread primary ctor param), same pattern GameDataManager itself already uses
    // for its own ordering-only constructor deps.
    private readonly IGameDataManager _gameDataManager = gameDataManager;

    private Dictionary<ushort, DominionData> _dominions = [];
    private readonly IGuildDominionManager _guildDominionManager = guildDominionManager;
    private Dictionary<ushort, uint> _guardTowerSettingIdByZone = [];
    /// <summary>Current guard_tower_steps progression per zone - manager-side only, not part of DominionData's wire format (unknown fields like SiegeTimers.Bdm/Durations already occupy that struct, not safe to overload).</summary>
    private Dictionary<ushort, int> _guardTowerStepByZone = [];
    /// <summary>
    /// Old (guild-owned) castle system's Keep/Castle/Palace tier per zone (Exeloch/Sungold Fields only - see
    /// AdvanceCastleTier.cs), persisted to the `dominions.castle_tier` column (2026-08-21 migration). Same
    /// manager-side-only reasoning as _guardTowerStepByZone - not part of DominionData's wire format.
    /// </summary>
    private Dictionary<ushort, int> _castleTierByZone = [];
    /// <summary>
    /// The Territory Agent NPC per claimed zone group - World-side only, never persisted (matches the existing
    /// GM /spawn command's behavior for dynamically-created NPCs, per the original 2026-08-19 design note this
    /// carries forward). Tracked here (2026-08-20 fix) so a Zone reconnect/reload can RE-ANNOUNCE the SAME
    /// existing NPC object to Zone instead of calling NpcManager.Create() again, which would otherwise spawn a
    /// second, duplicate NPC every time - see RelayAllToZone's doc comment for the bug this closes.
    /// </summary>
    private readonly Dictionary<ushort, Npc> _territoryAgentByZone = [];
    /// <summary>
    /// Housing template ids already built inside each claimed zone group, via <see cref="TryBuildDominionStructure"/>
    /// - World-side only, not persisted (a server restart re-derives this from the live `housings` table itself,
    /// see that method's own re-population on Load). Prevents a guild spamming the same item (e.g. the Guardian
    /// Altar) into an unlimited pile of duplicate Houses; one of each design per territory, matching
    /// dominion_housings' own one-row-per-group shape for the 5 unique buildings (altar/farm/processing/
    /// logistics/military), see that method's doc comment for the full data trail.
    /// </summary>
    private readonly Dictionary<ushort, HashSet<uint>> _builtStructuresByZone = [];

    public IEnumerable<DominionData> Dominions => _dominions.Values;

    public DominionData GetByZoneId(ushort zoneId) => _dominions.GetValueOrDefault(zoneId);

    public DominionData GetDominionAtPosition(ushort zoneId, float x, float y)
    {
        if (!_dominions.TryGetValue(zoneId, out var dominion))
            return null;

        var dx = x - dominion.X;
        var dy = y - dominion.Y;
        var radius = dominion.TerritoryData?.RadiusDominion ?? 0;
        return dx * dx + dy * dy <= (float)radius * radius ? dominion : null;
    }

    /// <summary>
    /// **Root cause of the missing world-map territory circle, found + fixed 2026-08-20** (see
    /// aaemu-siege-castle-hero-nation memory for the full RE trail that led here). This method calls
    /// `BuildTerritoryData()`, which reads `SiegeGameData.Instance.GetGuardTowerSettings(...)` - but
    /// `SiegeGameData` is populated by `GameDataManager.Load()`, a SEPARATE `ILoadable` with no constructor-
    /// declared dependency forcing it to run before this one. `ManagerOrchestrator`'s batch scheduler
    /// (`BuildBatches&lt;ILoadable&gt;`) only orders managers by constructor-parameter dependencies - with none
    /// declared, `DominionManager` and `GameDataManager` landed in the SAME parallel batch and raced on every
    /// boot. `DominionManager` frequently won the race, so `GetGuardTowerSettings` returned null for a
    /// perfectly real, present row (confirmed live: `guard_tower_settings.id=5` has real non-zero radii in the
    /// live DB - the "row missing" WARN was always a lie, it just hadn't loaded yet) - producing an all-zero
    /// `TerritoryData`. The native client's own circle-drawing code (`FUN_39cfaee0` in x2game-dev.dll, decompiled
    /// directly) requires all 4 radius fields to be &gt; 0 and silently skips drawing (no player-visible error,
    /// just a debug log line) otherwise - a zeroed TerritoryData is a 100% silent, complete explanation for the
    /// missing circle, independent of anything about the SC/WZ DominionData wire-format work done the same day.
    /// **Fixed** by adding `IGameDataManager` as a constructor dependency above (ordering-only, unused
    /// otherwise) - this alone forces the orchestrator to run `GameDataManager.Load()` in an earlier batch.
    /// </summary>
    public void Load()
    {
        _dominions = [];
        _guardTowerSettingIdByZone = [];
        _guardTowerStepByZone = [];
        _castleTierByZone = [];

        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM dominions";
        command.Prepare();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var zoneId = (ushort)reader.GetInt32(reader.GetOrdinal("zone_id"));
            var guardTowerSettingId = (uint)reader.GetInt32(reader.GetOrdinal("guard_tower_setting_id"));
            _guardTowerStepByZone[zoneId] = reader.GetInt32(reader.GetOrdinal("guard_tower_step"));
            _castleTierByZone[zoneId] = reader.GetInt32(reader.GetOrdinal("castle_tier"));

            var expeditionId = (uint)reader.GetInt32(reader.GetOrdinal("expedition_id"));
            var owningFactionId = (uint)reader.GetInt32(reader.GetOrdinal("faction_id"));

            var dominion = new DominionData
            {
                ZoneId = zoneId,
                ExpeditionId = expeditionId,
                OwningFactionId = owningFactionId,
                // Faction-owned rows (owningFactionId != 0) already carry the real alliance id directly - no
                // guild-race derivation needed or wanted. Guild-owned rows (54/56) keep the existing derivation.
                FactionId = owningFactionId != 0 ? (FactionsEnum)owningFactionId : ResolveOwningFaction(expeditionId),
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
                LastNationalTaxRateChagedTime = reader.GetDateTime(reader.GetOrdinal("last_national_tax_rate_changed_time")),
                NationalTaxRate = (ushort)reader.GetInt32(reader.GetOrdinal("national_tax_rate")),
                NationalMonumentDbId = reader.GetInt64(reader.GetOrdinal("national_monument_db_id")),
                NationalMonumentX = reader.GetFloat(reader.GetOrdinal("national_monument_x")),
                NationalMonumentY = reader.GetFloat(reader.GetOrdinal("national_monument_y")),
                NationalMonumentZ = reader.GetFloat(reader.GetOrdinal("national_monument_z")),
                ObjId = 0,
                TerritoryData = BuildTerritoryData(guardTowerSettingId),
                SiegeTimers = new DominionSiegeTimers
                {
                    Durations = [0, 0, 0, 0, 0],
                    Started = DateTime.MinValue,
                    Fixed = DateTime.MinValue,
                    Bdm = 0,
                    SiegePeriod = (byte)reader.GetInt32(reader.GetOrdinal("siege_period")),
                    UnkData = EmptyUnkData(),
                    Unk2Data = EmptyUnkData()
                },
                NonPvPStart = reader.GetDateTime(reader.GetOrdinal("non_pvp_start")),
                NonPvPDuration = (ushort)reader.GetInt32(reader.GetOrdinal("non_pvp_duration"))
            };

            _dominions[zoneId] = dominion;
            _guardTowerSettingIdByZone[zoneId] = guardTowerSettingId;
        }

        Logger.Info("Loaded {0} dominions", _dominions.Count);

        taskManager.Schedule(new DominionTaxPayoutTask(), TimeSpan.FromMinutes(2), TimeSpan.FromHours(1));
    }

    /// <summary>
    /// Guild-claim path (Exeloch/Sungold Fields, zone groups 54/56). <paramref name="expeditionId"/> is the
    /// real ownership; FactionId on the resulting DominionData is only derived for the wire's display purposes.
    /// </summary>
    public DominionData Declare(ushort zoneId, uint expeditionId, House lodestone, Character declarer) =>
        Declare(zoneId, expeditionId, 0, lodestone, declarer);

    /// <summary>
    /// Hero/faction-claim path (the 4 siege_zones territories, zone groups 33/34/43/44) - real ownership is
    /// <paramref name="owningFactionId"/> (the declaring Hero's own alliance, 148 Nuia / 149 Haranya), NOT tied
    /// to any guild. <paramref name="declarer"/>'s eligibility (must be their faction's currently-elected Hero)
    /// is the caller's responsibility to check first - see DeclareDominion.cs.
    /// </summary>
    public DominionData DeclareForFaction(ushort zoneId, uint owningFactionId, House lodestone, Character declarer) =>
        Declare(zoneId, 0, owningFactionId, lodestone, declarer);

    private DominionData Declare(ushort zoneId, uint expeditionId, uint owningFactionId, House lodestone, Character declarer)
    {
        if (_dominions.ContainsKey(zoneId))
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
            OwningFactionId = owningFactionId,
            FactionId = owningFactionId != 0
                ? (FactionsEnum)owningFactionId
                : (ResolveOwningFaction(declarer) is var declarerFaction && declarerFaction != FactionsEnum.Invalid
                    ? declarerFaction
                    : ResolveOwningFaction(expeditionId)),
            House = lodestone.Id,
            // No taxes for the castle system - user's explicit call, 2026-08-19 ("idk why it is in the housing
            // system in the first place"). Zeroed rather than removed from the wire struct (still fragile/
            // unconfirmed - see the wire-format research note below), so PayoutTax's own total<=0 guard makes
            // the weekly tax-mail task a permanent no-op without touching its scheduling.
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
            LastNationalTaxRateChagedTime = now,
            // Restored 2026-08-19 after being zeroed alongside the housing-style Dominion tax: research
            // confirmed this is a genuinely separate, real Nation-level tax/receipt-mail system
            // (MAIL_NATIONAL_TAX_RATE / national_tax_receipt strings, its own mail type) - not the mechanic the
            // user objected to. Not otherwise wired up yet (no accrual, no payout) - a legitimate future
            // feature, kept at a sane non-zero default rather than left at 0.
            NationalTaxRate = 500,
            NationalMonumentDbId = 0,
            NationalMonumentX = 0,
            NationalMonumentY = 0,
            NationalMonumentZ = 0,
            ObjId = 0,
            TerritoryData = BuildTerritoryData(guardTowerSettingId),
            SiegeTimers = new DominionSiegeTimers
            {
                Bdm = 0,
                Durations = [0, 0, 0, 0, 0],
                Fixed = DateTime.MinValue,
                Started = DateTime.MinValue,
                SiegePeriod = 1,
                UnkData = EmptyUnkData(),
                Unk2Data = EmptyUnkData()
            },
            NonPvPDuration = 0,
            NonPvPStart = now
        };

        _dominions[zoneId] = dominion;
        _guardTowerSettingIdByZone[zoneId] = guardTowerSettingId;
        _guardTowerStepByZone[zoneId] = 0;
        Insert(dominion, guardTowerSettingId);
        NotifyZoneDominionClaimed(dominion, lodestone.Transform.ZoneId);

        // Guild-facing ownership on the House itself: House.cs:293 treats AccountId<=0 || OwnerId<=0 as "no
        // owner" for display purposes (the seeded lodestone rows start at 0/0). Actual game-logic ownership is
        // Expedition-based via dominion.ExpeditionId above; OwnerId here just needs to point at a current
        // member so the existing Expedition-based AllowedToInteract() check keeps working, matching how other
        // Expedition-gated housing in this codebase is already modeled (see HousingManager.Build's Expedition
        // gate). Known limitation, not fixed here: if this specific character later leaves the guild, this
        // field goes stale until someone re-declares - no ownership-transfer-on-leave exists yet.
        if (declarer != null)
        {
            lodestone.OwnerId = declarer.Id;
            lodestone.CoOwnerId = declarer.Id;
            lodestone.AccountId = declarer.AccountId;
            // Without this, only a fresh zone-enter (which re-sends full House state) picks up the new
            // owner - anyone already observing the lodestone at claim time keeps seeing the stale 0/0
            // "no owner" state until they leave and re-enter. Matches the live-refresh push
            // HousingManager.Sell already does on ownership change (SCHouseDataPacket to observers).
            lodestone.BroadcastPacket(new SCHouseDataPacket([lodestone]), true);
            // SCHouseDataPacket alone updates only the World->Client channel. This architecture's real-time
            // interaction/tooltip queries are answered by the native Zone process against its OWN cached
            // House state (Zone is sim authority - see aaemu-server-overview memory), which never gets told
            // about this change unless we also push it over World->Zone, same as HousingManager.
            // CreateDominionHouse already does for a brand-new House (NotifyZoneHouseCreated right after its
            // own SCHouseDataPacket send). First attempt at this fix only did the SC half - still needed a
            // zone re-entry to show the new owner, exactly because this half was missing.
            if (WorldIntegration.ZoneAuthority)
                HousingZoneBridge.NotifyZoneHouseCreated(lodestone);
        }

        // Visual claim state, REAL root cause found: the lodestone House is the generic per-house
        // housing_build_steps system (House.CurrentStep/ModelId), not a buff-driven swap. Every guard-tower
        // template (139/184-192/271/272) has exactly one housing_build_steps row - step 0, model_id 893 (the
        // buried/pre-claim lodestone), num_actions 1, skill_id 13661 (== the DeclareDominion "Purifying Archeum"
        // skill itself, confirming this step is meant to complete at declare time) - and main_model_id 894 is the
        // risen/floating tower. The seed script correctly left every lodestone at current_step=0, but nothing
        // ever advanced it, so it was permanently stuck on the buried model until a GM ran /build on it by hand
        // (BuildHouse.cs's House.AddBuildAction()). Completing that single build action here reproduces exactly
        // what /build does: flips CurrentStep to -1, swaps ModelId to Template.MainModelId (894, the floating
        // tower), and creates the template's bound doodads (Template.HousingBindingDoodad) - matching House.cs's
        // CurrentStep setter. This is also the likely cause of the Territory Agent NPC being unclickable/
        // "spawning inside the lodestone" noted previously: it was placed against a House frozen on the buried
        // model's (smaller/underground) collision footprint.
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

        // guard_tower_settings.initial_buff_id (buff 4771 in this build, "수호의 시작" / "The Guard Tower has
        // risen, the territory has been declared") - a status/gameplay buff marking the claim, kept alongside the
        // model-swap fix above rather than relied on for the visual change itself (buffs don't drive House.ModelId
        // in this codebase's architecture).
        if (dominion.TerritoryData?.Id2 is { } initialBuffId and > 0)
            lodestone.Buffs.AddBuff(initialBuffId, lodestone);

        // Territory Agent NPC (siege_zones.dominion_merchant_id, e.g. 15609 "누이마리 영지 대리인" for Nuimari) -
        // the dominion vendor/agent that should appear once a zone group is claimed. Confirmed real: registered
        // in npc_spawner_npcs but never once observed spawning anywhere in this build's live zone data (see the
        // "closed zone saga" investigation earlier this session) - this was the missing trigger, not missing
        // data. Extracted into EnsureTerritoryAgentNpc (2026-08-20) so RelayAllToZone can reuse the exact same
        // logic on a Zone reconnect, not just the original claim.
        EnsureTerritoryAgentNpc(dominion, lodestone);

        var expedition = expeditionManager.Expeditions.FirstOrDefault(e => (uint)e.Id == expeditionId);
        var territoryName = lodestone.Template?.Name;
        // Confirmed via server log that this packet DOES send (S->C type 299 SCWorldMessagePacket right at
        // declare) - if it's still not visible client-side, the likely remaining cause is the message string
        // itself: every real world_message_effects row uses |cffXXXXXX...|r WoW-style color-code markup (see
        // world_message_effects id 26/30/31, the declare-countdown messages, which DO render correctly - only
        // this custom one doesn't). Matching that convention here rather than sending unstyled plain text,
        // which the client's rich-text renderer may silently drop instead of falling back to plain display.
        var guildName = owningFactionId != 0
            ? ((FactionsEnum)owningFactionId) switch
            {
                FactionsEnum.NuiaAlliance => "The Nuia Alliance",
                FactionsEnum.HaranyaAlliance => "The Haranya Alliance",
                _ => "A faction"
            }
            : expedition?.Name ?? "A guild";
        var placeName = string.IsNullOrEmpty(territoryName) ? "a territory" : territoryName;
        var announcement = $"|cffe7ce25{guildName}|r |cFF87CEFAhas successfully claimed |cFFFFFFFF{placeName}|r|cFF87CEFA!|r";
        WorldManager.Instance.BroadcastPacketToServer(new SCWorldMessagePacket(0, 1, announcement));

        WorldManager.Instance.BroadcastPacketToServer(new SCDominionDataPacket(dominion, true, true));

        // Real bug found + fixed 2026-08-19, same class as ExpeditionManager.SaveCharacterExpeditionNow: every
        // House field this method sets above (OwnerId/CoOwnerId/AccountId, CurrentStep via AddBuildAction) only
        // marks the House dirty in memory - House.Save() is otherwise deferred to SaveManager's periodic tick or
        // a graceful shutdown. A hard World restart (Stop-Process, crash) inside that window silently reverted
        // the whole claim's visual/ownership state back to the seeded row (owner=0, current_step=0) while
        // `dominions` itself stayed correct (Insert() above is an immediate, explicit write) - confirmed live
        // this session across several routine dev restarts. Force an immediate save so a claim survives any
        // restart from the moment it's declared, not just after the next autosave tick happens to land.
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

    public void UpdateTaxRate(GameConnection connection, ushort zoneId, int taxRate)
    {
        var character = connection?.ActiveChar;
        if (character == null)
            return;

        if (!_dominions.TryGetValue(zoneId, out var dominion))
            return;

        // TODO: real valid tax-rate bounds haven't been confirmed against content_configs/game rules yet -
        // only rejecting the obviously-invalid negative case for now.
        if (taxRate < 0)
        {
            character.SendErrorMessage(ErrorMessageType.Invalid);
            return;
        }

        var hasPermission = dominion.OwningFactionId != 0
            ? ResolveOwningFaction(character) == (FactionsEnum)dominion.OwningFactionId && HeroManager.Instance.IsCurrentHero(character)
            : character.Expedition != null && (uint)character.Expedition.Id == dominion.ExpeditionId;
        if (!hasPermission)
        {
            character.SendErrorMessage(ErrorMessageType.NoPerm);
            return;
        }

        dominion.TaxRate = taxRate;
        dominion.LastTaxRateChangedTime = DateTime.UtcNow;

        using (var mysqlConnection = MySQL.CreateConnection())
        using (var command = mysqlConnection.CreateCommand())
        {
            command.CommandText = "UPDATE dominions SET tax_rate = @taxRate, last_tax_rate_changed_time = @changed WHERE zone_id = @zoneId";
            command.Parameters.AddWithValue("@taxRate", dominion.TaxRate);
            command.Parameters.AddWithValue("@changed", dominion.LastTaxRateChangedTime);
            command.Parameters.AddWithValue("@zoneId", zoneId);
            command.Prepare();
            command.ExecuteNonQuery();
        }

        WorldManager.Instance.BroadcastPacketToServer(new SCDominionTaxRatePacket(zoneId, dominion.TaxRate));
    }

    public void SetNationalMonument(ushort zoneId, long dbId, float x, float y, float z)
    {
        if (!_dominions.TryGetValue(zoneId, out var dominion))
            return;

        dominion.NationalMonumentDbId = dbId;
        dominion.NationalMonumentX = x;
        dominion.NationalMonumentY = y;
        dominion.NationalMonumentZ = z;

        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dominions
            SET national_monument_db_id = @dbId, national_monument_x = @x, national_monument_y = @y, national_monument_z = @z
            WHERE zone_id = @zoneId
            """;
        command.Parameters.AddWithValue("@dbId", dbId);
        command.Parameters.AddWithValue("@x", x);
        command.Parameters.AddWithValue("@y", y);
        command.Parameters.AddWithValue("@z", z);
        command.Parameters.AddWithValue("@zoneId", zoneId);
        command.Prepare();
        command.ExecuteNonQuery();
    }

    public void PayoutTax()
    {
        var now = DateTime.UtcNow;
        foreach (var dominion in _dominions.Values)
        {
            if (now - dominion.LastPaidTime < TaxPayoutInterval)
                continue;

            var total = dominion.CurHouseTaxMoney + dominion.CurHuntTaxMoney + dominion.PeaceTaxMoney;
            if (total <= 0)
            {
                dominion.LastPaidTime = now;
                PersistTaxPool(dominion);
                continue;
            }

            var expedition = expeditionManager.Expeditions.FirstOrDefault(e => (uint)e.Id == dominion.ExpeditionId);
            var receiverName = expedition != null ? NameManager.Instance.GetCharacterName(expedition.OwnerId) : null;
            if (receiverName == null)
            {
                Logger.Warn("DominionManager.PayoutTax: zone {0} owning Expedition/leader not found, leaving {1} in the pool", dominion.ZoneId, total);
                continue;
            }

            var mail = new BaseMail
            {
                MailType = MailType.NationTaxReceipt,
                Title = "Dominion Tax",
                ReceiverName = receiverName
            };
            mail.Header.ReceiverId = expedition.OwnerId;
            mail.Header.Status = MailStatus.Unread;
            mail.Body.Text = $"House tax: {dominion.CurHouseTaxMoney}, Hunt tax: {dominion.CurHuntTaxMoney}, Peace tax: {dominion.PeaceTaxMoney}";
            mail.Body.CopperCoins = total;
            mail.Body.RecvDate = now;
            mail.Send();

            dominion.CurHouseTaxMoney = 0;
            dominion.CurHuntTaxMoney = 0;
            dominion.PeaceTaxMoney = 0;
            dominion.LastPaidTime = now;
            PersistTaxPool(dominion);

            Logger.Info("Dominion tax payout: zone {0}, {1} copper to {2}", dominion.ZoneId, total, receiverName);
        }
    }

    private void PersistTaxPool(DominionData dominion)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE dominions
            SET cur_house_tax_money = @house, cur_hunt_tax_money = @hunt, peace_tax_money = @peace, last_paid_time = @paid
            WHERE zone_id = @zoneId
            """;
        command.Parameters.AddWithValue("@house", dominion.CurHouseTaxMoney);
        command.Parameters.AddWithValue("@hunt", dominion.CurHuntTaxMoney);
        command.Parameters.AddWithValue("@peace", dominion.PeaceTaxMoney);
        command.Parameters.AddWithValue("@paid", dominion.LastPaidTime);
        command.Parameters.AddWithValue("@zoneId", dominion.ZoneId);
        command.Prepare();
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// DISABLED 2026-08-19 - DO NOT RE-ENABLE without a Ghidra-confirmed WZDominionData (opcode 0x0060) field
    /// layout. Live-tested via /dominionresync: the instant this send fired, the native Zone process for that
    /// zone crashed/disconnected (confirmed in the server log - "WZDominionData → zone group=34..." followed in
    /// the same second by "Zone lost (zone TCP disconnect)"). The opcode itself is real (confirmed via earlier
    /// dev-DLL string mining), but the field layout in WZDominionDataPacket was an unverified best-effort guess
    /// (two Ghidra research passes on this specific packet family both hit a wall - see DominionManager's
    /// wire-format doc comment further up this file) and it is fatally incompatible with what the closed-source
    /// native binary actually expects. Unlike a wrong client-facing packet (which just renders incorrectly),
    /// this crashes the whole Zone process for every player in it - a much higher-risk failure mode. Left as a
    /// no-op rather than deleted so the plumbing (WorldIntegration.RelayDominionClaimedToZone, Program.cs
    /// wiring, the WZDominionDataPacket class itself) is ready to re-enable the moment a real field layout is
    /// confirmed - do not simply uncomment the call below without that.
    /// </summary>
    /// <summary>
    /// Matches WZDominionDataPacket.RequiredPaddingBytes in AAEmu.WorldServer (can't reference that constant
    /// directly - AAEmu.Game doesn't/shouldn't depend on AAEmu.WorldServer, hence the WorldIntegration delegate
    /// indirection this whole method exists to support). Keep these two values in sync if either changes - see
    /// that packet class's doc comment and the aaemu-siege-castle-hero-nation memory's 2026-08-20 bisection
    /// entry for why this specific number, and why it's still an empirical stand-in, not a real fix.
    /// </summary>
    private const int DominionZonePaddingBytes = 36;

    /// <summary>internal, not private: GuildDominionManager's own Declare/RelayAllToZone reuse this directly (2026-08-24) - same reasoning as BuildTerritoryData/EmptyUnkData below, this is proven wire-format plumbing, not manager-specific decision logic.</summary>
    internal static void NotifyZoneDominionClaimed(DominionData dominion, uint rawZoneId, int diagnosticPaddingBytes = DominionZonePaddingBytes)
    {
        // RE-ENABLED 2026-08-20, per explicit user direction: a Zone-process crash here has no real
        // consequence in this dev environment (no players, auto-restarts, no persisted damage) - the user
        // wants live iteration instead of more static analysis. Multiple crashes so far (2026-08-19, 2026-08-20)
        // each had a real, identifiable cause found via post-mortem log analysis of BOTH World's own log
        // (WZDominionData → ... immediately followed by a zone TCP disconnect in the same second) AND Zone's
        // own per-zone log under C:\AAEmuRuntime\ZoneManager\Logs\<zoneKey>-<name>\ArcheAge-...log, which turned
        // out to carry the real native error text ("not enough buffer for <field>" / "serializer size mismatch"
        // - a graceful, bounds-checked parse failure, not a memory-corruption crash - no .dmp is produced). See
        // aaemu-zone-wire-format-danger memory for the full technique and history. If this crashes again, check
        // BOTH logs before assuming anything.
        if (!WorldIntegration.ZoneAuthority || rawZoneId == 0)
            return;

        WorldIntegration.RelayDominionClaimedToZone?.Invoke(rawZoneId, dominion, diagnosticPaddingBytes);
    }

    /// <summary>
    /// <summary>
    /// **Root cause of the Territory Agent NPC disappearing + territory placement silently failing after any
    /// length of play, found + fixed 2026-08-20.** Zone hosts are separate native processes from World and
    /// routinely unload/reload independently (confirmed live: zone group 33's raw zone auto-unloaded and
    /// reconnected 8+ times over a couple hours of normal play, unrelated to any crash - this is the existing,
    /// by-design `AutoZoneIdleSeconds` unload-when-idle behavior, see aaemu-fixes-applied memory). Zone persists
    /// nothing itself - a freshly (re)loaded Zone process starts with zero awareness of anything, and only
    /// knows about a Dominion claim or the Territory Agent NPC if World explicitly re-tells it. Houses already
    /// had this handled (`HousingManager.RelayAllToZone`, wired to the real `ZwOpcodes.ZoneLoaded` event) -
    /// Dominion claims and the Territory Agent NPC never did; `NotifyZoneDominionClaimed`/`EnsureTerritoryAgentNpc`
    /// were only ever called from `Declare()` (once, at the original claim) and manual `/dominionresync` - so
    /// every single zone-reload since the last manual resync silently wiped Zone's own knowledge that the
    /// territory was ever claimed (explaining "placement always fails, no matter where" - Zone itself didn't
    /// think ANY of the housing_area plots belonged to a claimed Dominion) and the NPC was never told to Zone
    /// again at all (explaining it disappearing outright, on BOTH claimed territories, exactly as observed live).
    /// **Fixed** by wiring this method to the same `ZwOpcodes.ZoneLoaded` event housing already uses
    /// (`WorldIntegration.NotifyZoneReadyForDominion`, `AAEmu.WorldServer\...\ZoneProtocolHandler.cs`) - see
    /// there for the exact hook.
    /// </summary>
    public void RelayAllToZone(uint rawZoneId)
    {
        if (!WorldIntegration.ZoneAuthority)
            return;

        foreach (var dominion in _dominions.Values)
        {
            var house = HousingManager.Instance.GetHouseById(dominion.House);
            if (house?.Transform == null || house.Transform.ZoneId != rawZoneId)
                continue;

            NotifyZoneDominionClaimed(dominion, rawZoneId);
            EnsureTerritoryAgentNpc(dominion, house);
        }
    }

    /// <summary>
    /// Spawns the Territory Agent NPC for a claimed Dominion, or - if one already exists for this zone group
    /// (tracked in `_territoryAgentByZone`) - just re-announces the SAME existing World-side object to Zone
    /// instead of creating a second one. This distinction matters specifically because `RelayAllToZone` can now
    /// call this repeatedly across many Zone reloads over a single World session (see that method's doc
    /// comment) - blindly calling `NpcManager.Create()` every time would spawn a new duplicate NPC on top of
    /// the previous one(s) each time Zone reconnects, since the World-side NPC object itself survives Zone
    /// reloads even though Zone's own awareness of it doesn't.
    /// </summary>
    private void EnsureTerritoryAgentNpc(DominionData dominion, House lodestone)
    {
        if (_territoryAgentByZone.TryGetValue(dominion.ZoneId, out var existing) && existing != null)
        {
            if (!WorldIntegration.PublishNpcSpawn(existing))
                WorldIntegration.DeleteNpcMirror(existing, false);
            return;
        }

        var merchantNpcId = SiegeGameData.Instance.GetSiegeZoneSchedule(dominion.ZoneId)?.DominionMerchantId ?? 0;
        if (merchantNpcId <= 0 || !NpcManager.Instance.Exist(merchantNpcId) || lodestone.ParentWorld == null)
            return;

        var merchant = NpcManager.Instance.Create(lodestone.ParentWorld, 0, merchantNpcId);
        if (merchant == null)
            return;

        // 2026-08-20 fix: previously cloned the lodestone's transform directly (zero offset), placing the
        // merchant at the EXACT same coordinates as the guard-tower building itself - embedded in its own
        // collision. Live-confirmed real consequences, not just cosmetic: the Nuimari merchant was visibly
        // "stuck in the lodestone model", and the Salpimari one was found to have actually DIED shortly after
        // spawning (Npc.DoDie's zone-mirror corpse-cleanup path fired ~15-20s later, removing it entirely) -
        // consistent with the engine having a "stuck in solid geometry" safety-kill mechanic. Same offset
        // technique already proven in this codebase for exactly this class of problem (SpawnDoodad.cs): step a
        // fixed distance out in front of the source object's own facing, then snap to real terrain height
        // rather than trusting the source's own Z (a guard tower's risen-model Z may not match ground level at
        // the offset point).
        var rpy = lodestone.Transform.World.ToRollPitchYawDegrees();
        var (offsetX, offsetY) = MathUtil.AddDistanceToFrontDeg(
            12f, lodestone.Transform.World.Position.X, lodestone.Transform.World.Position.Y, rpy.Z);
        var offsetZ = lodestone.ParentWorld.Template.GeoData.GetHeight(new Vector3(offsetX, offsetY, lodestone.Transform.World.Position.Z));

        merchant.Transform = lodestone.Transform.CloneDetached(merchant);
        merchant.SetPosition(offsetX, offsetY, offsetZ, rpy.X, rpy.Y, rpy.Z);
        merchant.IsZoneMirror = true;
        merchant.Spawn();
        if (!WorldIntegration.PublishNpcSpawn(merchant))
            WorldIntegration.DeleteNpcMirror(merchant, false);

        _territoryAgentByZone[dominion.ZoneId] = merchant;
    }

    /// <summary>
    /// GM/manual catch-up: re-sends WZDominionData to Zone AND a fresh SCDominionDataPacket to every connected
    /// client, for an already-claimed zone group, without a full unclaim/reclaim. The SC-side broadcast was
    /// added 2026-08-20 - previously this only refreshed Zone's own awareness, so a client that had already
    /// received an older (possibly still-desynced, pre-header-fix) SCDominionDataPacket earlier in the session
    /// would keep showing stale/wrong map data until their next relog/zone-enter - not what "resync" should mean.
    /// </summary>
    public bool ResyncZone(ushort zoneId)
    {
        if (!_dominions.TryGetValue(zoneId, out var dominion))
            return false;

        var house = HousingManager.Instance.GetHouseById(dominion.House);
        if (house?.Transform == null)
            return false;

        WorldManager.Instance.BroadcastPacketToServer(new SCDominionDataPacket(dominion, true, true));

        NotifyZoneDominionClaimed(dominion, house.Transform.ZoneId);
        return true;
    }

    public bool ResyncZoneWithZeroedTestData(ushort zoneId, int diagnosticPaddingBytes = 0)
    {
        if (!_dominions.TryGetValue(zoneId, out var dominion))
            return false;

        var house = HousingManager.Instance.GetHouseById(dominion.House);
        if (house?.Transform == null)
            return false;

        var zeroed = new DominionData
        {
            ZoneId = dominion.ZoneId,
            ExpeditionId = dominion.ExpeditionId,
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
            LastNationalTaxRateChagedTime = DateTime.MinValue,
            NationalTaxRate = 0,
            NationalMonumentDbId = 0,
            NationalMonumentX = 0,
            NationalMonumentY = 0,
            NationalMonumentZ = 0,
            ObjId = 0,
            TerritoryData = new DominionTerritoryData(), // real (non-null) instance so the wire structure stays the same shape, just zero-valued
            SiegeTimers = new DominionSiegeTimers { SiegePeriod = 0 },
            NonPvPStart = DateTime.MinValue,
            NonPvPDuration = 0
        };

        NotifyZoneDominionClaimed(zeroed, house.Transform.ZoneId, diagnosticPaddingBytes);
        return true;
    }

    /// <summary>
    /// GM/testing tool, added 2026-08-21 on explicit user request to reset territories for re-testing the
    /// claim flow. Reverses everything Declare() does - see this method's doc comment on IDominionManager for
    /// the full behavior. Deliberately does NOT send any World->Zone "unclaim" wire signal: WZDominionData
    /// (the only Zone-facing Dominion packet) has a documented history of crashing the Zone process on an
    /// unconfirmed field layout (see NotifyZoneDominionClaimed's doc comment above) and no "claim removed"
    /// variant of it has ever been reverse-engineered or tested - inventing one here would repeat that exact
    /// mistake. Zone's own cached awareness of the old claim is stale until its next reload (matches the
    /// existing, already-accepted behavior of RelayAllToZone/ResyncZone being the only things that push
    /// Dominion state to Zone) - re-claiming the SAME zone group via Declare()/ClaimTerritory afterward
    /// re-sends WZDominionData for the new claim and corrects it either way.
    /// </summary>
    /// <summary>
    /// 2026-08-24: the guild/Hero-faction dominion split moved Exeloch/Sungold (54/56) out of `_dominions`
    /// entirely, into GuildDominionManager - but nation-founding (the only caller of this method, see
    /// NationManager.DeclareIndependence) is exclusively scoped to those exact two zones
    /// (NationFoundableZoneGroups). So the normal path here is now a cross-manager migration, not an in-place
    /// update: pull the full live state (guard tower step, castle tier, dedup-built structures - not just the
    /// DominionData itself) out of GuildDominionManager via RemoveForTransfer, then insert it here as a real
    /// Hero/faction-owned dominion. The old in-place branch is kept first for robustness (harmless no-op today
    /// since nothing currently calls this for an already-`_dominions`-resident zone) rather than assuming it can
    /// never happen.
    /// </summary>
    public bool TransferToFaction(ushort zoneId, uint newOwningFactionId)
    {
        if (_dominions.TryGetValue(zoneId, out var existing))
        {
            existing.ExpeditionId = 0;
            existing.OwningFactionId = newOwningFactionId;
            existing.FactionId = (FactionsEnum)newOwningFactionId;

            using (var connection = MySQL.CreateConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "UPDATE dominions SET expedition_id = 0, faction_id = @factionId WHERE zone_id = @zoneId";
                command.Parameters.AddWithValue("@factionId", newOwningFactionId);
                command.Parameters.AddWithValue("@zoneId", zoneId);
                command.Prepare();
                command.ExecuteNonQuery();
            }

            WorldManager.Instance.BroadcastPacketToServer(new SCDominionDataPacket(existing, true, true));
            Logger.Info("Dominion zone {0} transferred to nation faction {1} (was already Hero/faction-owned)", zoneId, newOwningFactionId);
            return true;
        }

        var state = _guildDominionManager.RemoveForTransfer(zoneId);
        if (state == null)
            return false;

        var dominion = state.Dominion;
        dominion.ExpeditionId = 0;
        dominion.OwningFactionId = newOwningFactionId;
        dominion.FactionId = (FactionsEnum)newOwningFactionId;
        // guild_dominions never carried these Hero/faction-only fields - seed them the same defaults Declare()
        // uses for a brand-new claim rather than leaving them at DominionData's zeroed guild-side values.
        var now = DateTime.UtcNow;
        dominion.NationalTaxRate = 500;
        dominion.NationalMonumentDbId = 0;
        dominion.NationalMonumentX = 0;
        dominion.NationalMonumentY = 0;
        dominion.NationalMonumentZ = 0;
        dominion.LastNationalTaxRateChagedTime = now;

        _dominions[zoneId] = dominion;
        _guardTowerSettingIdByZone[zoneId] = state.GuardTowerSettingId;
        _guardTowerStepByZone[zoneId] = state.GuardTowerStep;
        _castleTierByZone[zoneId] = state.CastleTier;
        if (state.BuiltStructures.Count > 0)
            _builtStructuresByZone[zoneId] = state.BuiltStructures;

        Insert(dominion, state.GuardTowerSettingId);
        using (var connection = MySQL.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE dominions SET guard_tower_step = @step, castle_tier = @tier WHERE zone_id = @zoneId";
            command.Parameters.AddWithValue("@step", state.GuardTowerStep);
            command.Parameters.AddWithValue("@tier", state.CastleTier);
            command.Parameters.AddWithValue("@zoneId", zoneId);
            command.Prepare();
            command.ExecuteNonQuery();
        }

        WorldManager.Instance.BroadcastPacketToServer(new SCDominionDataPacket(dominion, true, true));
        Logger.Info("Dominion zone {0} transferred from the guild system to nation faction {1}", zoneId, newOwningFactionId);
        return true;
    }

    /// <summary>Reverse of the guild-origin branch in <see cref="TransferToFaction"/> - see that method's doc comment. Only meaningful for zone 54/56 (nation disband).</summary>
    public bool TransferToGuild(ushort zoneId, uint expeditionId)
    {
        if (!_dominions.TryGetValue(zoneId, out var dominion))
            return false;

        var guardTowerSettingId = _guardTowerSettingIdByZone.GetValueOrDefault(zoneId);
        var guardTowerStep = _guardTowerStepByZone.GetValueOrDefault(zoneId);
        var castleTier = _castleTierByZone.GetValueOrDefault(zoneId);
        _builtStructuresByZone.Remove(zoneId, out var builtStructures);

        _dominions.Remove(zoneId);
        _guardTowerSettingIdByZone.Remove(zoneId);
        _guardTowerStepByZone.Remove(zoneId);
        _castleTierByZone.Remove(zoneId);

        using (var connection = MySQL.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM dominions WHERE zone_id = @zoneId";
            command.Parameters.AddWithValue("@zoneId", zoneId);
            command.Prepare();
            command.ExecuteNonQuery();
        }

        var state = new GuildDominionTransferState(dominion, guardTowerSettingId, guardTowerStep, castleTier, builtStructures ?? []);
        _guildDominionManager.AdoptFromNationTransfer(state, expeditionId);

        WorldManager.Instance.BroadcastPacketToServer(new SCDominionDataPacket(dominion, true, true));
        Logger.Info("Dominion zone {0} transferred back to guild {1} (moved back into the guild system)", zoneId, expeditionId);
        return true;
    }

    public bool UnclaimTerritory(ushort zoneId)
    {
        if (!_dominions.TryGetValue(zoneId, out var dominion))
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
            // Setter cleans up AttachedDoodads and swaps ModelId back to the buried step-0 model - same
            // reversal AddBuildAction()/CurrentStep's own doc comment in House.cs describes for the forward
            // direction.
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
            // 2026-08-25: real bug found live - if this lookup ever misses (House not yet loaded, a stale
            // dominion.House id, whatever the actual trigger is - not fully root-caused), the code below still
            // deletes the dominions row regardless, leaving the House itself stuck fully-built and owned by
            // whoever last held it, with nothing left tracking the claim at all. Confirmed live: exactly this
            // state on Salpimari/Nuimari's lodestones, fixed by hand via direct DB UPDATE. Log loudly so this
            // is never silently missed again - a null here means an UnclaimTerritory call is about to leave an
            // orphaned House behind.
            Logger.Error(
                "UnclaimTerritory: House {0} for zone {1} not found via HousingManager.GetHouseById - " +
                "the dominion record is being removed but the House itself CANNOT be reset (owner/model will " +
                "stay stuck in its last claimed state). This is a data-integrity risk, needs manual DB fixup.",
                dominion.House, zoneId);
        }

        if (_territoryAgentByZone.TryGetValue(zoneId, out var agent) && agent != null)
        {
            WorldIntegration.DeleteNpcMirror(agent, true);
            _territoryAgentByZone.Remove(zoneId);
        }

        _dominions.Remove(zoneId);
        _guardTowerSettingIdByZone.Remove(zoneId);
        _guardTowerStepByZone.Remove(zoneId);
        _castleTierByZone.Remove(zoneId);

        using (var connection = MySQL.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM dominions WHERE zone_id = @zoneId";
            command.Parameters.AddWithValue("@zoneId", zoneId);
            command.Prepare();
            command.ExecuteNonQuery();
        }

        // Tell already-online clients this territory is unclaimed now, same SC-side courtesy Declare()/
        // ResyncZone already extend - real ZoneId kept so the client can match it against the old entry,
        // everything else zeroed/defaulted to a genuinely-blank DominionData.
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
            LastNationalTaxRateChagedTime = DateTime.MinValue,
            NationalTaxRate = 0,
            NationalMonumentDbId = 0,
            NationalMonumentX = 0,
            NationalMonumentY = 0,
            NationalMonumentZ = 0,
            ObjId = 0,
            TerritoryData = new DominionTerritoryData(),
            SiegeTimers = new DominionSiegeTimers
            {
                Durations = [0, 0, 0, 0, 0],
                Started = DateTime.MinValue,
                Fixed = DateTime.MinValue,
                Bdm = 0,
                SiegePeriod = 0,
                UnkData = EmptyUnkData(),
                Unk2Data = EmptyUnkData()
            },
            NonPvPStart = DateTime.MinValue,
            NonPvPDuration = 0
        };
        WorldManager.Instance.BroadcastPacketToServer(new SCDominionDataPacket(cleared, true, true));

        Logger.Info("Dominion zone {0} unclaimed via GM tool (was Expedition {1}, Faction {2})",
            zoneId, dominion.ExpeditionId, dominion.OwningFactionId);
        return true;
    }

    /// <summary>See IDominionManager's doc comment.</summary>
    public DominionData ClaimTerritory(ushort zoneId, Models.Game.Expeditions.Expedition expedition, Models.Game.Char.Character declarer)
    {
        if (expedition == null || _dominions.ContainsKey(zoneId))
            return null;

        var templateId = SiegeGameData.Instance.GetLodestoneHousingTemplateId(zoneId);
        if (templateId == null)
            return null;

        var lodestone = HousingManager.Instance.GetAllHouses().FirstOrDefault(h => h.TemplateId == templateId.Value);
        if (lodestone == null)
            return null;

        return Declare(zoneId, (uint)expedition.Id, lodestone, declarer);
    }

    /// <summary>
    /// GM/testing counterpart to <see cref="ClaimTerritory"/> for the 4 Hero/faction-only territories - claims
    /// directly for a faction, bypassing the normal Hero-eligibility check in DeclareDominion.cs (this is a GM
    /// tool, same trust level as /claimterritory's existing guild path).
    /// </summary>
    public DominionData ClaimTerritoryForFaction(ushort zoneId, FactionsEnum factionId, Models.Game.Char.Character declarer)
    {
        if (factionId == FactionsEnum.Invalid || _dominions.ContainsKey(zoneId))
            return null;

        var templateId = SiegeGameData.Instance.GetLodestoneHousingTemplateId(zoneId);
        if (templateId == null)
            return null;

        var lodestone = HousingManager.Instance.GetAllHouses().FirstOrDefault(h => h.TemplateId == templateId.Value);
        if (lodestone == null)
            return null;

        return DeclareForFaction(zoneId, (uint)factionId, lodestone, declarer);
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
            command.CommandText = "UPDATE dominions SET guard_tower_step = @step WHERE zone_id = @zoneId";
            command.Parameters.AddWithValue("@step", nextStep);
            command.Parameters.AddWithValue("@zoneId", zoneId);
            command.Prepare();
            command.ExecuteNonQuery();
        }

        if (_dominions.TryGetValue(zoneId, out var dominion) && stepRow.BuffId != 0)
        {
            var house = HousingManager.Instance.GetHouseById(dominion.House);
            house?.Buffs.AddBuff(stepRow.BuffId, house);
        }

        Logger.Info("Dominion zone {0} guard tower advanced to step {1} ({2} gates, {3} walls)", zoneId, nextStep, stepRow.NumGates, stepRow.NumWalls);
        return nextStep;
    }

    /// <summary>
    /// Places a real House for one of the 4 territory-construction blueprint items (Guardian Altar 47335, Farm
    /// 47330, Workshop 47333, Warehouse 47334, Overseer Post 47311, etc.) that DO have a real design, found
    /// 2026-08-21 by re-checking the actual placement pipeline instead of the item_spawn_doodads table an
    /// earlier pass wrongly assumed was the only relevant one:
    /// - `item_housings` (item_id -> design_id) IS real data for these items (e.g. 47335 -> design 756,
    ///   47330 -> 744, 47333 -> 747, 47334 -> 748, 47311 -> 749) - loaded into HousingGameData's
    ///   `_housingItemHousings` already, just never consulted anywhere live (HousingManager's own
    ///   GetDesignByItemId equivalent was dead code, literally wrapped in `/* Unused */`). Added a live
    ///   accessor, HousingGameData.GetDesignByItemId, for this method to use.
    /// - `housing_build_steps` (keyed by that same design/housing_id) has real per-step `model_id` rows for
    ///   each of these designs - a real, playable construction progression already exists in the data and in
    ///   `House.Template.BuildSteps`/`House.CurrentStep`, exactly the same system every other player house
    ///   already uses. `HousingManager.CreateDominionHouse` (already used by DeclareDominion.cs for the
    ///   lodestone itself) sets `CurrentStep = 0` automatically when `Template.BuildSteps.Count > 0`, so this
    ///   gets the same real construction-in-progress model as any other house, no new client-facing wire work.
    /// - `dominion_housings` groups these 5 into exactly one row each (alter/production/processing/logistics/
    ///   military) - confirms "one of each per territory" is the intended shape, hence the dedup guard below.
    /// NOT wired here, and don't conflate with this: `guard_tower_steps.NumGates`/`NumWalls` (the numbers
    /// AdvanceGuardTowerStep logs above) are a SEPARATE numeric progression on the claimed lodestone House
    /// itself (already handled by the BuffId application above) - those columns have no model/template id
    /// anywhere and their large values (up to 90) don't fit "spawn N discrete objects"; they read as a
    /// construction-material/action threshold akin to `housing_build_steps.num_actions`, not a spawn count.
    /// Building N literal extra Wall/Gate/Tower doodads to match those numbers would be inventing a mechanic
    /// the data doesn't describe - not done. The Wall (47313)/Gate (47314)/Tower (47306/47383) items DO also
    /// have real `item_housings` designs (121/135/119 respectively) and so ARE placeable via this same method
    /// if a player uses them - they just aren't auto-spawned by step count, and (unlike the 5 above) aren't
    /// dedup-limited to one each, since a territory plausibly wants several.
    /// Guard NPCs: searched `npcs`/`localized_texts` for anything named after "감독"(overseer)/"경비"(guard)
    /// scoped to Dominion/guard-tower content - found nothing real to wire. Not guessed at.
    /// </summary>
    /// <returns>The newly created House, or null if the item has no real design, the territory isn't claimed,
    /// or (for the 5 dedup-limited designs) one is already built here.</returns>
    public House TryBuildDominionStructure(ushort zoneId, uint itemTemplateId, Character declarer)
    {
        if (!_dominions.ContainsKey(zoneId))
            return null;

        var designId = HousingGameData.Instance.GetDesignByItemId(itemTemplateId);
        if (designId == 0)
            return null;

        if (!_builtStructuresByZone.TryGetValue(zoneId, out var built))
            _builtStructuresByZone[zoneId] = built = [];

        var isUniqueDesign = SiegeGameData.Instance.IsUniqueDominionHousingDesign(designId);
        if (isUniqueDesign && built.Contains(designId))
        {
            // No dedicated "one of these per territory already built" error code exists in the shipped
            // ErrorMessageType enum - reusing the same generic Invalid code AdvanceGuardTowerStep.cs already
            // uses for its own rejection paths, rather than inventing a new enum value the client can't map.
            declarer.SendErrorMessage(ErrorMessageType.Invalid);
            return null;
        }

        var pos = declarer.Transform.World.Position;
        var house = HousingManager.Instance.CreateDominionHouse(designId, declarer, declarer.ParentWorld, pos.X, pos.Y, pos.Z);
        if (house == null)
            return null;

        built.Add(designId);
        Logger.Info("Dominion zone {0}: {1} built structure design {2} (item {3}) as house {4}",
            zoneId, declarer.Name, designId, itemTemplateId, house.Id);
        return house;
    }

    public int GetCastleTier(ushort zoneId) => _castleTierByZone.GetValueOrDefault(zoneId, 0);

    public int AdvanceCastleTier(ushort zoneId, int targetTier)
    {
        if (!_dominions.ContainsKey(zoneId))
            return GetCastleTier(zoneId);

        _castleTierByZone[zoneId] = targetTier;

        using (var connection = MySQL.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE dominions SET castle_tier = @tier WHERE zone_id = @zoneId";
            command.Parameters.AddWithValue("@tier", targetTier);
            command.Parameters.AddWithValue("@zoneId", zoneId);
            command.Prepare();
            command.ExecuteNonQuery();
        }

        Logger.Info("Dominion zone {0} castle tier advanced to {1}", zoneId, targetTier);
        return targetTier;
    }

    public void UpdateSiegePeriod(ushort zoneId, byte period)
    {
        if (!_dominions.TryGetValue(zoneId, out var dominion))
            return;

        dominion.SiegeTimers.SiegePeriod = period;

        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE dominions SET siege_period = @period WHERE zone_id = @zoneId";
        command.Parameters.AddWithValue("@period", period);
        command.Parameters.AddWithValue("@zoneId", zoneId);
        command.Prepare();
        command.ExecuteNonQuery();
    }

    public void SendAllDominionsTo(GameConnection connection)
    {
        var character = connection?.ActiveChar;
        if (character == null)
            return;

        foreach (var dominion in _dominions.Values)
            character.SendPacket(new SCDominionDataPacket(dominion, false, true));
    }

    private void Insert(DominionData dominion, uint guardTowerSettingId)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            REPLACE INTO dominions
                (zone_id, expedition_id, faction_id, house, guard_tower_setting_id, tax_rate, x, y, z,
                 cur_house_tax_money, cur_hunt_tax_money, peace_tax_money, cur_house_tax_aa_point, peace_tax_aa_point,
                 last_paid_time, last_siege_end_time, reign_start_time, last_tax_rate_changed_time,
                 last_national_tax_rate_changed_time, national_tax_rate, national_monument_db_id,
                 national_monument_x, national_monument_y, national_monument_z, siege_period,
                 non_pvp_start, non_pvp_duration)
            VALUES
                (@zoneId, @expeditionId, @factionId, @house, @guardTowerSettingId, @taxRate, @x, @y, @z,
                 @curHouseTaxMoney, @curHuntTaxMoney, @peaceTaxMoney, @curHouseTaxAaPoint, @peaceTaxAaPoint,
                 @lastPaidTime, @lastSiegeEndTime, @reignStartTime, @lastTaxRateChangedTime,
                 @lastNationalTaxRateChangedTime, @nationalTaxRate, @nationalMonumentDbId,
                 @nationalMonumentX, @nationalMonumentY, @nationalMonumentZ, @siegePeriod,
                 @nonPvPStart, @nonPvPDuration)
            """;
        command.Parameters.AddWithValue("@zoneId", dominion.ZoneId);
        command.Parameters.AddWithValue("@expeditionId", dominion.ExpeditionId);
        command.Parameters.AddWithValue("@factionId", dominion.OwningFactionId);
        command.Parameters.AddWithValue("@house", dominion.House);
        command.Parameters.AddWithValue("@guardTowerSettingId", guardTowerSettingId);
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
        command.Parameters.AddWithValue("@lastNationalTaxRateChangedTime", dominion.LastNationalTaxRateChagedTime);
        command.Parameters.AddWithValue("@nationalTaxRate", dominion.NationalTaxRate);
        command.Parameters.AddWithValue("@nationalMonumentDbId", dominion.NationalMonumentDbId);
        command.Parameters.AddWithValue("@nationalMonumentX", dominion.NationalMonumentX);
        command.Parameters.AddWithValue("@nationalMonumentY", dominion.NationalMonumentY);
        command.Parameters.AddWithValue("@nationalMonumentZ", dominion.NationalMonumentZ);
        command.Parameters.AddWithValue("@siegePeriod", dominion.SiegeTimers.SiegePeriod);
        command.Parameters.AddWithValue("@nonPvPStart", dominion.NonPvPStart);
        command.Parameters.AddWithValue("@nonPvPDuration", dominion.NonPvPDuration);
        command.Prepare();
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Resolves the top-level Nuia/Harihara alliance (FactionsEnum.NuiaAlliance/HaranyaAlliance) a declaring
    /// character belongs to, for DominionData.FactionId - see that field's doc comment for why this matters
    /// (the native X2Dominion:GetOwnerFaction gate). Characters carry their own race-based faction
    /// (Character.Faction, e.g. Nuian/Elf/Harani/Firran); FactionManager already resolves that faction's
    /// MotherId up to the alliance root (same pattern already used by ChatManager's nation channels and
    /// CSResurrectCharacterPacket's war-return-point lookup - Character.Faction.MotherId).
    /// </summary>
    /// <summary>Public so DeclareDominion.cs can resolve a Hero's own alliance faction for the new faction-claim path without duplicating this logic.</summary>
    public static FactionsEnum ResolveOwningFaction(Character declarer)
    {
        var raceFaction = declarer?.Faction;
        if (raceFaction == null)
            return FactionsEnum.Invalid;
        return raceFaction.MotherId != FactionsEnum.Invalid ? raceFaction.MotherId : raceFaction.Id;
    }

    /// <summary>
    /// Same resolution as the Character overload above, but for boot-time reload (Load()) where no live
    /// Character is available - falls back to the claiming guild's owner (or, failing that, any member)
    /// via ExpeditionManager's already-loaded roster, using ExpeditionMember.FactionId (each member's own
    /// race-based faction, refreshed on login - see ExpeditionMember.Refresh).
    /// </summary>
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

    /// <summary>internal so GuildDominionManager (old castle system) can build the same TerritoryData shape without duplicating this logic - pure data transform, not manager state.</summary>
    internal static DominionTerritoryData BuildTerritoryData(uint guardTowerSettingId)
    {
        var settings = SiegeGameData.Instance.GetGuardTowerSettings(guardTowerSettingId);
        if (settings == null)
        {
            Logger.Warn("No guard_tower_settings row for id {0}; using zeroed TerritoryData", guardTowerSettingId);
            return new DominionTerritoryData();
        }

        return new DominionTerritoryData
        {
            Id = settings.Id,
            Id2 = settings.InitialBuffId,
            MaxGates = settings.MaxGates,
            MaxWalls = settings.MaxWalls,
            RadiusDeclare = settings.RadiusDeclare,
            RadiusDominion = settings.RadiusDominion,
            RadiusOffenseHq = settings.RadiusOffenseHq,
            RadiusSiege = settings.RadiusSiege
        };
    }

    /// <summary>internal so GuildDominionManager can reuse - pure data transform, not manager state.</summary>
    internal static DominionUnkData EmptyUnkData() => new()
    {
        Id = 0,
        ObjId = 0,
        X = 0,
        Y = 0,
        Z = 0,
        Ni = 0,
        Nr = 0,
        Limit = 0,
        UnkIds = []
    };
}
