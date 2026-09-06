using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Housing;

namespace AAEmu.Game.Core.Managers;

public interface IDominionManager : ILoadable
{
    IEnumerable<DominionData> Dominions { get; }
    DominionData GetByZoneId(ushort zoneId);

    /// <summary>The claimed dominion whose territory (TerritoryData.RadiusDominion around its X/Y) contains this world position, or null.</summary>
    DominionData GetDominionAtPosition(ushort zoneId, float x, float y);

    /// <summary>
    /// Claims <paramref name="zoneId"/> for <paramref name="expeditionId"/> on behalf of <paramref name="lodestone"/>,
    /// persists it and broadcasts the result. Returns null (and sends <see cref="Models.Game.ErrorMessageType.DominionAlreadyDedclared"/>
    /// to <paramref name="declarer"/>) if the zone group is already claimed.
    /// </summary>
    DominionData Declare(ushort zoneId, uint expeditionId, House lodestone, Models.Game.Char.Character declarer);

    /// <summary>
    /// Hero/faction-claim path for the 4 siege_zones territories (zone groups 33/34/43/44) - claims for
    /// <paramref name="owningFactionId"/> (the raw FactionsEnum id, 148 Nuia / 149 Haranya) directly, NOT tied
    /// to any guild. Caller (DeclareDominion.cs) is responsible for checking the declarer is their faction's
    /// currently-elected Hero first. Same already-claimed/error-message behavior as <see cref="Declare"/>.
    /// </summary>
    DominionData DeclareForFaction(ushort zoneId, uint owningFactionId, House lodestone, Models.Game.Char.Character declarer);

    /// <summary>Changes the local dominion tax rate; only the owning Expedition (or, for a faction-owned zone, that faction's current Hero) may call this.</summary>
    void UpdateTaxRate(GameConnection connection, ushort zoneId, int taxRate);

    /// <summary>Sends every currently-claimed dominion to <paramref name="connection"/> (zone-enter / on-request push).</summary>
    void SendAllDominionsTo(GameConnection connection);

    /// <summary>System-driven (not player-driven) siege-phase update; used by SiegeManager's schedule tick.</summary>
    void UpdateSiegePeriod(ushort zoneId, byte period);

    /// <summary>Weekly: mails out each dominion's current tax pool to its owning Expedition's leader and resets it. Called by DominionTaxPayoutTask; also callable directly for tests/GM use.</summary>
    void PayoutTax();

    /// <summary>Current guard_tower_steps progression (0 = none unlocked yet) for a claimed zone, or 0 if not claimed.</summary>
    int GetGuardTowerStep(ushort zoneId);

    /// <summary>
    /// Advances a claimed Dominion's Guard Tower by one guard_tower_steps step (unlocking more gates/walls,
    /// applying the step's buff to the House), persists it, and returns the new step - or the current step,
    /// unchanged, if already at the max defined step or the zone isn't claimed. What player action should call
    /// this is not decided yet - exposed for a future contribution/resource system or GM command to drive.
    /// </summary>
    int AdvanceGuardTowerStep(ushort zoneId);

    /// <summary>
    /// Places a real House for one of the territory-construction blueprint items that has a real item_housings
    /// design (Guardian Altar, Farm, Workshop, Warehouse, Overseer Post, Wall, Gate, Tower, etc.) at
    /// <paramref name="declarer"/>'s current position, in a zone group that's already claimed. Returns null (a
    /// silent no-op, not an error) if the item has no design, or the zone isn't claimed, or - for the 5 unique
    /// per-territory designs from dominion_housings - one is already built here. See
    /// DominionManager.TryBuildDominionStructure's own doc comment for the full data trail.
    /// </summary>
    House TryBuildDominionStructure(ushort zoneId, uint itemTemplateId, Models.Game.Char.Character declarer);

    /// <summary>Re-sends WZDominionData to Zone for an already-claimed zone group. Returns false if not claimed or the House/its zone can't be resolved.</summary>
    bool ResyncZone(ushort zoneId);

    /// <summary>
    /// Called whenever a raw zone (re)connects (real ZwOpcodes.ZoneLoaded event, see
    /// WorldIntegration.NotifyZoneReadyForDominion) - re-notifies Zone about any claimed Dominion whose House
    /// lives in that raw zone, and re-announces its Territory Agent NPC. See DominionManager's own
    /// implementation doc comment for the real bug this fixes (Zone hosts reload independently of World and
    /// remember nothing on their own).
    /// </summary>
    void RelayAllToZone(uint rawZoneId);

    /// <summary>
    /// DIAGNOSTIC ONLY, 2026-08-20 - sends WZDominionData for an already-claimed zone group but with every
    /// field forced to zero/default except ZoneId/ExpeditionId (kept real so Zone can still match it up).
    /// Isolates whether a live crash is data-dependent (a real non-zero value getting misread as a huge count
    /// due to a field-width mismatch) vs. purely structural (crashes regardless of content). Optional trailing
    /// zero-byte padding (safe - see WZDominionDataPacket's doc comment) lets the true required packet length
    /// be bisected live in-game without a rebuild per attempt. Remove once the remaining WZDominionData
    /// field-width questions are resolved - see aaemu-zone-wire-format-danger memory.
    /// </summary>
    bool ResyncZoneWithZeroedTestData(ushort zoneId, int diagnosticPaddingBytes = 0);

    /// <summary>Persists a newly-placed National Monument's doodad id/position onto a claimed dominion. Caller (NationManager) owns the placement rules - this just saves the result.</summary>
    void SetNationalMonument(ushort zoneId, long dbId, float x, float y, float z);

    /// <summary>
    /// GM/testing tool: reverses everything Declare() does for a claimed zone group - deletes its dominions
    /// row, resets the lodestone House back to its pre-claim buried state (owner cleared, build step reset to
    /// 0, attached doodads cleaned up), removes the initial-claim and any guard-tower-step buffs from the
    /// House, despawns the Territory Agent NPC, and broadcasts the cleared state to already-online clients.
    /// Returns false if the zone group isn't currently claimed. Does not touch the leftover native "Guard
    /// Tower Summon Point" doodad markers (never AAEmu-tracked) - the same lodestone House can be re-claimed
    /// afterward via the normal Declare() flow or <see cref="ClaimTerritory"/>.
    /// </summary>
    bool UnclaimTerritory(ushort zoneId);

    /// <summary>
    /// GM/testing convenience: claims <paramref name="zoneId"/> for <paramref name="expedition"/> without a
    /// live skill-cast/doodad-interaction context. Resolves the zone's already-seeded lodestone House by its
    /// guard_tower_settings-mapped template id and calls <see cref="Declare"/> - fails the same way Declare
    /// does if already claimed, and returns null if this build has no lodestone House for the zone group.
    /// <paramref name="declarer"/> is optional (used for House.OwnerId/faction resolution, same as a real
    /// declare) - pass null to fall back to the boot-time expedition-owner faction resolution.
    /// </summary>
    DominionData ClaimTerritory(ushort zoneId, Models.Game.Expeditions.Expedition expedition, Models.Game.Char.Character declarer);

    /// <summary>
    /// GM/testing convenience, faction path: claims <paramref name="zoneId"/> for <paramref name="factionId"/>
    /// directly, bypassing the normal Hero-eligibility check - see <see cref="DeclareForFaction"/>.
    /// </summary>
    DominionData ClaimTerritoryForFaction(ushort zoneId, Models.StaticValues.FactionsEnum factionId, Models.Game.Char.Character declarer);

    /// <summary>
    /// Old (guild-owned) castle system, Exeloch/Sungold Fields only - current Keep/Castle/Palace tier
    /// (0 = none, 1 = Keep, 2 = Castle, 3 = Palace) for a claimed zone, or 0 if not claimed. See
    /// AdvanceCastleTier.cs.
    /// </summary>
    int GetCastleTier(ushort zoneId);

    /// <summary>
    /// Advances a claimed Dominion's castle tier to <paramref name="targetTier"/> (caller - AdvanceCastleTier.cs -
    /// already validated this is exactly current+1) and persists it. Returns the new tier, or the current tier
    /// unchanged if the zone isn't claimed.
    /// </summary>
    int AdvanceCastleTier(ushort zoneId, int targetTier);

    /// <summary>
    /// Player-nation founding (NationManager.DeclareIndependence): converts an already-claimed, guild-owned
    /// dominion to nation ownership - clears ExpeditionId, sets OwningFactionId to the nation's own real
    /// faction id, leaves the House/guard-tower-step/build-state entirely untouched (unlike UnclaimTerritory,
    /// this is a same-territory ownership handoff, not a release). Returns false if the zone isn't claimed.
    /// </summary>
    bool TransferToFaction(ushort zoneId, uint newOwningFactionId);

    /// <summary>Voluntary nation disband (NationManager.Disband, forced=false): hands an already-claimed, nation-owned dominion back to the founding guild. Returns false if the zone isn't claimed.</summary>
    bool TransferToGuild(ushort zoneId, uint expeditionId);
}
