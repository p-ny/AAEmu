using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;

using NLog;

namespace AAEmu.Game.GameData;

/// <summary>
/// One row of <c>guard_tower_settings</c> — the real source for a claimed Dominion's territory radii/gate-and-wall
/// caps, keyed by <see cref="Models.Game.Housing.HousingTemplate.GuardTowerSettingId"/> on the claimed lodestone's
/// housing template. Previously hardcoded per-claim in <c>DeclareDominion</c>.
/// </summary>
public class GuardTowerSettings
{
    public uint Id { get; init; }
    public uint InitialBuffId { get; init; }
    public byte MaxGates { get; init; }
    public byte MaxWalls { get; init; }
    public short RadiusDeclare { get; init; }
    public ushort RadiusDominion { get; init; }
    public short RadiusOffenseHq { get; init; }
    public short RadiusSiege { get; init; }
}

/// <summary>
/// One row of <c>siege_zones</c> — the recurring weekly schedule template for a zone group's siege cycle. All
/// "_weekday" fields are read as a **day offset added to the applicable <see cref="SiegePlan.WeekStart"/>**, not
/// an absolute day-of-week code — inferred from the data (week_start is always a Tuesday, and every phase's
/// weekday field is uniformly 3 across all 4 shipped rows, which only makes sense as "N days after the week's
/// anchor"; there was no dev-DLL string spelling this out explicitly, so treat this as the best-supported reading
/// rather than a confirmed fact — worth re-checking against a live client capture).
/// </summary>
public class SiegeZoneSchedule
{
    public uint ZoneGroupId { get; init; }
    public int ReinforceDefenseDelayMins { get; init; }
    public uint DefenseMerchantId { get; init; }
    public uint OffenseMerchantId { get; init; }
    public uint DominionMerchantId { get; init; }
    public uint MonumentDoodadId { get; init; }

    public int StartHeroVolunteerWeekdayOffset { get; init; }
    public int StartHeroVolunteerHour { get; init; }
    public int StartHeroVolunteerMin { get; init; }

    public int StartReadyToSiegeWeekdayOffset { get; init; }
    public int StartReadyToSiegeHour { get; init; }
    public int StartReadyToSiegeMin { get; init; }

    public int StartDeclareDominionWeekdayOffset { get; init; }
    public int StartDeclareDominionHour { get; init; }
    public int StartDeclareDominionMin { get; init; }
    public TimeSpan DeclareDominionDuration { get; init; }

    public int StartSiegeWeekdayOffset { get; init; }
    public int StartSiegeHour { get; init; }
    public int StartSiegeMin { get; init; }
    public TimeSpan SiegeDuration { get; init; }

    private static DateTime At(DateTime weekStart, int dayOffset, int hour, int min) =>
        weekStart.AddDays(dayOffset).Date.AddHours(hour).AddMinutes(min);

    public DateTime HeroVolunteerStart(DateTime weekStart) =>
        At(weekStart, StartHeroVolunteerWeekdayOffset, StartHeroVolunteerHour, StartHeroVolunteerMin);

    public DateTime ReadyToSiegeStart(DateTime weekStart) =>
        At(weekStart, StartReadyToSiegeWeekdayOffset, StartReadyToSiegeHour, StartReadyToSiegeMin);

    public DateTime DeclareDominionStart(DateTime weekStart) =>
        At(weekStart, StartDeclareDominionWeekdayOffset, StartDeclareDominionHour, StartDeclareDominionMin);

    public DateTime DeclareDominionEnd(DateTime weekStart) => DeclareDominionStart(weekStart) + DeclareDominionDuration;

    public DateTime SiegeStart(DateTime weekStart) =>
        At(weekStart, StartSiegeWeekdayOffset, StartSiegeHour, StartSiegeMin);

    public DateTime SiegeEnd(DateTime weekStart) => SiegeStart(weekStart) + SiegeDuration;
}

/// <summary>One concrete weekly siege-cycle instance from <c>siege_plans</c>: "zone group X has a cycle starting at week_start Y".</summary>
public readonly record struct SiegePlan(uint ZoneGroupId, DateTime WeekStart);

/// <summary>
/// One row of <c>guard_tower_steps</c> — the wall/gate construction progression for a claimed Dominion's Guard
/// Tower, keyed by (guard_tower_setting_id, step). Distinct from the generic per-house
/// <c>housing_build_steps</c>/<c>HousingBuildStep</c> system (different columns: gates/walls/buff, not a model)
/// - Guard Towers have zero <c>housing_build_steps</c> rows, so <see cref="Models.Game.Housing.House.CurrentStep"/>
/// starts at -1 ("fully built") for them; this is a separate progression layered on top. See
/// DominionManager.AdvanceGuardTowerStep - wired since 2026-08-19 to the 5 guard-tower blueprint items' shared
/// use skill (see that method's own doc comment for the corrected status and remaining gaps).
/// </summary>
public class GuardTowerStep
{
    public uint GuardTowerSettingId { get; init; }
    public int Step { get; init; }
    public byte NumGates { get; init; }
    public byte NumWalls { get; init; }
    public uint BuffId { get; init; }
}

/// <summary>The <c>guard_tower_settings</c>/<c>siege_zones</c>/<c>siege_plans</c> template tables — see the individual row types.</summary>
[GameData]
public class SiegeGameData : Singleton<SiegeGameData>, IGameDataLoader
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private Dictionary<uint, GuardTowerSettings> _guardTowerSettings = [];
    private Dictionary<uint, SiegeZoneSchedule> _siegeZoneSchedules = [];
    private Dictionary<uint, List<DateTime>> _siegePlanWeekStartsByZoneGroup = [];
    private Dictionary<uint, List<GuardTowerStep>> _guardTowerStepsBySettingId = [];
    private readonly HashSet<uint> _uniqueDominionHousingDesigns = [];

    public void Load(SqliteConnection connection)
    {
        _guardTowerSettings = [];
        _siegeZoneSchedules = [];
        _siegePlanWeekStartsByZoneGroup = [];
        _guardTowerStepsBySettingId = [];
        _uniqueDominionHousingDesigns.Clear();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM guard_tower_settings";
            command.Prepare();
            using var sqliteReader = command.ExecuteReader();
            using var reader = new SQLiteWrapperReader(sqliteReader);
            while (reader.Read())
            {
                var settings = new GuardTowerSettings
                {
                    Id = reader.GetUInt32("id"),
                    InitialBuffId = reader.GetUInt32("initial_buff_id", 0),
                    MaxGates = (byte)reader.GetInt32("max_gates", 0),
                    MaxWalls = (byte)reader.GetInt32("max_walls", 0),
                    RadiusDeclare = (short)reader.GetInt32("radius_declare", 0),
                    RadiusDominion = (ushort)reader.GetInt32("radius_dominion", 0),
                    RadiusOffenseHq = (short)reader.GetInt32("radius_offense_hq", 0),
                    RadiusSiege = (short)reader.GetInt32("radius_siege", 0)
                };

                _guardTowerSettings[settings.Id] = settings;
            }
        }

        Logger.Info("Loaded {0} guard tower settings", _guardTowerSettings.Count);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM guard_tower_steps ORDER BY guard_tower_setting_id, step";
            command.Prepare();
            using var sqliteReader = command.ExecuteReader();
            using var reader = new SQLiteWrapperReader(sqliteReader);
            while (reader.Read())
            {
                var step = new GuardTowerStep
                {
                    GuardTowerSettingId = reader.GetUInt32("guard_tower_setting_id"),
                    Step = reader.GetInt32("step", 0),
                    NumGates = (byte)reader.GetInt32("num_gates", 0),
                    NumWalls = (byte)reader.GetInt32("num_walls", 0),
                    BuffId = reader.GetUInt32("buff_id", 0)
                };

                if (!_guardTowerStepsBySettingId.TryGetValue(step.GuardTowerSettingId, out var list))
                    _guardTowerStepsBySettingId[step.GuardTowerSettingId] = list = [];
                list.Add(step);
            }
        }

        Logger.Info("Loaded guard tower step progressions for {0} settings", _guardTowerStepsBySettingId.Count);

        using (var command = connection.CreateCommand())
        {
            // dominion_housings groups the 5 unique per-territory buildings (alter/production/processing/
            // logistics/military - see enum_dominion_housing_groups) by housing_id (== item_housings.design_id
            // == housings.template_id, same numbering space). Used to tell DominionManager.
            // TryBuildDominionStructure which designs are "one of these per territory" vs freely repeatable
            // (e.g. Wall/Gate/Tower designs, which have no row here at all).
            command.CommandText = "SELECT DISTINCT housing_id FROM dominion_housings";
            command.Prepare();
            using var sqliteReader = command.ExecuteReader();
            using var reader = new SQLiteWrapperReader(sqliteReader);
            while (reader.Read())
                _uniqueDominionHousingDesigns.Add(reader.GetUInt32("housing_id"));
        }

        Logger.Info("Loaded {0} unique-per-territory dominion housing designs", _uniqueDominionHousingDesigns.Count);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM siege_zones";
            command.Prepare();
            using var sqliteReader = command.ExecuteReader();
            using var reader = new SQLiteWrapperReader(sqliteReader);
            while (reader.Read())
            {
                var schedule = new SiegeZoneSchedule
                {
                    ZoneGroupId = reader.GetUInt32("zone_group_id"),
                    ReinforceDefenseDelayMins = reader.GetInt32("reinforce_defense_delay_mins", 0),
                    DefenseMerchantId = reader.GetUInt32("defense_merchant_id", 0),
                    OffenseMerchantId = reader.GetUInt32("offense_merchant_id", 0),
                    DominionMerchantId = reader.GetUInt32("dominion_merchant_id", 0),
                    MonumentDoodadId = reader.GetUInt32("monument_doodad_id", 0),

                    StartHeroVolunteerWeekdayOffset = reader.GetInt32("start_hero_volunteer_weekday", 0),
                    StartHeroVolunteerHour = reader.GetInt32("start_hero_volunteer_hour", 0),
                    StartHeroVolunteerMin = reader.GetInt32("start_hero_volunteer_min", 0),

                    StartReadyToSiegeWeekdayOffset = reader.GetInt32("start_ready_to_siege_weekday", 0),
                    StartReadyToSiegeHour = reader.GetInt32("start_ready_to_siege_hour", 0),
                    StartReadyToSiegeMin = reader.GetInt32("start_ready_to_siege_min", 0),

                    StartDeclareDominionWeekdayOffset = reader.GetInt32("start_declare_dominion_weekday", 0),
                    StartDeclareDominionHour = reader.GetInt32("start_declare_dominion_hour", 0),
                    StartDeclareDominionMin = reader.GetInt32("start_declare_dominion_min", 0),
                    DeclareDominionDuration = new TimeSpan(
                        reader.GetInt32("declare_dominion_days", 0),
                        reader.GetInt32("declare_dominion_hours", 0),
                        reader.GetInt32("declare_dominion_mins", 0), 0),

                    StartSiegeWeekdayOffset = reader.GetInt32("start_siege_weekday", 0),
                    StartSiegeHour = reader.GetInt32("start_siege_hour", 0),
                    StartSiegeMin = reader.GetInt32("start_siege_min", 0),
                    SiegeDuration = new TimeSpan(
                        reader.GetInt32("siege_days", 0),
                        reader.GetInt32("siege_hours", 0),
                        reader.GetInt32("siege_mins", 0), 0)
                };

                _siegeZoneSchedules[schedule.ZoneGroupId] = schedule;
            }
        }

        Logger.Info("Loaded {0} siege zone schedules", _siegeZoneSchedules.Count);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT zone_group_id, week_start FROM siege_plans ORDER BY week_start";
            command.Prepare();
            using var sqliteReader = command.ExecuteReader();
            using var reader = new SQLiteWrapperReader(sqliteReader);
            while (reader.Read())
            {
                var zoneGroupId = reader.GetUInt32("zone_group_id");
                var weekStart = reader.GetDateTime("week_start");
                if (!_siegePlanWeekStartsByZoneGroup.TryGetValue(zoneGroupId, out var list))
                    _siegePlanWeekStartsByZoneGroup[zoneGroupId] = list = [];
                list.Add(weekStart);
            }
        }

        var planCount = _siegePlanWeekStartsByZoneGroup.Values.Sum(l => l.Count);
        Logger.Info("Loaded {0} siege plan cycles across {1} zone groups", planCount, _siegePlanWeekStartsByZoneGroup.Count);
    }

    public void PostLoad()
    {
    }

    public GuardTowerSettings GetGuardTowerSettings(uint id) => _guardTowerSettings.GetValueOrDefault(id);

    /// <summary>Ordered step list (1, 2, 3, ...) for a guard_tower_setting_id, or empty if none defined.</summary>
    public IReadOnlyList<GuardTowerStep> GetGuardTowerSteps(uint guardTowerSettingId) =>
        (IReadOnlyList<GuardTowerStep>)_guardTowerStepsBySettingId.GetValueOrDefault(guardTowerSettingId) ?? [];

    /// <summary>Highest defined step number for a guard_tower_setting_id, or 0 if none.</summary>
    public int GetMaxGuardTowerStep(uint guardTowerSettingId) =>
        GetGuardTowerSteps(guardTowerSettingId).Count == 0 ? 0 : GetGuardTowerSteps(guardTowerSettingId)[^1].Step;

    /// <summary>True if this housing design (== item_housings.design_id == housings.template_id) is one of the
    /// 5 unique per-territory dominion buildings from dominion_housings, i.e. only one should ever be built per
    /// claimed zone group. False for anything else (Wall/Gate/Tower designs included) - those may be built
    /// repeatedly.</summary>
    public bool IsUniqueDominionHousingDesign(uint designId) => _uniqueDominionHousingDesigns.Contains(designId);

    public SiegeZoneSchedule GetSiegeZoneSchedule(uint zoneGroupId) => _siegeZoneSchedules.GetValueOrDefault(zoneGroupId);

    public IEnumerable<uint> ScheduledZoneGroupIds => _siegeZoneSchedules.Keys;

    /// <summary>
    /// zone_group_id -> `housings` template id for that zone group's single claimable Guard Tower, in this
    /// specific 10.0.2.13 CN build. Derived (not guessed) by cross-referencing `housings` rows that have a
    /// non-zero `guard_tower_setting_id` (184/187/189/192, exactly 4 of them) against `guard_tower_settings`'
    /// Korean location comments (e.g. id 2 = "살피마리 중앙 수호탑") matching `zone_groups.display_text`
    /// (id 33 = "살피마리"). No direct FK exists in the schema for this, hence a static table here rather than
    /// a runtime lookup - verified once, not re-derived on every load.
    /// guard_tower_settings has 12 rows total (up to 3 candidate tower spots per region, only one of which - not
    /// always the "center" one - got wired to a real `housings` row in this build) across 6 named regions
    /// (살피마리/누이마리/서녘마리/안식의 땅/심연의 입구/태양의 들녘) - the last 2 have no `housings` row at all in
    /// this build, i.e. are not currently claimable, unlike (per the user) older ArcheAge versions.
    /// </summary>
    private static readonly Dictionary<ushort, uint> LodestoneHousingTemplateByZoneGroup = new()
    {
        [33] = 184, // o_salpimari
        [34] = 187, // o_nuimari
        [43] = 189, // o_seonyeokmari
        [44] = 192, // o_rest_land
        // Re-enabled 2026-08-13: templates 271/272 and their live-DB House instances (housings id 11/12,
        // real coordinates) were already present in this build's seed data, but their guard_tower_setting_id
        // link was 0 in compact.sqlite3 - a real content regression versus the 1.2 reference DB (which has
        // 271->11, 272->12) - patched back across all compact.sqlite3 copies. Unlike zone groups 33/34/43/44,
        // these two have no siege_zones/siege_plans schedule rows at all (never shipped for this build), so
        // IsDeclareDominionWindowOpen always needs the /siegewindow GM override here - there is no natural
        // declare window to wait for. SiegeManager.Tick's GetScheduledPeriod also always returns Peace for
        // these zone groups (no schedule to drive HeroVolunteer/ReadyToSiege/Siege phases).
        [54] = 271, // e_exeloch (심연의 입구)
        [56] = 272  // e_sungold_fields (태양의 들녘)
    };

    public uint? GetLodestoneHousingTemplateId(ushort zoneGroupId) =>
        LodestoneHousingTemplateByZoneGroup.TryGetValue(zoneGroupId, out var templateId) ? templateId : null;

    /// <summary>The latest siege_plans week_start for <paramref name="zoneGroupId"/> that is not after <paramref name="atUtc"/>, or null if none.</summary>
    public DateTime? GetCurrentCycleWeekStart(uint zoneGroupId, DateTime atUtc)
    {
        if (!_siegePlanWeekStartsByZoneGroup.TryGetValue(zoneGroupId, out var weekStarts))
            return null;

        DateTime? best = null;
        foreach (var weekStart in weekStarts)
        {
            if (weekStart > atUtc)
                continue;
            if (best == null || weekStart > best)
                best = weekStart;
        }

        return best;
    }
}
