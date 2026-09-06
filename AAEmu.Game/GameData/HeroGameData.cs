using System.Linq;

using AAEmu.Commons.Utils;
using AAEmu.Game.GameData.Framework;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;

using NLog;

namespace AAEmu.Game.GameData;

/// <summary>hero_conditions — election eligibility thresholds and mail templates. Shared across all factions (no faction_id column).</summary>
public class HeroCondition
{
    public uint Id { get; init; }
    public int LeadershipRankingScope { get; init; }
    public int HeroCandidateScope { get; init; }
    public int VotableLeadershipPoint { get; init; }
    public int VotableLevel { get; init; }
    public int HeroCandidateMinPoint { get; init; }
    public int HeroCandidateMinLevel { get; init; }
    public string CandidateMailBody { get; init; }
    public string ElectionMailBody { get; init; }
    public string HeroBonusMailBody { get; init; }
    public string HeroNewPeriodTitle { get; init; }
}

/// <summary>hero_rewards — one row per (top_faction_id, ranking) placement; item_set_id is the mail-attached reward (see item_sets/item_set_items).</summary>
public class HeroReward
{
    public uint Id { get; init; }
    public int Ranking { get; init; }
    public uint TopFactionId { get; init; }
    public uint HeroGradeId { get; init; }
    public uint ItemSetId { get; init; }
    public int DominionPointWeeklyCount { get; init; }
    public bool DominionTax { get; init; }
    public int DefaultRezDistrictBindCount { get; init; }
    public int InferiorRezDistrictBindCount { get; init; }
}

/// <summary>
/// hero_bonuses — the daily-activity reward box for a currently-serving Hero (leadership + Mobilization
/// Order charges + an item box), keyed by <see cref="Id"/> only (no faction/grade column). Row 1/2/3
/// comments read "1등급"/"2등급"/"3등급 영웅 보상" (grade-1/2/3 hero reward) - mapped to real
/// hero_grade_id 4/3/2 respectively (id=1 = the best reward = the single top-ranked "이프니르" grade,
/// matching the same descending id-1↔4 pattern hero_rewards itself already uses for ranks 1 vs 4-6) -
/// this mapping is INFERRED from the comment text + hero_rewards' own precedent, not decompile-confirmed.
/// No row exists for hero_grade_id 1 (에페리움) - that grade is never actually elected to (hero_rewards
/// itself only ever populates grade_id 2/3/4), so this is a complete set for every real serving Hero.
/// </summary>
public class HeroBonus
{
    public uint Id { get; init; }
    public int LeadershipPoint { get; init; }
    public int MobilizationOrderCount { get; init; }
    public uint ItemId { get; init; }
    public byte ItemGradeId { get; init; }
    public int ItemCount { get; init; }
}

/// <summary>
/// hero_bonus_today_assignments — links one hero_bonus row to one today_quest_steps row (a Hero-board,
/// sort_id=4, daily quest) plus a completion-count threshold. Confirmed via the client's own
/// hero_mission.lua (per-row "todayQuests" entries with curValue/targetCount) that each of the 3
/// Hero-board steps tracks its own independent completion count, not a combined total - Count here is
/// that step's own target for the Hero's current grade.
/// </summary>
public class HeroBonusTodayAssignment
{
    public uint Id { get; init; }
    public uint HeroBonusId { get; init; }
    public uint TodayQuestStepId { get; init; }
    public int Count { get; init; }
}

/// <summary>One heros row plus its 4 hero_schedules phase windows — a single global monthly election cycle, shared by every faction.</summary>
public class HeroCycle
{
    public uint Id { get; init; }
    public uint HeroConditionId { get; init; }
    public Dictionary<HeroPhase, (DateTime Start, DateTime End)> Phases { get; } = [];

    public HeroPhase GetPhase(DateTime atUtc)
    {
        foreach (var (phase, window) in Phases)
        {
            if (atUtc >= window.Start && atUtc < window.End)
                return phase;
        }

        return HeroPhase.None;
    }
}

/// <summary>The heros/hero_schedules/hero_conditions/hero_grades/hero_rewards template tables — see the individual row types.</summary>
[GameData]
public class HeroGameData : Singleton<HeroGameData>, IGameDataLoader
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private List<HeroCycle> _cycles = [];
    private Dictionary<uint, HeroCondition> _conditions = [];
    private Dictionary<uint, string> _grades = [];
    private List<HeroReward> _rewards = [];
    private Dictionary<uint, HeroBonus> _bonuses = [];
    private List<HeroBonusTodayAssignment> _bonusAssignments = [];

    /// <summary>hero_bonuses.id ↔ hero_grade_id - see <see cref="HeroBonus"/>'s doc comment for why this
    /// specific mapping was chosen.</summary>
    private static readonly Dictionary<uint, uint> BonusIdByGradeId = new() { [4] = 1, [3] = 2, [2] = 3 };

    public void Load(SqliteConnection connection)
    {
        _cycles = [];
        _conditions = [];
        _grades = [];
        _rewards = [];
        _bonuses = [];
        _bonusAssignments = [];

        var cyclesById = new Dictionary<uint, HeroCycle>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM heros";
            command.Prepare();
            using var sqliteReader = command.ExecuteReader();
            using var reader = new SQLiteWrapperReader(sqliteReader);
            while (reader.Read())
            {
                var cycle = new HeroCycle
                {
                    Id = reader.GetUInt32("id"),
                    HeroConditionId = reader.GetUInt32("hero_condition_id", 0)
                };
                cyclesById[cycle.Id] = cycle;
                _cycles.Add(cycle);
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM hero_schedules";
            command.Prepare();
            using var sqliteReader = command.ExecuteReader();
            using var reader = new SQLiteWrapperReader(sqliteReader);
            while (reader.Read())
            {
                var heroId = reader.GetUInt32("hero_id");
                if (!cyclesById.TryGetValue(heroId, out var cycle))
                    continue;

                var eventId = reader.GetInt32("event_id", 0);
                if (eventId is < 1 or > 4)
                    continue;

                cycle.Phases[(HeroPhase)eventId] = (reader.GetDateTime("start"), reader.GetDateTime("end"));
            }
        }

        Logger.Info("Loaded {0} hero cycles", _cycles.Count);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM hero_conditions";
            command.Prepare();
            using var sqliteReader = command.ExecuteReader();
            using var reader = new SQLiteWrapperReader(sqliteReader);
            while (reader.Read())
            {
                var condition = new HeroCondition
                {
                    Id = reader.GetUInt32("id"),
                    LeadershipRankingScope = reader.GetInt32("leadership_ranking_scope", 0),
                    HeroCandidateScope = reader.GetInt32("hero_candidate_scope", 0),
                    VotableLeadershipPoint = reader.GetInt32("votable_leadership_point", 0),
                    VotableLevel = reader.GetInt32("votable_level", 0),
                    HeroCandidateMinPoint = reader.GetInt32("hero_candidate_min_point", 0),
                    HeroCandidateMinLevel = reader.GetInt32("hero_candidate_min_level", 0),
                    CandidateMailBody = reader.GetString("candidate_mail_body", string.Empty),
                    ElectionMailBody = reader.GetString("election_mail_body", string.Empty),
                    HeroBonusMailBody = reader.GetString("hero_bonus_mail_body", string.Empty),
                    HeroNewPeriodTitle = reader.GetString("hero_new_period_title", string.Empty)
                };

                _conditions[condition.Id] = condition;
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM hero_grades";
            command.Prepare();
            using var sqliteReader = command.ExecuteReader();
            using var reader = new SQLiteWrapperReader(sqliteReader);
            while (reader.Read())
                _grades[reader.GetUInt32("id")] = reader.GetString("name", string.Empty);
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM hero_rewards";
            command.Prepare();
            using var sqliteReader = command.ExecuteReader();
            using var reader = new SQLiteWrapperReader(sqliteReader);
            while (reader.Read())
            {
                _rewards.Add(new HeroReward
                {
                    Id = reader.GetUInt32("id"),
                    Ranking = reader.GetInt32("ranking", 0),
                    TopFactionId = reader.GetUInt32("top_faction_id", 0),
                    HeroGradeId = reader.GetUInt32("hero_grade_id", 0),
                    ItemSetId = reader.GetUInt32("item_set_id", 0),
                    DominionPointWeeklyCount = reader.GetInt32("dominion_point_weekly_count", 0),
                    DominionTax = reader.GetBoolean("dominion_tax"),
                    DefaultRezDistrictBindCount = reader.GetInt32("default_rez_district_bind_count", 0),
                    InferiorRezDistrictBindCount = reader.GetInt32("inferior_rez_district_bind_count", 0)
                });
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM hero_bonuses";
            command.Prepare();
            using var sqliteReader = command.ExecuteReader();
            using var reader = new SQLiteWrapperReader(sqliteReader);
            while (reader.Read())
            {
                var bonus = new HeroBonus
                {
                    Id = reader.GetUInt32("id"),
                    LeadershipPoint = reader.GetInt32("leadership_point", 0),
                    MobilizationOrderCount = reader.GetInt32("mobilization_order_count", 0),
                    ItemId = reader.GetUInt32("item_id", 0),
                    ItemGradeId = (byte)reader.GetInt32("item_grade_id", 0),
                    ItemCount = reader.GetInt32("item_count", 0)
                };
                _bonuses[bonus.Id] = bonus;
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM hero_bonus_today_assignments";
            command.Prepare();
            using var sqliteReader = command.ExecuteReader();
            using var reader = new SQLiteWrapperReader(sqliteReader);
            while (reader.Read())
            {
                _bonusAssignments.Add(new HeroBonusTodayAssignment
                {
                    Id = reader.GetUInt32("id"),
                    HeroBonusId = reader.GetUInt32("hero_bonus_id"),
                    TodayQuestStepId = reader.GetUInt32("today_quest_step_id"),
                    Count = reader.GetInt32("count", 1)
                });
            }
        }

        Logger.Info("Loaded {0} hero conditions, {1} hero grades, {2} hero rewards, {3} hero bonuses",
            _conditions.Count, _grades.Count, _rewards.Count, _bonuses.Count);
    }

    public void PostLoad()
    {
    }

    /// <summary>The cycle whose union of phase windows contains <paramref name="atUtc"/>, or null if none (gap between cycles).</summary>
    public HeroCycle GetCurrentCycle(DateTime atUtc)
    {
        foreach (var cycle in _cycles)
        {
            foreach (var window in cycle.Phases.Values)
            {
                if (atUtc >= window.Start && atUtc < window.End)
                    return cycle;
            }
        }

        return null;
    }

    /// <summary>
    /// The cycle nearest to <paramref name="atUtc"/> when none is currently active (a real schedule gap) -
    /// whichever cycle's overall window (earliest phase start to latest phase end) is closest, preferring an
    /// upcoming cycle over a past one at equal distance. Used by /herophase (GM phase-override testing aid) so
    /// forcing a phase during a gap still has a real HeroCycle (id, hero_condition_id) to operate against,
    /// instead of every DB-backed step (candidate computation, vote recording, ...) having nothing to key on.
    /// </summary>
    public HeroCycle GetNearestCycle(DateTime atUtc)
    {
        HeroCycle best = null;
        var bestDistance = TimeSpan.MaxValue;
        foreach (var cycle in _cycles)
        {
            if (cycle.Phases.Count == 0)
                continue;

            var start = cycle.Phases.Values.Min(w => w.Start);
            var end = cycle.Phases.Values.Max(w => w.End);
            var distance = atUtc < start ? start - atUtc : atUtc >= end ? atUtc - end : TimeSpan.Zero;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = cycle;
            }
        }

        return best;
    }

    public HeroCondition GetCondition(uint id) => _conditions.GetValueOrDefault(id);

    public string GetGradeName(uint id) => _grades.GetValueOrDefault(id, string.Empty);

    /// <summary>hero_rewards row for a faction's final ranking placement in a concluded cycle, or null if that faction had no reward at that rank.</summary>
    public HeroReward GetReward(uint topFactionId, int ranking) =>
        _rewards.Find(r => r.TopFactionId == topFactionId && r.Ranking == ranking);

    /// <summary>Every top_faction_id that has hero_rewards data - i.e. every faction that runs an election, including the 166 player-nation template.</summary>
    public IEnumerable<uint> FactionsWithRewards => _rewards.Select(r => r.TopFactionId).Distinct();

    /// <summary>
    /// Clones the 166 "국가 독립 기본 세력 설정" (Nation Independence Default Force Configuration) template
    /// rows onto a newly-founded nation's own real faction id, in memory only - no SQLite write (compact.sqlite3
    /// is shared static reference data, not something to mutate live). HeroManager has zero hardcoded faction
    /// checks and just loops FactionsWithRewards, so once this runs the new faction id is immediately eligible
    /// for the normal Hero election cycle with no further changes needed. No-ops (returns false) if
    /// <paramref name="newFactionId"/> already has reward rows (idempotent - a re-run after a restart, once
    /// NationManager.Load() re-populates the founded-nation list, calls this again for every existing nation).
    /// </summary>
    public bool CloneRewardsForNewFaction(uint templateFactionId, uint newFactionId)
    {
        if (_rewards.Any(r => r.TopFactionId == newFactionId))
            return false;

        var templateRows = _rewards.Where(r => r.TopFactionId == templateFactionId).ToList();
        foreach (var template in templateRows)
        {
            _rewards.Add(new HeroReward
            {
                Id = template.Id, // template Id is only ever used as a DB PK on load - never re-persisted, safe to duplicate across in-memory clones
                Ranking = template.Ranking,
                TopFactionId = newFactionId,
                HeroGradeId = template.HeroGradeId,
                ItemSetId = template.ItemSetId,
                DominionPointWeeklyCount = template.DominionPointWeeklyCount,
                DominionTax = template.DominionTax,
                DefaultRezDistrictBindCount = template.DefaultRezDistrictBindCount,
                InferiorRezDistrictBindCount = template.InferiorRezDistrictBindCount
            });
        }

        return templateRows.Count > 0;
    }

    /// <summary>The hero_bonus_today_assignment row (target completion count) for a given hero_grade_id's
    /// bonus tier and a specific Hero-board today_quest_steps.id, or null if that grade/step combination
    /// has no assignment (e.g. grade 1/에페리움, which is never actually elected to).</summary>
    public HeroBonusTodayAssignment GetBonusAssignment(uint heroGradeId, uint todayQuestStepId)
    {
        if (!BonusIdByGradeId.TryGetValue(heroGradeId, out var bonusId))
            return null;

        return _bonusAssignments.Find(a => a.HeroBonusId == bonusId && a.TodayQuestStepId == todayQuestStepId);
    }

    public HeroBonus GetBonus(uint id) => _bonuses.GetValueOrDefault(id);

    /// <summary>How many hero seats a nation elects - the count of distinct hero_rewards rankings for that
    /// faction (retail: 6 for Nuia/Haranya, 3 for the Pirates). Used to cap how many candidates a single
    /// ballot may select.</summary>
    public int SeatsFor(uint topFactionId) => _rewards.Count(r => r.TopFactionId == topFactionId);
}
