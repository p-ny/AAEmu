using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.GameData;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Teleport;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Tasks.Heroes;

using MySql.Data.MySqlClient;

using NLog;

namespace AAEmu.Game.Core.Managers;

/// <summary>
/// Ticks the 4-phase hero election cycle (HeroPhase) shared by every faction off heros/hero_schedules in
/// HeroGameData, computes candidates from Character.LeadershipPoint at HeroAbstain, tallies CSHeroVotingPacket
/// votes during HeroVoting, and pays out hero_rewards by mail at HeroPeriod. Candidate/vote list SC response
/// packets (SCHeroCandidateListPacket/SCHeroAllScorePacket) don't exist yet in r575 and aren't added here - no
/// confirmed array layout was found, see D:\aa\siege-castle-hero-nation-brief.md.
/// </summary>
public class HeroManager(ITaskManager taskManager) : Singleton<HeroManager>, IHeroManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private const string SystemSenderName = ".hero";

    /// <summary>Synthetic cycle id for ToggleTestHero - well above any real heros.id from hero_schedules data, so it can never collide with a real election cycle.</summary>
    private const uint TestCycleId = 999999999;

    /// <summary>
    /// The (season, phase) this manager last broadcast to online characters - lets Tick() detect a natural
    /// phase transition (e.g. HeroAbstain -> HeroVoting) and announce it properly (see
    /// BuildEventStateEntries), instead of only silently resyncing at login.
    /// </summary>
    private (uint Season, HeroPhase Phase) _lastBroadcast = (0, HeroPhase.None);

    /// <summary>
    /// GM-forced phase (/herophase), overriding hero_schedules until cleared with null. Testing aid ported
    /// 2026-08-14 from a rejected community PR's design (github.com/AAEmu/AAEmu/pull/1516,
    /// `HeroElectionManager`/`HeroPhaseCmd` there - rejected for code shape, not for this idea), adapted onto
    /// this codebase's own HeroManager/HeroGameData instead of introducing a second, competing manager: the
    /// PR's own `hero_election_votes`/`HeroSeason` tables would have duplicated our already-working
    /// hero_candidates/hero_votes/HeroCycle data model. The shipped hero_schedules windows are weeks to months
    /// apart (see the class-level doc comment), so without a forced override there is no way to reach most
    /// phases without waiting or hand-editing dates in both the server's and the client's own compact.sqlite3 -
    /// this replaces that whole date-editing dance with a single in-memory flag.
    /// </summary>
    private HeroPhase? _phaseOverride;

    /// <summary>
    /// Per-faction Mobilization Order flag state (see MobilizationTimeStateType's own doc comment) -
    /// in-memory only, matching this window's own short (30-minute) real-world duration: a World restart
    /// mid-window simply loses the remaining boost early rather than corrupting any persisted data.
    /// </summary>
    private readonly Dictionary<uint, (MobilizationTimeStateType State, DateTime Until)> _mobilizationTimeState = new();

    /// <summary>Called by DeclareMobilizationTimeState when a Hero declares one of the 3 flag states.</summary>
    public void SetMobilizationTimeState(uint factionId, MobilizationTimeStateType state, TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        _mobilizationTimeState[factionId] = (state, until);
        Logger.Info("HeroManager.SetMobilizationTimeState: faction {0} declared {1}, active until {2}", factionId, state, until);
    }

    /// <summary>The faction's currently-active Mobilization Order flag state, or None if expired/never set.</summary>
    public MobilizationTimeStateType GetActiveMobilizationTimeState(uint factionId)
    {
        if (!_mobilizationTimeState.TryGetValue(factionId, out var entry))
            return MobilizationTimeStateType.None;

        return entry.Until > DateTime.UtcNow ? entry.State : MobilizationTimeStateType.None;
    }

    /// <summary>
    /// One faction's currently in-flight Mobilization Order (from issue to the popup's own ~60s window
    /// closing) - lets AcceptMobilizationOrder tell a real Accept apart from a stale/replayed one, and stops
    /// the same character teleporting twice off one order. That exact bug (repeatable accept -> repeated
    /// teleport) is one of the concrete issues a rejected community PR's mobilization implementation was
    /// flagged for (github.com/AAEmu/AAEmu/pull/1516) - guarded against here from the start rather than
    /// found later. In-memory only, same reasoning as _mobilizationTimeState: short-lived, a restart simply
    /// losing it early is an acceptable failure mode.
    /// </summary>
    private sealed class MobilizationOrderState
    {
        public ulong HeroId;
        public DateTime ExpiresAt;
        public HashSet<uint> AcceptedCharacterIds { get; } = [];
    }

    private readonly Dictionary<uint, MobilizationOrderState> _activeMobilizationOrders = new();

    /// <summary>
    /// The 3 capital statues (see DoodadFuncFactionStatueDevote/StatueContribute) doubling as each faction's
    /// real-world Mobilization Order assembly point. Not a guess: PR #1516's own assembly-point coordinates
    /// (from a different, incompatible client build - its zone ids resolve to unrelated zones in this
    /// server's data) land within ~10-30 units of these statues' real doodad_spawns.json positions for all
    /// 3 factions, independently matching the "Time of Leap/Battle/Plunder" flag interaction's own tooltip
    /// text ("...through the King Andrion II Statue at the rally point").
    /// </summary>
    private static readonly (uint FactionId, uint StatueTemplateId)[] MobilizationAssemblyStatues =
    [
        (148, 10588), // Nuia - King Andrion II
        (149, 10650), // Haranya - Amarendra IV
        (114, 10651), // Pirate - Morpheus
    ];

    /// <summary>
    /// Per-faction zones.group_id for the doodad the F-key "issue mobilization order" interaction actually
    /// gates on (a different, real Time-of-Leap/Plunder statue per faction - doodad_almighty_id 9163/9432/
    /// 9433, NOT the same templates as <see cref="MobilizationAssemblyStatues"/> above). Ghidra+Frida
    /// live-confirmed 2026-09-06: the client's native gate (x2game.dll FUN_39108670) requires its cached
    /// zoneGroupId field to exactly equal the interacted doodad's own zone group - our
    /// SCHeroMobilizationOrderUpdatedPacket was hardcoding 0 at every call site, which can never match, so
    /// the dialog was unreachable and the client always fell back to its generic "used all your orders"
    /// message regardless of the real count. Each faction's value here was confirmed by locating that
    /// faction's real doodad_spawns.json position (main_world) and reading the live server's own resolved
    /// zones.group_id at that exact position, not guessed: Nuia @ w_marianople_2 (zone_key 183), Haranya @
    /// e_sunrise_peninsula_2 (zone_key 191), Pirate @ s_pirate_island (zone_key 284).
    /// </summary>
    private static readonly (uint FactionId, uint ZoneGroupId)[] MobilizationOrderZoneGroups =
    [
        (148, 2),  // Nuia - w_marianople_2
        (149, 4),  // Haranya - e_sunrise_peninsula_2
        (114, 60), // Pirate - s_pirate_island
    ];

    /// <summary>
    /// Resolves the zones.group_id our SCHeroMobilizationOrderUpdatedPacket needs to send for this
    /// character's own nation, so the client's native "issue mobilization order" gate can actually match it -
    /// see <see cref="MobilizationOrderZoneGroups"/>. Returns 0 (never matches anything real) if the
    /// character's nation isn't one of the 3 known factions.
    /// </summary>
    public uint ResolveMobilizationOrderZoneGroupId(Character character)
    {
        var nationFactionId = ResolveNationFactionId(character);
        var entry = Array.Find(MobilizationOrderZoneGroups, z => z.FactionId == nationFactionId);
        return entry.ZoneGroupId;
    }

    /// <summary>
    /// A faction member accepts a Hero's Mobilization Order (CSFactionMobilizationOrderPacket,
    /// Result=Accept) - teleports them to their faction's capital statue/rally point. This is the
    /// previously-missing mechanical effect of accepting an order (the issue/broadcast/counter plumbing was
    /// already real and working - see IssueMobilizationOrder). Rejects a stale order (past its window), an
    /// order from a Hero other than the one currently active (a newer order superseded it), and a repeat
    /// accept from the same character on the same order - the abuse PR #1516 was rejected for allowing.
    /// </summary>
    public bool AcceptMobilizationOrder(Character character, ulong heroId)
    {
        if (character?.Faction == null)
            return false;

        var nationFactionId = ResolveNationFactionId(character);
        if (!_activeMobilizationOrders.TryGetValue(nationFactionId, out var order))
            return false;

        if (order.HeroId != heroId || order.ExpiresAt < DateTime.UtcNow)
            return false;

        if (!order.AcceptedCharacterIds.Add(character.Id))
            return false;

        var statue = Array.Find(MobilizationAssemblyStatues, s => s.FactionId == nationFactionId);
        var doodad = statue.StatueTemplateId != 0
            ? character.ParentWorld?.GetDoodadsByTemplateId(statue.StatueTemplateId).FirstOrDefault()
            : null;
        if (doodad == null)
        {
            Logger.Warn("HeroManager.AcceptMobilizationOrder: no live assembly-point statue found for faction {0}", nationFactionId);
            return false;
        }

        var pos = doodad.Transform.World.Position;
        character.ForceDismount();
        character.DisabledSetPosition = true;
        character.SendPacket(new SCTeleportUnitPacket(TeleportReason.Etc, 0, pos.X, pos.Y, pos.Z + 2f, 0f));
        Logger.Info("HeroManager.AcceptMobilizationOrder: teleported {0} to faction {1}'s assembly point", character.Name, nationFactionId);
        return true;
    }

    public bool IsPhaseOverridden => _phaseOverride.HasValue;

    public void Load()
    {
        taskManager.Schedule(new HeroTickTask(), TimeSpan.FromSeconds(20), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// The real schedule's cycle+phase for "now", with the GM override substituted in when set. Falls back to
    /// HeroGameData.GetNearestCycle when overriding during a genuine schedule gap (GetCurrentCycle would
    /// otherwise return null) so every DB-backed step below still has a real HeroCycle (id, hero_condition_id)
    /// to operate against - without an override, a gap still correctly yields (null, HeroPhase.None) exactly
    /// as before this was added.
    /// </summary>
    private (HeroCycle Cycle, HeroPhase Phase) GetEffective(DateTime now)
    {
        var cycle = HeroGameData.Instance.GetCurrentCycle(now);
        if (cycle == null && _phaseOverride.HasValue)
            cycle = HeroGameData.Instance.GetNearestCycle(now);

        var phase = _phaseOverride ?? (cycle?.GetPhase(now) ?? HeroPhase.None);
        return (cycle, phase);
    }

    public void Tick()
    {
        var (cycle, phase) = GetEffective(DateTime.UtcNow);
        var seasonId = cycle?.Id ?? 0;

        if (phase != _lastBroadcast.Phase || seasonId != _lastBroadcast.Season)
        {
            var leaving = _lastBroadcast;
            _lastBroadcast = (seasonId, phase);
            BroadcastPhaseChange(leaving);
        }

        RunPhaseEntryWork(cycle, phase);
    }

    /// <summary>The per-phase one-time work (leadership reset / candidate freeze / election count) - shared by
    /// Tick()'s natural transitions and SetOverride() so a GM-forced phase change doesn't have to wait up to a
    /// minute for the next tick to actually do anything.</summary>
    private void RunPhaseEntryWork(HeroCycle cycle, HeroPhase phase)
    {
        if (cycle == null)
            return;

        switch (phase)
        {
            case HeroPhase.LeadershipRanking:
                EnsureLeadershipPeriodReset(cycle);
                break;
            case HeroPhase.HeroAbstain:
                EnsureCandidatesComputed(cycle);
                break;
            case HeroPhase.HeroPeriod:
                EnsureElectionFinalized(cycle);
                break;
        }
    }

    /// <summary>Forces the hero season's phase (/herophase), or clears the force with null so hero_schedules
    /// takes over again. Broadcasts the change to everyone online immediately, same as a natural transition.</summary>
    public void SetOverride(HeroPhase? phase)
    {
        _phaseOverride = phase;
        var (cycle, effective) = GetEffective(DateTime.UtcNow);
        var leaving = _lastBroadcast;
        _lastBroadcast = (cycle?.Id ?? 0, effective);
        RunPhaseEntryWork(cycle, effective);
        BroadcastPhaseChange(leaving);
    }

    /// <summary>Human-readable summary for /herophase with no arguments.</summary>
    public string Describe()
    {
        var (cycle, phase) = GetEffective(DateTime.UtcNow);
        var overrideText = _phaseOverride.HasValue ? $" (GM override, /herophase auto to clear)" : "";

        if (cycle == null)
            return $"Phase: {phase}{overrideText}. No hero cycle data at all.";

        var windows = string.Join(", ", cycle.Phases.OrderBy(p => p.Key)
            .Select(p => $"{p.Key}={p.Value.Start:yyyy-MM-dd HH:mm}..{p.Value.End:yyyy-MM-dd HH:mm} UTC"));
        return $"Phase: {phase}{overrideText}. Cycle {cycle.Id} schedule: {windows}";
    }

    /// <summary>
    /// Rolls leadership at the start of a cycle's LeadershipRanking phase: the current period's total
    /// (Character.LeadershipPoint) is snapshotted into the previous-period record
    /// (Character.LeadershipPeriodPoint - what the client's native X2Hero:IsVoter() actually gates the vote
    /// checkbox on) and then reset to 0, starting a fresh ladder. Guarded by hero_period_resets so a restart
    /// mid-phase (this runs every tick while the phase is active) can't re-roll a cycle that already rolled -
    /// same idempotency idiom as EnsureCandidatesComputed/EnsureElectionFinalized below.
    /// </summary>
    /// <remarks>
    /// Rebuilt 2026-08-15 from a plain zero-reset to a real current-to-frozen roll, matching a rejected
    /// community PR's leadership model (github.com/AAEmu/AAEmu/pull/1516's RollPeriod) - see
    /// Character.LeadershipPeriodPoint's doc comment and D:\aa\hero-vote-bug-report.txt for why the earlier
    /// single-field design was wrong.
    /// </remarks>
    private static void EnsureLeadershipPeriodReset(HeroCycle cycle)
    {
        using var connection = MySQL.CreateConnection();
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM hero_period_resets WHERE cycle_id=@c";
            check.Parameters.AddWithValue("@c", cycle.Id);
            check.Prepare();
            if (Convert.ToInt64(check.ExecuteScalar()) > 0)
                return; // already rolled for this cycle
        }

        using (var roll = connection.CreateCommand())
        {
            roll.CommandText = "UPDATE characters SET leadership_period_point=leadership_point, leadership_point=0";
            roll.ExecuteNonQuery();
        }

        using (var mark = connection.CreateCommand())
        {
            mark.CommandText = "INSERT INTO hero_period_resets (cycle_id, reset_at) VALUES (@c,@t)";
            mark.Parameters.AddWithValue("@c", cycle.Id);
            mark.Parameters.AddWithValue("@t", DateTime.UtcNow);
            mark.Prepare();
            mark.ExecuteNonQuery();
        }

        // The UPDATE above only touched the DB - already-online characters hold the authoritative in-memory
        // figures and would overwrite the roll right back on their next autosave otherwise. Apply the same
        // roll live, then push the updated slot-11/12 values (SCCharacterGamePointsPacket/SCHeroSeasonOffPacket,
        // see their doc comments) so an already-connected client's sheet and vote-eligibility gate see it
        // immediately, not just on their next zone-enter.
        foreach (var character in WorldManager.Instance.GetAllCharacters())
        {
            character.LeadershipPeriodPoint = character.LeadershipPoint;
            character.LeadershipPoint = 0;
            character.SendPacket(new SCCharacterGamePointsPacket(character));
            character.SendPacket(new SCHeroSeasonOffPacket(0, character.LeadershipPeriodPoint));
        }

        Logger.Info("Hero cycle {0}: rolled leadership_point -> leadership_period_point for all characters", cycle.Id);
    }

    /// <summary>
    /// A genuine phase transition just happened - announce it (BuildEventStateEntries' leaving/entering
    /// states) to everyone online right now, and refresh their full Hero view (candidates just computed for
    /// HeroAbstain, election just finalized for HeroPeriod, etc.) at the same time. A character who is
    /// offline for the live moment simply resyncs silently on their next login - matching a rejected
    /// community PR's own design (github.com/AAEmu/AAEmu/pull/1516), which does not try to "catch up" a late
    /// arrival with a synthetic announcement either.
    /// </summary>
    private void BroadcastPhaseChange((uint Season, HeroPhase Phase) leaving)
    {
        foreach (var character in WorldManager.Instance.GetAllCharacters())
            PushHeroInfo(character, showUi: false, leaving);
    }

    /// <summary>
    /// Pushes the Hero panel's data to a character: current phase (SCHeroEventStatePacket), candidate list
    /// with leadership standings (SCHeroCandidateListPacket), and the finalized ranked roster
    /// (SCHeroRankingListPacket) if the election for this cycle has been finalized. Call at login, matching
    /// the existing ExpeditionManager.SendExpeditionInfo pattern - this opcode family didn't exist anywhere in
    /// this codebase until 2026-08-14 (see aaemu-siege-castle-hero-nation memory), so the client had no way to
    /// ever learn this even when the server-side election logic below was already running correctly.
    /// </summary>
    /// <param name="showUi">
    /// SCHeroCandidateListPacket's showUi flag - confirmed via a rejected community PR's disassembly
    /// (github.com/AAEmu/AAEmu/pull/1516) to be what makes the native client raise the window-populating
    /// "HERO_ELECTION" Lua event; the PR calls this openWindow and deliberately passes true ONLY from the
    /// actual CSHeroCandidateListPacket handler (the player interacting with the voting machine), false from
    /// every background push (login, zone-enter, phase-change broadcasts, ranking/score-tab requests). We
    /// previously sent true unconditionally from every call site - live-tested 2026-08-14: this repopulates
    /// the ballot's listCtrl (resetting its Lua-tracked CHECK_DATA/checked-row state, a real quirk in this
    /// client's generic listCtrl widget module, confirmed via Ghidra tracing `module.lua`'s InsertData) any
    /// time a background push landed while the voting window happened to already be open with a row ticked -
    /// explaining "the vote checkbox works but nothing ever gets submitted." Defaulting to false here matches
    /// the PR's own pattern and avoids ever needing a client-side fix for this.
    /// </param>
    public void SendHeroInfo(Character character, bool showUi = false) => PushHeroInfo(character, showUi, leaving: null);

    /// <summary>
    /// SendHeroInfo's real body, extended with an optional "phase just left" signal - see
    /// BuildEventStateEntries. <paramref name="leaving"/> is null for every resync path (login, zone-enter,
    /// window-open, on-demand faction request) and only ever set from BroadcastPhaseChange, which is the one
    /// call site backed by a genuine, just-detected phase transition.
    /// </summary>
    private void PushHeroInfo(Character character, bool showUi, (uint Season, HeroPhase Phase)? leaving)
    {
        if (character?.Faction == null)
            return;

        // The client's Hero panel has a faction picker (Pirate/Nuia/Haranya) to view any faction's candidates,
        // not just your own - but this method used to only ever compute+send data for the caller's own faction.
        // Confirmed 2026-08-14: selecting another faction's tab just re-showed the last data actually pushed
        // (always the player's own, Nuia in this case) instead of that faction's real (possibly empty) list -
        // the client has no fallback/empty state for "never received anything about this faction" and re-uses
        // stale data instead. Send every hero_rewards faction's data on each request, matching how
        // BroadcastPhaseChange already treats phase transitions as server-wide-per-faction, not per-viewer.
        using var connection = MySQL.CreateConnection();
        var (currentCycle, basePhase) = GetEffective(DateTime.UtcNow);
        var ownFactionId = ResolveNationFactionId(character);
        var factionIds = HeroGameData.Instance.FactionsWithRewards.ToList();
        var phaseByFaction = factionIds.ToDictionary(f => f, f => ComputeFactionPhase(f, currentCycle, basePhase, connection));

        character.SendPacket(new SCHeroEventStatePacket(false, BuildEventStateEntries(phaseByFaction, leaving)));

        // FUN_39113f30 (Ghidra, 2026-08-16): the native client keeps hero candidates in ONE global (not
        // per-faction) vector that gets fully cleared and repopulated from whatever SCHeroCandidateListPacket
        // it receives most recently, regardless of which faction that packet is about. Sending one packet per
        // faction here (needed for the faction-tab picker) means whichever faction's packet lands LAST wins the
        // native cache - if that wasn't the player's own faction, voting resolves the selected rank to a
        // zero/sentinel character id (confirmed live via Frida: FUN_39113150's candidate scan finds no entry
        // for the requested rank and falls through to the DAT_3a6678b0 zero sentinel). This is the actual root
        // cause of "checkbox works, vote never submits" - not a wire-format bug. Sending the player's own
        // faction last guarantees the native cache is correct by the time they can click Vote.
        foreach (var factionId in factionIds.OrderBy(f => f == ownFactionId ? 1 : 0))
            SendHeroInfoForFaction(character, factionId, phaseByFaction[factionId].Phase, phaseByFaction[factionId].SeasonId, showUi, connection, sendScores: false);

        // "Current Record" (own leadership standing) only makes sense for the character's own faction.
        var ownPhase = basePhase;
        using (var findOwnCandidate = connection.CreateCommand())
        {
            findOwnCandidate.CommandText = "SELECT votes FROM hero_candidates WHERE faction_id=@f AND character_id=@ch AND abstained=0 ORDER BY cycle_id DESC LIMIT 1";
            findOwnCandidate.Parameters.AddWithValue("@f", ownFactionId);
            findOwnCandidate.Parameters.AddWithValue("@ch", character.Id);
            findOwnCandidate.Prepare();
            var ownVotesResult = findOwnCandidate.ExecuteScalar();
            var ownVotes = ownVotesResult != null ? (int)Convert.ToInt64(ownVotesResult) : 0;
            character.SendPacket(new SCHeroSeasonInfoPacket((int)ownPhase, character.LeadershipPoint, ownVotes));
        }

        // Baseline periodLeadershipPoint for this login/zone-enter - covers a character whose
        // LeadershipPeriodPoint was already nonzero from a previous session (Character.ChangeGamePoints only
        // pushes these two packets on a NEW change, not on load). See SCCharacterGamePointsPacket.cs's doc
        // comment for why both packets are needed together.
        character.SendPacket(new SCCharacterGamePointsPacket(character));
        character.SendPacket(new SCHeroSeasonOffPacket(0, character.LeadershipPeriodPoint));

        // Primes the client's X2Hero:DominionPointCount() cache the same way the two sends just above prime
        // leadership - the dialog that reads it (tab_dominion.lua) never sends a dedicated "give me the
        // count" request, it just reads whatever was last pushed. Harmless/idempotent if not currently a
        // hero (GetDominionPointCount returns weeklyMax=0 either way).
        SendDominionPointCount(character);
    }

    /// <summary>
    /// Same as SendHeroInfo, but only sends the candidate/ranking/hero-list data for ONE specific faction -
    /// matching what CSHeroAllScorePacket's request actually carries (a real factionId parameter, confirmed via
    /// Ghidra as the "RequestFactionScores(factionId)" Lua binding, not a generic/ignorable value). Previously
    /// every Hero-menu Candidates-panel faction-tab click got answered with ALL factions' data regardless of
    /// which was clicked, and the client seemingly always fell back to displaying whatever faction actually had
    /// real data (Nuia) no matter which tab was open. The batched event-state (needed for the phase icon to stay
    /// correct across all factions) is still sent for every faction, same as SendHeroInfo - only the per-faction
    /// candidate/ranking/hero/score lists are now scoped to the one actually requested.
    /// </summary>
    public void SendHeroInfoForRequestedFaction(Character character, uint requestedFactionId, bool showUi = false)
    {
        if (character?.Faction == null)
            return;

        using var connection = MySQL.CreateConnection();
        var (currentCycle, basePhase) = GetEffective(DateTime.UtcNow);
        var factionIds = HeroGameData.Instance.FactionsWithRewards.ToList();
        var phaseByFaction = factionIds.ToDictionary(f => f, f => ComputeFactionPhase(f, currentCycle, basePhase, connection));

        character.SendPacket(new SCHeroEventStatePacket(false, BuildEventStateEntries(phaseByFaction, leaving: null)));

        if (phaseByFaction.TryGetValue(requestedFactionId, out var requested))
            SendHeroInfoForFaction(character, requestedFactionId, requested.Phase, requested.SeasonId, showUi, connection, sendScores: true);
    }

    /// <summary>
    /// Real hero_schedules data has gaps between consecutive monthly cycles (confirmed 2026-08-14: cycle 4
    /// ends 2026-08-04, cycle 5 doesn't start until 2026-08-15 - "now" can genuinely fall in the dead zone,
    /// which is a real data characteristic, not a bug). GetCurrentCycle correctly returns null there and phase
    /// becomes None. But if a Hero has actually been elected (real election or /makehero test), the client's UI
    /// reacts to phase=None by showing "No Hero candidates have been selected yet" regardless of whatever
    /// candidate/hero data arrives afterward - confirmed by the user hitting exactly this after ToggleTestHero.
    /// Override to HeroPeriod (term-in-progress) whenever an elected Hero exists for this faction, so the phase
    /// state doesn't contradict the hero data being sent right after it.
    /// </summary>
    private static (HeroPhase Phase, uint SeasonId) ComputeFactionPhase(uint factionId, HeroCycle cycle, HeroPhase basePhase, MySqlConnection connection)
    {
        var phase = basePhase;
        var seasonId = cycle?.Id ?? 0;

        if (phase == HeroPhase.None)
        {
            using var checkElected = connection.CreateCommand();
            checkElected.CommandText = "SELECT cycle_id FROM hero_candidates WHERE faction_id=@f AND elected=1 ORDER BY cycle_id DESC LIMIT 1";
            checkElected.Parameters.AddWithValue("@f", factionId);
            checkElected.Prepare();
            var electedCycle = checkElected.ExecuteScalar();
            if (electedCycle != null)
            {
                phase = HeroPhase.HeroPeriod;
                seasonId = Convert.ToUInt32(electedCycle);
            }
        }

        return (phase, seasonId);
    }

    private void SendHeroInfoForFaction(Character character, uint factionId, HeroPhase phase, uint currentSeasonId, bool showUi, MySqlConnection connection, bool sendScores)
    {
        var candidates = new List<HeroCandidateEntry>();
        var rankings = new List<HeroRankingEntry>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT hc.cycle_id, hc.character_id, hc.leadership_point_at_ranking, hc.votes, c.expedition_id,
                       c.leadership_point, c.accumulated_leadership_point
                FROM hero_candidates hc
                JOIN characters c ON c.id = hc.character_id
                WHERE hc.faction_id=@f AND hc.abstained=0
                ORDER BY hc.votes DESC, hc.leadership_point_at_ranking DESC
                """;
            select.Parameters.AddWithValue("@f", factionId);
            select.Prepare();
            using var reader = select.ExecuteReader();
            var ranking = 0;
            while (reader.Read())
            {
                ranking++;
                var seasonId = (uint)reader.GetInt32(0);
                var characterId = (uint)reader.GetInt32(1);
                var leadershipPoint = reader.GetInt32(2);
                var votes = (int)reader.GetInt64(3);
                var expeditionId = (uint)reader.GetInt32(4);
                var liveLeadershipPoint = reader.GetInt32(5);
                var liveAccumulatedLeadershipPoint = reader.GetInt32(6);

                // Score must never be 0 for a real candidate - confirmed via Ghidra 2026-08-14 (see
                // aaemu-siege-castle-hero-nation memory): the voting-machine window's native GetCandidateList()
                // rebuilds a dense rank-slot array and uses "score != 0" as an implicit "is this slot actually
                // populated" flag, not a display value check. A real candidate with 0 votes so far used to get
                // treated as an empty slot and rendered as the "abstainer_player" placeholder. That guard is
                // still needed, but Score/AccumPoint double as the Hero panel's own "Leadership" number pair.
                // 2026-09-05 first pass used hc.leadership_point_at_ranking (a ONE-TIME snapshot frozen at
                // whatever moment this candidate row was computed) for BOTH fields - fixed the "shows vote
                // count" bug but produced an identical, frozen "X/X" that never grows (confirmed live:
                // "6000/6000" for Pny, not moving month to month as the user expected). Now uses the LIVE
                // characters.leadership_point (current period, resets each cycle) for Score and LIVE
                // characters.accumulated_leadership_point (true lifetime total, never resets) for AccumPoint -
                // matches Character.cs's own documented distinction between the two concepts, and both grow
                // in real time instead of being stuck at an election/ranking-time snapshot.
                candidates.Add(new HeroCandidateEntry(seasonId, characterId, factionId, expeditionId, ranking, Math.Max(liveLeadershipPoint, 1), liveAccumulatedLeadershipPoint, votes, 0));
                rankings.Add(new HeroRankingEntry(characterId, leadershipPoint, votes, expeditionId));
            }
        }

        Logger.Info("SendHeroInfo({0}): faction={1} phase={2} candidates={3}{4}", character.Name, factionId, phase,
            candidates.Count,
            candidates.Count > 0
                ? $" first=[season={candidates[0].SeasonId},char={candidates[0].CharacterId},topFac={candidates[0].TopFactionId},exp={candidates[0].ExpeditionId},rank={candidates[0].Ranking},score={candidates[0].Score},accum={candidates[0].AccumPoint},votes={candidates[0].VoteCount},rep={candidates[0].Reputation}]"
                : "");

        // "Already voted" - confirmed via a rejected community PR's disassembly (github.com/AAEmu/AAEmu/pull/1516)
        // that X2Hero:IsAlreadyVoted() reads a byte that ONLY SCHeroVotingPacket ever stores natively - not
        // anything in SCHeroCandidateListPacket's own fields. This packet class already existed in this
        // codebase but nothing had ever constructed it ("TODO: nothing constructs this packet yet") - so the
        // checkbox/vote button never learned a vote had actually landed, matching the reported "can vote
        // repeatedly" symptom exactly. Only meaningful for the character's own faction (a voter only ever
        // votes within it). The PR's own ordering rule: send this BEFORE the candidate list, and suppress the
        // candidate list's showUi when already voted - both applied here.
        var isOwnFaction = character.Faction != null && factionId == ResolveNationFactionId(character);
        if (isOwnFaction && currentSeasonId != 0 && HasVoted(connection, currentSeasonId, character.Id))
        {
            character.SendPacket(new SCHeroVotingPacket((int)currentSeasonId, 1));
            showUi = false;
        }

        character.SendPacket(new SCHeroCandidateListPacket(showUi, (int)factionId, (int)currentSeasonId, candidates));

        // Only on a genuine CSHeroAllScorePacket request (the Mission/Score tab actually being opened) - NOT
        // on every general Hero panel refresh. Confirmed 2026-08-15 (see D:\aa\hero-vote-bug-report.txt): the
        // client's own hero_mission.lua:179 throws "bad argument #1 to 'sort' (table expected, got nil)" every
        // time this packet's HERO_ALL_SCORE_UPDATED event fires - a genuine, pre-existing client-side script
        // bug (X2Hero:GetFactionScores(factionID) returns nil regardless of what real, byte-correct data this
        // packet carries - independently Ghidra-reconfirmed twice). Sending it unconditionally on every
        // candidate-list/voting-window refresh meant that Lua error fired mid-vote-confirmation too - the
        // likely actual cause of the long-standing empty-ballot (count=0) mystery, not a wire-format bug.
        // Scoping this the same way showUi is already scoped removes that error from the vote-submission path
        // entirely, whether or not the underlying hero_mission.lua bug itself ever gets fixed.
        if (sendScores)
        {
            var scores = candidates.Select(c => new HeroScoreEntry((ulong)c.CharacterId, c.Score, c.Score, 0)).ToList();
            Logger.Info("SendHeroInfo({0}): dispatching SCHeroAllScorePacket faction={1} count={2}{3}", character.Name, factionId,
                scores.Count,
                scores.Count > 0
                    ? $" first=[char={scores[0].CharacterId},score={scores[0].Score},periodScore={scores[0].PeriodScore},mob={scores[0].MobilizationCount}]"
                    : "");
            character.SendPacket(new SCHeroAllScorePacket((int)factionId, scores));
        }
        // Always send, even with an empty rankings list - the client's Candidates-panel faction picker waits on
        // this exact reply (tabWindow:WaitPage(true) before the request, cleared by the "HERO_RANK_DATA_RETRIEVED"
        // event this reply triggers) per hero_rank.lua. Gating it behind rankings.Count>0 left the panel stuck
        // showing its loading overlay forever for any faction with zero real candidates.
        character.SendPacket(new SCHeroRankingListPacket(factionId, character.LeadershipPoint, 0, rankings));

        // "Who currently holds Hero rank N" - the most recently finalized cycle for this faction (any
        // elected=1 row), showing every ranked slot from that cycle, not just the elected=1 one - the 6-slot
        // Erenor/Ayanad/Delphinad tiers are all "heroes" at their own grade, only rank 1 is "elected".
        var heroes = new List<HeroListEntry>();
        using (var findCycle = connection.CreateCommand())
        {
            findCycle.CommandText = "SELECT cycle_id FROM hero_candidates WHERE faction_id=@f AND elected=1 ORDER BY cycle_id DESC LIMIT 1";
            findCycle.Parameters.AddWithValue("@f", factionId);
            findCycle.Prepare();
            var latestFinalizedCycle = findCycle.ExecuteScalar();
            if (latestFinalizedCycle != null)
            {
                using var selectHeroes = connection.CreateCommand();
                selectHeroes.CommandText = """
                    SELECT hc.character_id, hc.leadership_point_at_ranking, hc.votes, hc.elected, c.expedition_id,
                           c.leadership_point, c.accumulated_leadership_point
                    FROM hero_candidates hc
                    JOIN characters c ON c.id = hc.character_id
                    WHERE hc.cycle_id=@c AND hc.faction_id=@f AND hc.abstained=0
                    ORDER BY hc.votes DESC, hc.leadership_point_at_ranking DESC
                    """;
                selectHeroes.Parameters.AddWithValue("@c", latestFinalizedCycle);
                selectHeroes.Parameters.AddWithValue("@f", factionId);
                selectHeroes.Prepare();
                using var reader = selectHeroes.ExecuteReader();
                var ranking = 0;
                var seasonId = Convert.ToUInt32(latestFinalizedCycle);
                while (reader.Read())
                {
                    ranking++;
                    var characterId = (uint)reader.GetInt32(0);
                    var leadershipPoint = reader.GetInt32(1);
                    var votes = (int)reader.GetInt64(2);
                    var elected = reader.GetBoolean(3);
                    var expeditionId = (uint)reader.GetInt32(4);
                    var liveLeadershipPoint = reader.GetInt32(5);
                    var liveAccumulatedLeadershipPoint = reader.GetInt32(6);
                    var heroGrade = (byte)(HeroGameData.Instance.GetReward(factionId, ranking)?.HeroGradeId ?? 0);
                    // Score/AccumPoint here are the same "Leadership" display fields as HeroCandidateEntry's
                    // (see that constructor's doc comment, fixed again 2026-09-05) - live current/lifetime
                    // leadership instead of a frozen election-time snapshot, so the number actually grows.
                    heroes.Add(new HeroListEntry(seasonId, characterId, factionId, expeditionId, ranking, Math.Max(liveLeadershipPoint, 1), liveAccumulatedLeadershipPoint, heroGrade));
                }
            }
        }

        if (heroes.Count > 0)
        {
            character.SendPacket(new SCHeroListPacket(heroes));
            // The list packet alone does not populate the client's local per-character-id Hero map that
            // IsHero/IsTopLevelHero (and by extension the kind_id=79 unit_reqs gate) actually reads - confirmed
            // via Ghidra 2026-08-14, see aaemu-siege-castle-hero-nation memory. Re-send each entry as an
            // individual update so a fresh login also primes that map, not just a live ToggleTestHero/election
            // event.
            foreach (var hero in heroes)
                character.SendPacket(new SCHeroInfoUpdatedPacket(hero));

            // Proactively prime the client's cached zoneGroupId for the Mobilization Order dialog if this
            // character is themselves a currently-serving Hero for this faction - without this, the value
            // stays at its uninitialized 0 until the very first successful DoodadFuncIssuanceOfMobilizationOrderUiOpen.Use(),
            // but that Use() itself only fires after the client's own local gate check already passes,
            // which requires this same value to already be correct: a real bootstrapping deadlock, found
            // live 2026-09-06 (see MobilizationOrderZoneGroups). Sending it here on login/status-change
            // breaks that deadlock for the character's own nation.
            if (heroes.Any(h => h.CharacterId == character.Id))
            {
                character.SendPacket(new SCHeroMobilizationOrderUpdatedPacket(
                    0, ResolveMobilizationOrderZoneGroupId(character), character.Id,
                    (uint)character.MobilizationOrderTodayCount, (uint)character.MobilizationOrderTotalCount));
            }
        }
    }

    /// <summary>
    /// Whether this character is one of their faction's currently-serving heroes.
    /// </summary>
    /// <remarks>
    /// Fixed 2026-08-15 (see aaemu-siege-castle-hero-nation memory) - the previous version picked a single
    /// `elected=1` row via `ORDER BY cycle_id DESC LIMIT 1` and compared its character_id, which only ever
    /// recognized ONE hero even though a nation elects 3-6 seats per hero_rewards (every rank 1-N gets
    /// elected=1 in EnsureElectionFinalized, not just rank 1). Now checks membership within the latest cycle
    /// that has any elected row for this faction, not a single arbitrary row.
    /// </remarks>
    public bool IsCurrentHero(Character character)
    {
        if (character?.Faction == null)
            return false;

        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM hero_candidates
            WHERE faction_id=@f AND character_id=@ch AND elected=1
              AND cycle_id = (SELECT MAX(cycle_id) FROM hero_candidates WHERE faction_id=@f AND elected=1)
            """;
        command.Parameters.AddWithValue("@f", ResolveNationFactionId(character));
        command.Parameters.AddWithValue("@ch", character.Id);
        command.Prepare();
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// Resolves a character to their top-level nation faction id (Nuia 148/Haranya 149/Pirate 114), the
    /// same way DominionManager.ResolveOwningFaction does. Character.Faction is normally a RACE-level
    /// sub-faction (e.g. system_factions 103 "Dream's Exiles"/Elf, MotherId 148), while every
    /// hero_candidates.faction_id and HeroGameData.FactionsWithRewards entry is always the NATION-level id.
    /// Confirmed live 2026-09-05: comparing hero data against the raw race-sub-faction id never matches a
    /// real character - no real character's own faction_id is ever literally a nation id - which is why a
    /// genuinely-elected Hero's own IsCurrentHero check could still fail, and why the automatic election
    /// pipeline (EnsureCandidatesComputed) needs the matching RaceFactionIdsUnderNation expansion below.
    /// </summary>
    private static uint ResolveNationFactionId(Character character) => (uint)DominionManager.ResolveOwningFaction(character);

    /// <summary>Every race-level system_factions id whose MotherId is <paramref name="nationFactionId"/>, plus the nation id itself (in case a character's raw faction_id was ever literally set to it, e.g. via NationManager) - characters.faction_id is always race-level, so matching it against a nation-level id needs this expansion.</summary>
    private static List<uint> RaceFactionIdsUnderNation(uint nationFactionId)
    {
        var ids = FactionManager.Instance.GetSystemFactions()
            .Where(f => (uint)f.MotherId == nationFactionId)
            .Select(f => (uint)f.Id)
            .ToList();
        ids.Add(nationFactionId);
        return ids;
    }

    /// <summary>War/Choice/Peace flag items (items 46174/46177/46178) - see MobilizationOrderAction.</summary>
    private static readonly (uint ItemId, byte Action)[] MobilizationOrderFlags =
    [
        (46174, 0), // 전쟁을 부르는 깃발 - "Flag that calls War"
        (46177, 1), // 선택을 부르는 깃발 - "Flag that calls Choice"
        (46178, 2), // 평화를 부르는 깃발 - "Flag that calls Peace"
    ];

    /// <summary>
    /// A serving Hero issues a Mobilization Order for their own faction - backs
    /// CSFactionIssuanceOfMobilizationOrderPacket / CSFactionMobilizationOrderPacket
    /// (X2Faction:RequestIssuanceOfMobilizationOrder). These heroes-only flag items are already mailed out
    /// on election finalize (hero_rewards.item_set_id), so a newly-elected Hero can act on this immediately.
    /// </summary>
    /// <remarks>
    /// 2026-08-31: which of the 3 flags the client actually selected is not cleanly recoverable from either
    /// CS packet's RTTI-decoded fields without a live capture (CSFactionIssuanceOfMobilizationOrderPacket
    /// carries only an optional Bc id - most likely the mobilization doodad's own ObjId, matching the
    /// DoodadFuncHeroElection precedent, not an order-type selector; CSFactionMobilizationOrderPacket's
    /// "result"/two unnamed fields have confirmed field COUNT but not confirmed semantic mapping to an
    /// order type). Deliberately NOT guessing a field-to-enum mapping here - instead this derives the order
    /// type from evidence that IS solid: whichever one of the 3 flag items the Hero is actually holding.
    /// Only one flag type should ever be held at a time in practice (a Hero receives one set per term), so
    /// this is unambiguous in the real/expected case; if a Hero somehow holds more than one flag type, the
    /// first match in <see cref="MobilizationOrderFlags"/> wins (war > choice > peace), which is an
    /// arbitrary but harmless tie-break.
    ///
    /// The actual MECHANICAL effect of a war/peace/choice mobilization order (a diplomacy/relation change?
    /// a temporary buff? something else?) could not be pinned down from available evidence in this pass -
    /// not implemented here. This method wires the real, decompile-confirmed plumbing (gating, item
    /// consumption, counters, the two broadcast packets, the ack) - the missing mechanical effect is a
    /// separate, clearly-flagged gap, not silently skipped.
    /// </remarks>
    public bool IssueMobilizationOrder(Character character)
    {
        if (character?.Faction == null)
            return false;

        if (!IsCurrentHero(character))
        {
            character.SendErrorMessage(ErrorMessageType.Invalid);
            return false;
        }

        var flag = MobilizationOrderFlags.FirstOrDefault(f =>
            character.Inventory.CheckItems(SlotType.Inventory, f.ItemId, 1));
        if (flag.ItemId == 0)
        {
            character.SendErrorMessage(ErrorMessageType.NotEnoughItem);
            return false;
        }

        var consumed = character.Inventory.Bag.ConsumeItem(
            ItemTaskType.SkillEffectConsumption, flag.ItemId, 1, null);
        if (consumed != 1)
            return false;

        var now = DateTime.UtcNow;
        if (character.LastMobilizationOrderTime.Date != now.Date)
            character.MobilizationOrderTodayCount = 0;
        character.MobilizationOrderTodayCount++;
        character.MobilizationOrderTotalCount++;
        character.LastMobilizationOrderTime = now;

        var scOrder = new SCFactionMobilizationOrderPacket(0, character.Id, character.Name);
        var scUpdated = new SCHeroMobilizationOrderUpdatedPacket(
            flag.Action, ResolveMobilizationOrderZoneGroupId(character), character.Id,
            (uint)character.MobilizationOrderTodayCount, (uint)character.MobilizationOrderTotalCount);
        var ownNationFactionId = ResolveNationFactionId(character);
        _activeMobilizationOrders[ownNationFactionId] = new MobilizationOrderState
        {
            HeroId = character.Id,
            ExpiresAt = now.AddSeconds(90), // client's own popup window is shorter (~60s, a static client-side constant); this is just a generous server-side backstop against a stale replay
        };
        foreach (var member in WorldManager.Instance.GetAllCharacters()
                     .Where(c => c.Faction != null && ResolveNationFactionId(c) == ownNationFactionId))
        {
            member.SendPacket(scOrder);
            member.SendPacket(scUpdated);
        }

        character.SendPacket(new SCFactionMobilizationOrderSuccessPacket());
        Logger.Info("HeroManager.IssueMobilizationOrder: {0} ({1}) issued action={2} (item {3}), today={4} total={5}",
            character.Name, character.Faction.Id, flag.Action, flag.ItemId, character.MobilizationOrderTodayCount, character.MobilizationOrderTotalCount);
        return true;
    }

    /// <summary>
    /// Whether this character is a standing (non-withdrawn) candidate in the most recently computed cycle for
    /// their faction - backs UnitReqsKindType.NotHeroNotCandidate (kind_id=128), added 2026-08-15 alongside
    /// Hero/NotHero (see aaemu-siege-castle-hero-nation memory).
    /// </summary>
    public bool IsCandidate(Character character)
    {
        if (character?.Faction == null)
            return false;

        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM hero_candidates
            WHERE faction_id=@f AND character_id=@ch AND abstained=0
              AND cycle_id = (SELECT MAX(cycle_id) FROM hero_candidates WHERE faction_id=@f)
            """;
        command.Parameters.AddWithValue("@f", ResolveNationFactionId(character));
        command.Parameters.AddWithValue("@ch", character.Id);
        command.Prepare();
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// This character's own hero_grades tier (1-4), or 0 if they're not a currently-serving hero. Backs the
    /// Hero mission board's grade-based eligibility check (today_quest_steps.sort_id=4, see
    /// TodayQuestStepTemplate.SortId's doc comment) - added 2026-08-15 alongside that fix.
    /// </summary>
    public int GradeOf(Character character)
    {
        if (character?.Faction == null)
            return 0;

        var factionId = ResolveNationFactionId(character);
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT character_id FROM hero_candidates
            WHERE faction_id=@f AND elected=1
              AND cycle_id = (SELECT MAX(cycle_id) FROM hero_candidates WHERE faction_id=@f AND elected=1)
            ORDER BY votes DESC, leadership_point_at_ranking DESC
            """;
        command.Parameters.AddWithValue("@f", factionId);
        command.Prepare();
        using var reader = command.ExecuteReader();
        var ranking = 0;
        while (reader.Read())
        {
            ranking++;
            if ((uint)reader.GetInt32(0) == character.Id)
                return (int)(HeroGameData.Instance.GetReward(factionId, ranking)?.HeroGradeId ?? 0);
        }

        return 0;
    }

    /// <summary>
    /// Called from TodayAssignmentManager.CompleteStep whenever a Hero-board (sort_id=4) daily quest
    /// step completes. Tracks per-character cumulative progress toward that step's
    /// hero_bonus_today_assignments threshold (own persistent table, not a Character column - mirrors
    /// how expedition_buff_purchases is kept separate from Character rather than bloating it further) and,
    /// once reached, grants the tier's hero_bonuses reward (leadership + an item box by mail) and resets
    /// the counter so it can be earned again. Only currently-serving Heroes can ever reach here at all -
    /// TodayAssignmentManager's own eligibility gate already restricts the Hero board to a character whose
    /// <see cref="GradeOf"/> falls in that board's step's level range - but this still no-ops safely if
    /// grade somehow comes back 0 (e.g. a Hero's term just ended between accepting and completing the step).
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT touch hero_bonuses.mobilization_order_count / Character's
    /// MobilizationOrderTodayCount/TotalCount fields - those track Mobilization ORDERS ISSUED (a real item
    /// gets consumed per issuance), a different, already-self-consistent model than this reward field,
    /// and hero_mission.lua's "mobilizationOrderCount"/"maxMobilizationOrderCount" display could plausibly
    /// mean either "issued so far" or "charges currently held" - genuinely ambiguous from available
    /// evidence. Wiring this reward field to either interpretation risked corrupting an already-implemented,
    /// independently-verified counter over a guess - left as a flagged follow-up instead.
    /// </remarks>
    public void OnHeroBoardQuestCompleted(Character character, uint todayQuestStepId)
    {
        var grade = (uint)GradeOf(character);
        if (grade == 0)
            return;

        var assignment = HeroGameData.Instance.GetBonusAssignment(grade, todayQuestStepId);
        if (assignment == null)
            return;

        using var connection = MySQL.CreateConnection();
        int newCount;
        using (var upsert = connection.CreateCommand())
        {
            upsert.CommandText = """
                INSERT INTO character_hero_bonus_progress (character_id, today_quest_step_id, `count`)
                VALUES (@ch, @step, 1)
                ON DUPLICATE KEY UPDATE `count` = `count` + 1
                """;
            upsert.Parameters.AddWithValue("@ch", character.Id);
            upsert.Parameters.AddWithValue("@step", todayQuestStepId);
            upsert.Prepare();
            upsert.ExecuteNonQuery();
        }

        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT `count` FROM character_hero_bonus_progress WHERE character_id=@ch AND today_quest_step_id=@step";
            select.Parameters.AddWithValue("@ch", character.Id);
            select.Parameters.AddWithValue("@step", todayQuestStepId);
            select.Prepare();
            newCount = Convert.ToInt32(select.ExecuteScalar());
        }

        if (newCount < assignment.Count)
            return;

        using (var reset = connection.CreateCommand())
        {
            reset.CommandText = "UPDATE character_hero_bonus_progress SET `count`=0 WHERE character_id=@ch AND today_quest_step_id=@step";
            reset.Parameters.AddWithValue("@ch", character.Id);
            reset.Parameters.AddWithValue("@step", todayQuestStepId);
            reset.Prepare();
            reset.ExecuteNonQuery();
        }

        var bonus = HeroGameData.Instance.GetBonus(assignment.HeroBonusId);
        if (bonus == null)
            return;

        if (bonus.LeadershipPoint > 0)
            character.ChangeGamePoints(GamePointKind.Leadership, bonus.LeadershipPoint);

        if (bonus.ItemId > 0 && bonus.ItemCount > 0)
        {
            var (cycle, _) = GetEffective(DateTime.UtcNow);
            var condition = cycle != null ? HeroGameData.Instance.GetCondition(cycle.HeroConditionId) : null;
            var mail = new BaseMail
            {
                MailType = MailType.HeroElectionItem,
                Title = "Hero Activity Reward",
                ReceiverName = character.Name
            };
            mail.Header.SenderName = SystemSenderName;
            mail.Header.ReceiverId = character.Id;
            mail.Header.Status = MailStatus.Unread;
            mail.Body.Text = condition?.HeroBonusMailBody ?? string.Empty;
            mail.Body.RecvDate = DateTime.UtcNow;

            var item = ItemManager.Instance.Create(bonus.ItemId, bonus.ItemCount, bonus.ItemGradeId, true);
            if (item != null)
                mail.Body.Attachments.Add(item);

            mail.Send();
        }

        Logger.Info("HeroBonus granted: {0} step={1} bonusId={2} leadership={3}", character.Name, todayQuestStepId, bonus.Id, bonus.LeadershipPoint);
    }

    /// <summary>
    /// Weekly Dominion Point allowance (hero_rewards.dominion_point_weekly_count) for this character's
    /// current serving-hero ranking, or 0 if they're not a currently-serving hero. Same ranking derivation
    /// as <see cref="GradeOf"/> - kept as a separate near-duplicate query rather than refactored into a
    /// shared helper, matching this class's existing IsCurrentHero/IsCandidate precedent, so GradeOf's
    /// live behavior is never put at risk by a change made for this feature.
    /// </summary>
    public int DominionPointWeeklyMax(Character character)
    {
        if (character?.Faction == null)
            return 0;

        var factionId = ResolveNationFactionId(character);
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT character_id FROM hero_candidates
            WHERE faction_id=@f AND elected=1
              AND cycle_id = (SELECT MAX(cycle_id) FROM hero_candidates WHERE faction_id=@f AND elected=1)
            ORDER BY votes DESC, leadership_point_at_ranking DESC
            """;
        command.Parameters.AddWithValue("@f", factionId);
        command.Prepare();
        using var reader = command.ExecuteReader();
        var ranking = 0;
        while (reader.Read())
        {
            ranking++;
            if ((uint)reader.GetInt32(0) == character.Id)
                return HeroGameData.Instance.GetReward(factionId, ranking)?.DominionPointWeeklyCount ?? 0;
        }

        return 0;
    }

    /// <summary>
    /// Current daily/weekly Dominion Point usage vs cap, for SCHeroDominionPointCountPacket - matches the
    /// Lua table shape X2Hero:DominionPointCount() returns (tab_dominion.lua,
    /// D:\aa\AA-CN\game\scripts\x2ui\community\nation\tab_dominion.lua: dailyCnt/dailyMax/weeklyCnt/
    /// weeklyMax/timeRemain - "personalPoint"/"memberCount" are NOT part of this packet, see
    /// GiveDominionPoint's scope note). dailyMax is hardcoded to 1 - INFERRED, not decompile-confirmed:
    /// the Lua only ever disables the "distribution" button off a timeRemain countdown when
    /// dailyCnt != 0 ("count['dailyCnt'] == 0 and 0 or count['timeRemain']"), consistent with "at most one
    /// give per day," and hero_rewards has no separate daily-cap column to read a real value from.
    /// </summary>
    public (uint daily, uint dailyMax, uint weekly, uint weeklyMax, uint remainSec) GetDominionPointCount(Character character)
    {
        var weeklyMax = (uint)DominionPointWeeklyMax(character);
        var now = DateTime.UtcNow;
        var everGiven = character.LastDominionPointGiveTime != default;
        var givenToday = everGiven && character.LastDominionPointGiveTime.Date == now.Date;
        var sameWeek = everGiven &&
            System.Globalization.ISOWeek.GetYear(character.LastDominionPointGiveTime) == System.Globalization.ISOWeek.GetYear(now) &&
            System.Globalization.ISOWeek.GetWeekOfYear(character.LastDominionPointGiveTime) == System.Globalization.ISOWeek.GetWeekOfYear(now);
        var weekly = (uint)(sameWeek ? character.DominionPointWeeklyGiven : 0);
        var daily = givenToday ? 1u : 0u;
        var remainSec = givenToday ? (uint)Math.Max(0, (now.Date.AddDays(1) - now).TotalSeconds) : 0u;
        return (daily, 1u, weekly, weeklyMax, remainSec);
    }

    /// <summary>Pushes the current Dominion Point count to this character - see GetDominionPointCount.</summary>
    public void SendDominionPointCount(Character character)
    {
        var (daily, dailyMax, weekly, weeklyMax, remainSec) = GetDominionPointCount(character);
        character.SendPacket(new SCHeroDominionPointCountPacket(daily, dailyMax, weekly, weeklyMax, remainSec));
    }

    public enum DominionPointGiveResult
    {
        Success,
        NotHero,
        NoSuchDominion,
        WrongFaction,
        AlreadyGivenToday,
        WeeklyCapReached
    }

    /// <summary>
    /// Backs CSHeroGiveDominionPointPacket / X2Hero:GiveDominionPoint(zoneGroup) - see the "distribution"
    /// button in tab_dominion.lua (D:\aa\AA-CN\game\scripts\x2ui\community\nation\tab_dominion.lua). The
    /// client only opens this whole dialog when X2Dominion:GetOwnerFaction(zoneGroup) equals the viewer's
    /// own top faction AND X2Hero:IsHero() is true - both mirrored here. The Lua's "zoneGroup" is the same
    /// ushort zoneId CSUpdateDominionTaxRatePacket already resolves for the identical window
    /// (window.zoneGroup is shared between the tax slider and this dialog) - resolved the same
    /// guild-first-then-Hero/faction way that packet already does. In practice guild dominions always have
    /// OwningFactionId=0 (see GuildDominionManager.Declare/ClaimTerritory) and can never pass the
    /// faction-match check below, so this is effectively Hero/faction-territory-only, matching
    /// X2Hero:IsHero()'s own gate (guild castles are led by a guild leader, not an elected Hero).
    ///
    /// SCOPE NOTE: this only implements the counter/grant machinery the master plan asked for (He-P1) -
    /// what a Dominion Point actually FUNDS on the dominion side (the client's own
    /// "service_point_share_total" = personalPoint * memberCount math, which needs a territory member-count
    /// concept Hero/faction dominions don't currently have) is deliberately NOT modeled here - no
    /// persistent effect is written to the dominion itself. Each successful call just consumes one
    /// daily/weekly allowance and notifies the giver. "point"/"type"/"type2" on the resulting
    /// SCHeroGiveDominionPointPacket are best-effort (that packet's own doc comment already flags its
    /// field semantics as unconfirmed beyond width/order) - 1 point, self-notify only, since Hero/faction
    /// territories have no member roster to broadcast to (unlike Expedition.Members for guild residence).
    /// </summary>
    public DominionPointGiveResult GiveDominionPoint(Character character, ushort zoneId)
    {
        if (character?.Faction == null || !IsCurrentHero(character))
            return DominionPointGiveResult.NotHero;

        var dominion = GuildDominionManager.Instance.GetByZoneId(zoneId) ?? DominionManager.Instance.GetByZoneId(zoneId);
        if (dominion == null)
            return DominionPointGiveResult.NoSuchDominion;

        if (dominion.OwningFactionId != ResolveNationFactionId(character))
            return DominionPointGiveResult.WrongFaction;

        var now = DateTime.UtcNow;
        var everGiven = character.LastDominionPointGiveTime != default;
        if (everGiven && character.LastDominionPointGiveTime.Date == now.Date)
            return DominionPointGiveResult.AlreadyGivenToday;

        var sameWeek = everGiven &&
            System.Globalization.ISOWeek.GetYear(character.LastDominionPointGiveTime) == System.Globalization.ISOWeek.GetYear(now) &&
            System.Globalization.ISOWeek.GetWeekOfYear(character.LastDominionPointGiveTime) == System.Globalization.ISOWeek.GetWeekOfYear(now);
        var weeklyGiven = sameWeek ? character.DominionPointWeeklyGiven : 0;
        var weeklyMax = DominionPointWeeklyMax(character);
        if (weeklyMax <= 0 || weeklyGiven >= weeklyMax)
            return DominionPointGiveResult.WeeklyCapReached;

        character.DominionPointWeeklyGiven = weeklyGiven + 1;
        character.LastDominionPointGiveTime = now;

        SendDominionPointCount(character);
        character.SendPacket(new SCHeroGiveDominionPointPacket(0, character.Name, 0, 1, true));

        return DominionPointGiveResult.Success;
    }

    public bool ToggleTestHero(Character character)
    {
        if (character?.Faction == null)
            return false;

        var factionId = ResolveNationFactionId(character);
        using var connection = MySQL.CreateConnection();

        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM hero_candidates WHERE cycle_id=@c AND faction_id=@f AND character_id=@ch AND elected=1";
            check.Parameters.AddWithValue("@c", TestCycleId);
            check.Parameters.AddWithValue("@f", factionId);
            check.Parameters.AddWithValue("@ch", character.Id);
            check.Prepare();
            if (Convert.ToInt64(check.ExecuteScalar()) > 0)
            {
                using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM hero_candidates WHERE cycle_id=@c AND faction_id=@f AND character_id=@ch";
                delete.Parameters.AddWithValue("@c", TestCycleId);
                delete.Parameters.AddWithValue("@f", factionId);
                delete.Parameters.AddWithValue("@ch", character.Id);
                delete.Prepare();
                delete.ExecuteNonQuery();
                // Server-wide: Hero status is public faction-wide knowledge, and this clears the client-side
                // per-character-id map the kind_id=79 unit_reqs gate reads (see SendHeroInfo's doc comment).
                WorldManager.Instance.BroadcastPacketToServer(new SCHeroInfoDeletedPacket((ulong)character.Id));

                WorldManager.Instance.BroadcastPacketToServer(new SCHeroEventStatePacket(false, BuildAllFactionEventState(connection)));

                return false;
            }
        }

        // Only one test-hero per faction at a time, matching real election semantics.
        using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM hero_candidates WHERE cycle_id=@c AND faction_id=@f";
            clear.Parameters.AddWithValue("@c", TestCycleId);
            clear.Parameters.AddWithValue("@f", factionId);
            clear.Prepare();
            clear.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO hero_candidates (cycle_id, faction_id, character_id, leadership_point_at_ranking, votes, abstained, elected) VALUES (@c,@f,@ch,@p,0,0,1)";
            insert.Parameters.AddWithValue("@c", TestCycleId);
            insert.Parameters.AddWithValue("@f", factionId);
            insert.Parameters.AddWithValue("@ch", character.Id);
            insert.Parameters.AddWithValue("@p", character.LeadershipPoint);
            insert.Prepare();
            insert.ExecuteNonQuery();
        }

        // Same map-populating packet SendHeroInfo sends at login - needed live here too, or the client
        // wouldn't see the change until the next relog. rank=1 marks "elected"/top tier (Erenor grade),
        // matching how ToggleTestHero always sets elected=1 for its single synthetic-cycle row.
        var testHeroGrade = (byte)(HeroGameData.Instance.GetReward(factionId, 1)?.HeroGradeId ?? 1);
        WorldManager.Instance.BroadcastPacketToServer(
            new SCHeroInfoUpdatedPacket(new HeroListEntry(TestCycleId, (uint)character.Id, factionId,
                (uint)(character.Expedition?.Id ?? 0), 1, Math.Max(character.LeadershipPoint, 1),
                character.AccumulatedLeadershipPoint, testHeroGrade)));

        // Same HeroPeriod-override reasoning as SendHeroInfo (via ComputeFactionPhase), and same "batch every
        // faction into one packet" fix as above - a single-faction broadcast here clobbers every other online
        // character's view of their own faction's phase, not just this faction's.
        WorldManager.Instance.BroadcastPacketToServer(new SCHeroEventStatePacket(false, BuildAllFactionEventState(connection)));

        return true;
    }

    private List<HeroEventStateEntry> BuildAllFactionEventState(MySqlConnection connection)
    {
        var (cycle, basePhase) = GetEffective(DateTime.UtcNow);
        // Not a real phase transition (just a /makehero status change) - always the silent resync form.
        return BuildEventStateEntries(HeroGameData.Instance.FactionsWithRewards
            .ToDictionary(f => f, f => ComputeFactionPhase(f, cycle, basePhase, connection)), leaving: null);
    }

    /// <summary>
    /// Builds SCHeroEventStatePacket entries for the current set of distinct per-faction phases, following
    /// the client's real 3-state semantics - confirmed via Ghidra AND independently via a rejected community
    /// PR's own disassembly (github.com/AAEmu/AAEmu/pull/1516): 0 = just started (the client announces the
    /// begin-events for that phase - day-alert banners etc.), 1 = running (stored silently, what a resync
    /// wants), 2 = just ended (the client announces the end-events, and for hero_voting specifically clears
    /// the "already voted" flag). See SCHeroEventStatePacket.cs's doc comment for the exact native behavior
    /// each state drives.
    /// </summary>
    /// <remarks>
    /// Rebuilt 2026-08-15 from a per-character "already announced this (season,phase)?" dedup workaround to
    /// this proper leaving/entering model, after the PR's own BuildStates made clear the workaround was
    /// papering over a structural issue: state=0 must ONLY ever come from a genuine, just-detected
    /// transition (leaving != null, i.e. from BroadcastPhaseChange), never from a plain resync (login,
    /// zone-enter, window-open, on-demand faction request) - a resync has nothing to announce and must always
    /// send 1, or the client's banners re-fire on every single call regardless of who has already seen them.
    /// See D:\aa\hero-vote-bug-report.txt.
    ///
    /// The client's "is an election currently active" icon (X2Hero:IsElectionPeriod) reads a fixed 5-slot
    /// array indexed purely by HeroScheduleEvent value (0-4) with NO per-faction dimension - so at most one
    /// entry per DISTINCT phase value is sent, not one per faction (multiple factions sharing a phase would
    /// otherwise race the same array slot, last-write-wins).
    /// </remarks>
    /// <remarks>
    /// Deliberately does NOT also advertise LeadershipRanking/HeroPeriod as "present" when they are not the
    /// current phase, unlike the source PR's BuildStates - that PR's single flat Season counter isn't the
    /// same shape as this codebase's per-cycle HeroCycle.Id keying, and sending an entry whose season/phase
    /// pair resolves to an ALREADY-PAST window (e.g. advertising the current cycle's LeadershipRanking
    /// phase as "present" while HeroVoting is actually running) is unverified territory - the native side's
    /// behavior for a stale-but-resolvable window was never Ghidra-confirmed here, only "doesn't resolve at
    /// all" was. Caused a real client crash on every Hero window open when tried 2026-08-15; reverted rather
    /// than guessed at further. Only entries for the genuinely current phase (plus a real leaving/entering
    /// transition) are sent - narrower than the PR, but everything sent is behavior this codebase's own
    /// Ghidra passes actually verified.
    /// </remarks>
    private static List<HeroEventStateEntry> BuildEventStateEntries(
        Dictionary<uint, (HeroPhase Phase, uint SeasonId)> phaseByFaction, (uint Season, HeroPhase Phase)? leaving)
    {
        var distinct = phaseByFaction.Values
            .Where(v => v.Phase != HeroPhase.None)
            .GroupBy(v => v.Phase)
            .Select(g => g.First())
            .ToDictionary(v => v.Phase, v => v.SeasonId);

        var entries = new List<HeroEventStateEntry>();
        var enteringState = (byte)(leaving.HasValue ? 0 : 1);
        foreach (var (phase, seasonId) in distinct)
            entries.Add(new HeroEventStateEntry(phase, seasonId, enteringState));

        if (leaving is { Phase: not HeroPhase.None } l && !distinct.ContainsKey(l.Phase))
            entries.Add(new HeroEventStateEntry(l.Phase, l.Season, 2));

        return entries;
    }

    /// <summary>
    /// Records a ballot - one or more candidate picks, cast together as a single vote.
    /// </summary>
    /// <remarks>
    /// Takes the WHOLE selection in one call, not one id at a time - a real ballot is multi-select (every
    /// nation elects multiple seats per hero_rewards), and the whole ballot is accepted or rejected together
    /// so a crafted packet with five good picks and one bad one can't quietly cast the five. This also fixed a
    /// real bug found 2026-08-15 (see aaemu-siege-castle-hero-nation memory): hero_votes' primary key didn't
    /// include candidate_character_id, so REPLACE INTO in a per-candidate loop silently overwrote all but the
    /// last pick - the PK now includes it (see the accompanying migration), and voting is rejected outright
    /// (not silently truncated) once a voter has already cast a ballot this cycle, matching a rejected
    /// community PR's own design (github.com/AAEmu/AAEmu/pull/1516) of treating a vote as final rather than
    /// changeable.
    /// </remarks>
    public void Vote(GameConnection connection, IReadOnlyCollection<ulong> candidateCharacterIds)
    {
        var voter = connection?.ActiveChar;
        if (voter == null)
        {
            Logger.Warn("Vote: no active character on connection");
            return;
        }

        var (cycle, phase) = GetEffective(DateTime.UtcNow);
        if (cycle == null || phase != HeroPhase.HeroVoting)
        {
            Logger.Info("Vote({0}): rejected, cycle={1} phase={2} (need HeroVoting)", voter.Name, cycle?.Id, phase);
            return;
        }

        // Gated on LeadershipPeriodPoint (the PREVIOUS period's frozen figure), not the current running
        // total - the client's native X2Hero:IsVoter() reads exactly that value (SCCharacterGamePointsPacket
        // slot 12 / SCHeroSeasonOffPacket), so a server-side check against anything else can disagree with
        // what the client already decided to show. See Character.LeadershipPeriodPoint's doc comment and
        // D:\aa\hero-vote-bug-report.txt.
        var condition = HeroGameData.Instance.GetCondition(cycle.HeroConditionId);
        if (condition != null && (voter.Level < condition.VotableLevel || voter.LeadershipPeriodPoint < condition.VotableLeadershipPoint))
        {
            Logger.Info("Vote({0}): rejected, level={1}/{2} leadership(period)={3}/{4}", voter.Name,
                voter.Level, condition.VotableLevel, voter.LeadershipPeriodPoint, condition.VotableLeadershipPoint);
            voter.SendErrorMessage(ErrorMessageType.NoPerm);
            return;
        }

        if (candidateCharacterIds == null || candidateCharacterIds.Count == 0)
        {
            Logger.Info("Vote({0}): rejected, empty ballot", voter.Name);
            return;
        }

        var factionId = ResolveNationFactionId(voter);
        var seats = HeroGameData.Instance.SeatsFor(factionId);
        if (candidateCharacterIds.Count > Math.Max(seats, 1))
        {
            Logger.Info("Vote({0}): rejected, {1} picks exceeds {2} seats for faction {3}",
                voter.Name, candidateCharacterIds.Count, seats, factionId);
            return;
        }

        using var connection2 = MySQL.CreateConnection();
        if (HasVoted(connection2, cycle.Id, voter.Id))
        {
            Logger.Info("Vote({0}): rejected, already voted this cycle {1}", voter.Name, cycle.Id);
            return;
        }

        var candidateIds = candidateCharacterIds.Select(id => (uint)id).Distinct().ToList();
        foreach (var candidateId in candidateIds)
        {
            using var checkCandidate = connection2.CreateCommand();
            checkCandidate.CommandText = "SELECT COUNT(*) FROM hero_candidates WHERE cycle_id=@c AND faction_id=@f AND character_id=@ch AND abstained=0";
            checkCandidate.Parameters.AddWithValue("@c", cycle.Id);
            checkCandidate.Parameters.AddWithValue("@f", factionId);
            checkCandidate.Parameters.AddWithValue("@ch", candidateId);
            checkCandidate.Prepare();
            if (Convert.ToInt64(checkCandidate.ExecuteScalar()) == 0)
            {
                Logger.Info("Vote({0}): rejected, candidate {1} is not a standing candidate in cycle {2} faction {3}",
                    voter.Name, candidateId, cycle.Id, factionId);
                return;
            }
        }

        foreach (var candidateId in candidateIds)
        {
            using var insertVote = connection2.CreateCommand();
            insertVote.CommandText = "INSERT INTO hero_votes (cycle_id, faction_id, voter_character_id, candidate_character_id) VALUES (@c,@f,@v,@ch)";
            insertVote.Parameters.AddWithValue("@c", cycle.Id);
            insertVote.Parameters.AddWithValue("@f", factionId);
            insertVote.Parameters.AddWithValue("@v", voter.Id);
            insertVote.Parameters.AddWithValue("@ch", candidateId);
            insertVote.Prepare();
            insertVote.ExecuteNonQuery();
        }

        RecountVotes(connection2, cycle.Id, factionId);
        Logger.Info("Vote({0}): recorded, candidates=[{1}] cycle={2} faction={3}", voter.Name, string.Join(",", candidateIds), cycle.Id, factionId);

        // Immediate feedback, not just on the next SendHeroInfo - see that method's own comment for why this
        // packet matters at all (X2Hero:IsAlreadyVoted, gates the checkbox/vote button).
        voter.SendPacket(new SCHeroVotingPacket((int)cycle.Id, 1));
    }

    public void Abstain(GameConnection connection)
    {
        var character = connection?.ActiveChar;
        if (character == null)
            return;

        var (cycle, phase) = GetEffective(DateTime.UtcNow);
        if (cycle == null || phase != HeroPhase.HeroAbstain)
            return;

        SetAbstained(character, cycle.Id, true);
    }

    public void DropoutComeback(GameConnection connection)
    {
        var character = connection?.ActiveChar;
        if (character == null)
            return;

        var (cycle, phase) = GetEffective(DateTime.UtcNow);
        if (cycle == null || phase != HeroPhase.HeroAbstain)
            return;

        SetAbstained(character, cycle.Id, false);
    }

    private void EnsureCandidatesComputed(HeroCycle cycle)
    {
        using var connection = MySQL.CreateConnection();
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM hero_candidates WHERE cycle_id=@cycleId";
            check.Parameters.AddWithValue("@cycleId", cycle.Id);
            check.Prepare();
            if (Convert.ToInt64(check.ExecuteScalar()) > 0)
                return; // already computed for this cycle - idempotent across ticks/restarts
        }

        var condition = HeroGameData.Instance.GetCondition(cycle.HeroConditionId);
        if (condition == null)
            return;

        foreach (var factionId in HeroGameData.Instance.FactionsWithRewards)
        {
            var candidates = new List<(uint characterId, int points)>();
            using (var select = connection.CreateCommand())
            {
                // Simplification: hero_conditions.leadership_ranking_scope (a wider "ranked" tier before the
                // narrower candidate cut) isn't materialized separately - it only matters for a leaderboard
                // display this pass doesn't build (no confirmed SC list-packet layout). Candidates are just the
                // top hero_candidate_scope characters meeting the candidacy thresholds directly.
                //
                // characters.faction_id is always the RACE-level sub-faction (e.g. Elf 103), never the
                // NATION-level id this loop's factionId actually is (e.g. Nuia 148) - confirmed live
                // 2026-09-05 no real character ever carries a nation-level faction_id, so a direct
                // faction_id=@f match here always found zero real candidates. Expand to every race id under
                // this nation instead (see RaceFactionIdsUnderNation/ResolveNationFactionId's doc comment).
                var raceFactionIds = RaceFactionIdsUnderNation(factionId);
                var placeholders = string.Join(",", raceFactionIds.Select((_, i) => "@f" + i));
                select.CommandText = $"SELECT id, leadership_point FROM characters WHERE faction_id IN ({placeholders}) AND level>=@lvl AND leadership_point>=@pt ORDER BY leadership_point DESC LIMIT @scope";
                for (var i = 0; i < raceFactionIds.Count; i++)
                    select.Parameters.AddWithValue("@f" + i, raceFactionIds[i]);
                select.Parameters.AddWithValue("@lvl", condition.HeroCandidateMinLevel);
                select.Parameters.AddWithValue("@pt", condition.HeroCandidateMinPoint);
                select.Parameters.AddWithValue("@scope", condition.HeroCandidateScope > 0 ? condition.HeroCandidateScope : 16);
                select.Prepare();
                using var reader = select.ExecuteReader();
                while (reader.Read())
                    candidates.Add(((uint)reader.GetInt32(0), reader.GetInt32(1)));
            }

            foreach (var (characterId, points) in candidates)
            {
                using (var insert = connection.CreateCommand())
                {
                    insert.CommandText = "INSERT INTO hero_candidates (cycle_id, faction_id, character_id, leadership_point_at_ranking) VALUES (@c,@f,@ch,@p)";
                    insert.Parameters.AddWithValue("@c", cycle.Id);
                    insert.Parameters.AddWithValue("@f", factionId);
                    insert.Parameters.AddWithValue("@ch", characterId);
                    insert.Parameters.AddWithValue("@p", points);
                    insert.Prepare();
                    insert.ExecuteNonQuery();
                }

                SendCandidateMail(characterId, condition);
            }

            Logger.Info("Hero cycle {0} faction {1}: {2} candidates", cycle.Id, factionId, candidates.Count);
        }
    }

    private void EnsureElectionFinalized(HeroCycle cycle)
    {
        using var connection = MySQL.CreateConnection();
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM hero_candidates WHERE cycle_id=@c AND elected=1";
            check.Parameters.AddWithValue("@c", cycle.Id);
            check.Prepare();
            if (Convert.ToInt64(check.ExecuteScalar()) > 0)
                return; // already finalized for this cycle
        }

        var condition = HeroGameData.Instance.GetCondition(cycle.HeroConditionId);

        foreach (var factionId in HeroGameData.Instance.FactionsWithRewards)
        {
            var ranked = new List<(uint characterId, long votes)>();
            using (var select = connection.CreateCommand())
            {
                select.CommandText = "SELECT character_id, votes FROM hero_candidates WHERE cycle_id=@c AND faction_id=@f AND abstained=0 ORDER BY votes DESC, leadership_point_at_ranking DESC";
                select.Parameters.AddWithValue("@c", cycle.Id);
                select.Parameters.AddWithValue("@f", factionId);
                select.Prepare();
                using var reader = select.ExecuteReader();
                while (reader.Read())
                    ranked.Add(((uint)reader.GetInt32(0), reader.GetInt64(1)));
            }

            for (var i = 0; i < ranked.Count; i++)
            {
                var (characterId, _) = ranked[i];
                var ranking = i + 1;
                var elected = i == 0;

                using (var update = connection.CreateCommand())
                {
                    update.CommandText = "UPDATE hero_candidates SET elected=@e WHERE cycle_id=@c AND faction_id=@f AND character_id=@ch";
                    update.Parameters.AddWithValue("@e", elected);
                    update.Parameters.AddWithValue("@c", cycle.Id);
                    update.Parameters.AddWithValue("@f", factionId);
                    update.Parameters.AddWithValue("@ch", characterId);
                    update.Prepare();
                    update.ExecuteNonQuery();
                }

                var reward = HeroGameData.Instance.GetReward(factionId, ranking);
                if (reward != null)
                    SendRewardMail(characterId, ranking, reward, condition, elected);

                if (elected)
                    SendStatueConstructionItemMail(characterId, factionId);
            }

            if (ranked.Count > 0)
                Logger.Info("Hero cycle {0} faction {1}: elected character {2} ({3} candidates ranked)", cycle.Id, factionId, ranked[0].characterId, ranked.Count);
        }
    }

    private static bool HasVoted(MySqlConnection connection, uint cycleId, uint characterId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM hero_votes WHERE cycle_id=@c AND voter_character_id=@v";
        command.Parameters.AddWithValue("@c", cycleId);
        command.Parameters.AddWithValue("@v", characterId);
        command.Prepare();
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static void RecountVotes(MySqlConnection connection, uint cycleId, uint factionId)
    {
        using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE hero_candidates hc
            SET votes = (SELECT COUNT(*) FROM hero_votes hv WHERE hv.cycle_id = hc.cycle_id AND hv.faction_id = hc.faction_id AND hv.candidate_character_id = hc.character_id)
            WHERE hc.cycle_id = @c AND hc.faction_id = @f
            """;
        update.Parameters.AddWithValue("@c", cycleId);
        update.Parameters.AddWithValue("@f", factionId);
        update.Prepare();
        update.ExecuteNonQuery();
    }

    private static void SetAbstained(Character character, uint cycleId, bool abstained)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE hero_candidates SET abstained=@a WHERE cycle_id=@c AND faction_id=@f AND character_id=@ch";
        command.Parameters.AddWithValue("@a", abstained);
        command.Parameters.AddWithValue("@c", cycleId);
        command.Parameters.AddWithValue("@f", ResolveNationFactionId(character));
        command.Parameters.AddWithValue("@ch", character.Id);
        command.Prepare();
        command.ExecuteNonQuery();
    }

    private static void SendCandidateMail(uint characterId, HeroCondition condition)
    {
        var name = NameManager.Instance.GetCharacterName(characterId);
        if (name == null)
            return;

        var mail = new BaseMail
        {
            MailType = MailType.HeroCandidateAlarm,
            Title = condition.HeroNewPeriodTitle,
            ReceiverName = name
        };
        mail.Header.SenderName = SystemSenderName;
        mail.Header.ReceiverId = characterId;
        mail.Header.Status = MailStatus.Unread;
        mail.Body.Text = condition.CandidateMailBody;
        mail.Body.RecvDate = DateTime.UtcNow;
        mail.Send();
    }

    private static void SendRewardMail(uint characterId, int ranking, HeroReward reward, HeroCondition condition, bool elected)
    {
        var name = NameManager.Instance.GetCharacterName(characterId);
        if (name == null)
            return;

        var mail = new BaseMail
        {
            MailType = MailType.HeroElectionItem,
            Title = elected ? condition?.HeroNewPeriodTitle ?? "Hero" : $"Hero ranking #{ranking}",
            ReceiverName = name
        };
        mail.Header.SenderName = SystemSenderName;
        mail.Header.ReceiverId = characterId;
        mail.Header.Status = MailStatus.Unread;
        mail.Body.Text = (elected ? condition?.ElectionMailBody : condition?.HeroBonusMailBody) ?? string.Empty;
        mail.Body.RecvDate = DateTime.UtcNow;

        var itemSet = ItemManager.Instance.GetItemSet(reward.ItemSetId);
        if (itemSet != null)
        {
            foreach (var setItem in itemSet.Items.Values)
            {
                var item = ItemManager.Instance.Create(setItem.ItemId, setItem.Count, 0, true);
                if (item != null)
                    mail.Body.Attachments.Add(item);
            }
        }

        mail.Send();
    }

    /// <summary>
    /// The 2 Hero-only phases of that faction's capital Statue (see
    /// <see cref="Models.Game.DoodadObj.Funcs.DoodadFuncFactionStatueDevote"/>) each need 5 devotions of a
    /// faction-specific item - 10 total. Confirmed via `doodad_func_devotes` (2026-08-31): Nuia's 안드리온
    /// 2세 석상/King Andrion II Statue uses 44816 (누이의 꿈/Nui's Dream), Haranya's 아마렌드라 4세 석상/
    /// Amarendra IV Statue uses 45131 (여왕의 영광/Queen's Glory), the Pirates' 모르페우스 석상/Morpheus
    /// Statue uses 45132 (진실의 눈/Eye of Truth). None of these 3 items appear in any `item_set_items` or
    /// `merchant_goods` row anywhere in the shipped data - nothing granted them to a Hero before this, which
    /// is very likely why statue construction "does not work" today (the Hero simply never had any to
    /// devote). Mailing 10 on election is the most direct fix given the evidence; the 90x GENERAL-player
    /// phase uses the SAME item and is left as a known, separate follow-up gap - no acquisition path for
    /// ordinary players was found either, and inventing one would be a guess.
    /// </summary>
    private static readonly Dictionary<uint, uint> StatueConstructionItemByFaction = new()
    {
        [148] = 44816, // Nuia
        [149] = 45131, // Haranya
        [114] = 45132  // Pirate
    };

    private static void SendStatueConstructionItemMail(uint characterId, uint factionId)
    {
        if (!StatueConstructionItemByFaction.TryGetValue(factionId, out var itemId))
            return;

        var name = NameManager.Instance.GetCharacterName(characterId);
        if (name == null)
            return;

        var item = ItemManager.Instance.Create(itemId, 10, 0, true);
        if (item == null)
            return;

        var mail = new BaseMail
        {
            MailType = MailType.HeroElectionItem,
            Title = "Faction Statue",
            ReceiverName = name
        };
        mail.Header.SenderName = SystemSenderName;
        mail.Header.ReceiverId = characterId;
        mail.Header.Status = MailStatus.Unread;
        mail.Body.Text = string.Empty;
        mail.Body.RecvDate = DateTime.UtcNow;
        mail.Body.Attachments.Add(item);
        mail.Send();
    }
}
