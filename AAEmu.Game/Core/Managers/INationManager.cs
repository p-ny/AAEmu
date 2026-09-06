using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.StaticValues;

namespace AAEmu.Game.Core.Managers;

public interface INationManager : ILoadable
{
    bool IsNation(ushort zoneId);
    uint? GetSovereign(ushort zoneId);

    /// <summary>Called for every completed quest; internally filters to the founding quest (7986) and calls DeclareIndependence.</summary>
    void OnQuestCompleted(Character character, uint questContextId);

    /// <summary>
    /// Founds a nation on the caller's claimed Dominion with them as Sovereign, if they have one and it isn't
    /// one already. No-op with a log warning if they hold no Dominion - the quest chain's own prerequisites
    /// should prevent that in practice, this is a defensive check, not the primary gate.
    /// </summary>
    void DeclareIndependence(Character character);

    /// <summary>The nation zone a character is Sovereign of, or null if they aren't one.</summary>
    ushort? GetNationOfSovereign(uint characterId);

    /// <summary>
    /// Places the caller's National Monument at (x,y,z) if they're a Sovereign, the position is inside their own
    /// Dominion, they don't already have one, and that zone group isn't currently in the Siege period.
    /// </summary>
    void PlaceNationalMonument(Character character, float x, float y, float z);

    /// <summary>Proposes a friend or hostile relation from the requester's nation to another, by mail.</summary>
    void RequestRelation(Character requester, ushort targetZoneId, bool friend);

    /// <summary>Accepts or rejects a pending relation request from another nation.</summary>
    void RespondRelation(Character responder, ushort requesterZoneId, bool accept);

    /// <summary>"friend", "hostile", or "neutral" for the pair of nation zones.</summary>
    string GetRelationStatus(ushort zoneIdA, ushort zoneIdB);

    /// <summary>The player-chosen nation name, or null if never set (no live client trigger delivers one yet).</summary>
    string GetNationName(ushort zoneId);

    /// <summary>Sets/persists a nation's name. False if no nation exists at that zone.</summary>
    bool SetNationName(ushort zoneId, string name);

    /// <summary>Sovereign invites a Nuia/Haranya character (not already nation-affiliated) into their nation. False on any ineligibility.</summary>
    bool InviteToNation(Models.Game.Char.Character sovereign, Models.Game.Char.Character target);

    /// <summary>A nation member reverts to their home faction. False if they're not in a nation, or still in a nation-bound guild (must leave that first).</summary>
    bool LeaveNation(Models.Game.Char.Character character);

    /// <summary>Current Sovereign hands leadership to another member of the same nation.</summary>
    bool TransferSovereign(Models.Game.Char.Character currentSovereign, Models.Game.Char.Character newSovereign);

    /// <summary>
    /// Disbands a nation. forced=false (voluntary): territory reverts to the founding guild, every member
    /// guild's nation-binding is cleared (guilds themselves survive). forced=true (e.g. a future siege-loss
    /// grace-period expiry): territory is left untouched, every member guild is disbanded outright. Either way
    /// every member character reverts to their home faction. False if no nation exists at that zone.
    /// </summary>
    bool Disband(ushort zoneId, bool forced);

    /// <summary>
    /// Sovereign-only: sets the nation's diplomatic relation toward Nuia or Haranya (only valid targets) to
    /// Neutral/Hostile/Friendly ("Friendly" = Allied). Persists and updates the live faction relation both ways,
    /// so every existing GetRelationStateTo-driven check (aggro, doodad interaction, trade, plot conditions -
    /// see SystemFaction.GetRelationState) respects it immediately, without a bespoke access-control mechanism.
    /// False if the caller isn't a Sovereign or the target isn't Nuia/Haranya.
    /// </summary>
    bool SetAllianceRelation(Character sovereign, FactionsEnum target, RelationState state);

    /// <summary>The nation's current relation toward Nuia or Haranya. RelationState.Neutral if no nation/invalid target.</summary>
    RelationState GetAllianceRelation(ushort zoneId, FactionsEnum target);
}
