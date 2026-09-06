using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Core.Packets.G2C;

/// <summary>Which hero schedule event is running, for which season, and whether this specific send should
/// (re-)announce it beginning (State=0 - fires the client's day-alert banner/icon-show), just silently resync
/// (State=1), or announce it ending (State=2). See SCHeroEventStatePacket's doc comment and
/// HeroManager.BuildEventStateEntries for why State is a real per-call choice, not a hardcoded constant.</summary>
public readonly record struct HeroEventStateEntry(HeroPhase ScheduleEvent, uint Season, byte State = 1);

/// <summary>
/// Recovered 2026-08-14 via Ghidra (see aaemu-siege-castle-hero-nation memory), then corrected the same day
/// against a rejected community PR's independent (and more complete) reverse-engineering of the same opcode
/// (github.com/AAEmu/AAEmu/pull/1516 - closed for code-quality reasons, but the disassembly findings are real
/// and cross-checked cleanly against our own recovery, so worth trusting for the field semantics).
///
/// The middle int32 was previously guessed as factionId - wrong. It is the SEASON (heros.id/
/// hero_schedules.hero_id): the client resolves each slot by scanning hero_schedules for the row whose
/// event_id and hero_id BOTH match. Sending a faction id there meant that lookup could never match any real
/// row, so the client's "is this event live" resolution silently failed for every entry - very likely the root
/// cause of both the missing HUD election-active icon and the voting-machine/candidates-panel "No Hero
/// candidates have been selected yet" message persisting even once real candidate data was already being sent
/// via SCHeroCandidateListPacket (that data renders from a separate push and doesn't depend on this resolution
/// succeeding, which is exactly why the row showed up while the phase-driven UI stayed stuck).
///
/// Also previously wrong: the trailing byte was written as the HeroPhase value again (reusing the
/// "HeroScheduleEvent" byte a second time), not a real state flag. The PR's recovery: state 2 means "over",
/// anything else counts as live.
///
/// **2026-08-14, second correction, this one from a full Ghidra trace (agent ac9e5833b63a32e5c) of the real
/// packet-apply handler (`FUN_39114dd0`, reached via the packet's vftable/PacketFunctor registration, not just
/// the deserializer found earlier) - explains why the HUD election-active icon appeared briefly on zone-enter
/// then always vanished again:** `clearAll` unconditionally fires `END_HERO_ELECTION_PERIOD` (hides the icon,
/// `alert.lua`'s handler is a bare `frame:Show(false)`) before applying the new entries. Per entry, the
/// *only* way the client fires the compensating `START_HERO_ELECTION_PERIOD` (which is what
/// `ShowHeroElectionAlert()` is wired to) is `eventType==3 (hero_voting) && state==0` - **state 1 ("live")
/// updates the slot silently, no event fires at all**. We were unconditionally sending state=1 (this file's
/// prior comment's "always 1" was itself the bug, not just a leftover value) - correct per `state != 2` still
/// counting as active for `IsElectionPeriod()`'s own read, but it meant nothing ever told the client to
/// re-show the icon after `clearAll` just hid it, on every single send.
///
/// Fix: send state=0 for a hero_voting entry to make the client actually re-announce it (fires
/// `START_HERO_ELECTION_PERIOD` - and also `HERO_ELECTION_DAY_ALERT`, a center-screen banner via
/// `hero_message.lua`).
///
/// **2026-08-15, third correction: confirmed live that the banner WAS excessive/spammy** - sending state=0
/// unconditionally on every call fired "the first Friday and Saturday of each month are hero election days..."
/// every single time the user opened the Hero or voting window, not just once when the phase actually began.
///
/// **2026-08-15, fourth correction, per a rejected community PR's own BuildStates model
/// (github.com/AAEmu/AAEmu/pull/1516): the per-character dedup that fixed the above was treating the symptom,
/// not the cause.** State=0/2 must ONLY ever come from a genuine, just-detected phase transition - never from
/// a plain resync (login, zone-enter, window-open, on-demand faction request), which has nothing to announce
/// and must always send 1. `HeroManager.BuildEventStateEntries` now takes an explicit "what phase, if any, was
/// just left" parameter instead of per-character memory: a real transition sends the entering phase as 0 and
/// the leaving phase (if it's no longer anyone's current phase) as 2; every resync path passes no leaving
/// phase and gets 1 for everything. `IsElectionPeriod()`'s own `state != 2` read is correct either way.
///
/// **2026-08-15, fifth correction - `clearAll` itself was the remaining piece.** Every send in this codebase
/// hardcoded `clearAll=true`, including plain resyncs - but `clearAll` "unconditionally fires
/// END_HERO_ELECTION_PERIOD (hides the icon)... before applying the new entries" (see above), so EVERY resync
/// re-hid the icon, and a resync's own entries only ever carry state=1 ("stored silently", by design - it must
/// not re-announce). Nothing was left to ever re-show it after the very next zone-enter cleared it again -
/// this is what made the icon "appear briefly then vanish on every zone change" the user kept re-reporting.
/// The PR's own steady-state usage sends `clearAll=false` for exactly this reason (its own comment: "this is
/// an update to a client that already has the schedule, so the entries overwrite in place" - no clear, no
/// re-hide). `HeroManager` now sends `clearAll=false` from every call site; nothing in this codebase currently
/// needs the true/reset form, so it is not used anywhere as of this writing.
/// </summary>
public sealed class SCHeroEventStatePacket(bool clearAll, IReadOnlyList<HeroEventStateEntry> entries)
    : GamePacket(SCOffsets.SCHeroEventStatePacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(clearAll);
        stream.Write(entries.Count);
        foreach (var entry in entries)
        {
            stream.Write((byte)entry.ScheduleEvent); // enum_hero_schedule_events row (1-4)
            stream.Write((int)entry.Season);          // heros.id / hero_schedules.hero_id - NOT the faction
            stream.Write(entry.State);                // 0 = announce (icon+banner), 1 = silent resync, 2 = over
        }
        return stream;
    }
}
