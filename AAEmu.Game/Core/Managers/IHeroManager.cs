using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers;

public interface IHeroManager : ILoadable
{
    /// <summary>Recomputes candidates when a cycle enters HeroAbstain, and finalizes/pays out when it enters HeroPeriod. Idempotent - safe to call every tick.</summary>
    void Tick();

    /// <summary>
    /// Whether this character is their faction's most recently elected Hero (elected=1 in the latest
    /// hero_candidates cycle for their faction_id) - backs UnitReqsKindType.Hero (kind_id=79). Deliberately
    /// not scoped to "inside the exact HeroPeriod DB window" - a Hero's status is meant to persist through
    /// their reign until the next election, not just the narrow window row.
    /// </summary>
    bool IsCurrentHero(Character character);

    /// <summary>Whether this character is a standing candidate in the most recently computed cycle for their faction - backs UnitReqsKindType.NotHeroNotCandidate.</summary>
    bool IsCandidate(Character character);

    /// <summary>This character's own hero_grades tier (1-4), or 0 if not a currently-serving hero - backs the Hero mission board's grade-based eligibility check.</summary>
    int GradeOf(Character character);

    /// <summary>Pushes the Hero panel's current phase/candidate/ranking data to a character. Call at login.
    /// showUi should stay false except from the actual CSHeroCandidateListPacket handler - see HeroManager's
    /// doc comment for why.</summary>
    void SendHeroInfo(Character character, bool showUi = false);

    /// <summary>Same as SendHeroInfo, but scopes the candidate/ranking/hero/score lists to one specific faction - see HeroManager's doc comment.</summary>
    void SendHeroInfoForRequestedFaction(Character character, uint requestedFactionId, bool showUi = false);

    /// <summary>
    /// GM testing aid: force-marks (or unmarks) the caller as their faction's elected Hero using a synthetic
    /// cycle id, without waiting for a real election. Not tied to the real hero_schedules cycle machinery -
    /// purely a shortcut for testing UnitReqsKindType.Hero-gated content.
    /// </summary>
    /// <returns>The new state (true = now marked as Hero).</returns>
    bool ToggleTestHero(Character character);

    /// <summary>
    /// CSHeroVotingPacket's payload is a count-prefixed set of candidate character ids (multi-select - a
    /// nation elects multiple seats) plus a trailing voter-id scalar, confirmed via Ghidra 2026-08-14/15 (see
    /// aaemu-siege-castle-hero-nation memory). The whole ballot is recorded or rejected together.
    /// </summary>
    void Vote(GameConnection connection, IReadOnlyCollection<ulong> candidateCharacterIds);

    /// <summary>Always acts on the caller's own character regardless of the packet's payload - CSHeroAbstainPacket's ulong field meaning is unconfirmed, and trusting a client-supplied id to abstain an arbitrary candidate would be exploitable.</summary>
    void Abstain(GameConnection connection);

    /// <summary>Reverses a prior Abstain, while still in the HeroAbstain phase of the same cycle.</summary>
    void DropoutComeback(GameConnection connection);
}
