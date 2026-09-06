using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Housing;

namespace AAEmu.Game.Core.Managers;

/// <summary>
/// Old (guild-owned) castle system - Exeloch/Sungold Fields only. Split out of DominionManager/IDominionManager
/// 2026-08-24 to isolate the two castle systems into fully separate features (own `guild_dominions` MySQL table,
/// own `guild_dominion_housings` housing-template registry, own decision logic) rather than continuing to
/// branch shared code on OwningFactionId. Deliberately does NOT cover Hero/faction-only concepts that never
/// applied here anyway (siege timers, national monuments, nation transfer) - those stay on DominionManager for
/// the 4 Hero/faction territories only.
/// </summary>
public interface IGuildDominionManager : ILoadable
{
    IEnumerable<DominionData> GuildDominions { get; }
    DominionData GetByZoneId(ushort zoneId);

    /// <summary>The claimed guild dominion whose territory (TerritoryData.RadiusDominion around its X/Y) contains this world position, or null.</summary>
    DominionData GetDominionAtPosition(ushort zoneId, float x, float y);

    /// <summary>Sends every currently-claimed guild dominion to <paramref name="connection"/> (zone-enter / on-request push) - reuses SCDominionDataPacket, the same wire sender DominionManager uses.</summary>
    void SendAllDominionsTo(GameConnection connection);

    /// <summary>Current guard_tower_steps progression (0 = none unlocked yet) for a claimed zone, or 0 if not claimed.</summary>
    int GetGuardTowerStep(ushort zoneId);

    /// <summary>Advances a claimed guild dominion's Guard Tower by one guard_tower_steps step, persists it, and returns the new step - or the current step, unchanged, if already maxed or the zone isn't claimed.</summary>
    int AdvanceGuardTowerStep(ushort zoneId);

    /// <summary>Old-system equivalent of DominionManager.GetCastleTier - current Keep/Castle/Palace tier (0 = none, 1 = Keep, 2 = Castle, 3 = Palace) for a claimed zone, or 0 if not claimed.</summary>
    int GetCastleTier(ushort zoneId);

    /// <summary>Advances a claimed guild dominion's castle tier to <paramref name="targetTier"/> and persists it.</summary>
    int AdvanceCastleTier(ushort zoneId, int targetTier);

    /// <summary>
    /// Places a real House for one of the territory-construction blueprint items that has a real item_housings
    /// design, at <paramref name="declarer"/>'s current position, in an already-claimed guild zone group.
    /// Returns null (silent no-op) if the item has no design, the zone isn't claimed, or - for unique
    /// per-territory designs - one is already built here.
    /// </summary>
    House TryBuildDominionStructure(ushort zoneId, uint itemTemplateId, Models.Game.Char.Character declarer);

    /// <summary>Real claim path (used by DeclareDominion.cs, the skill-driven flow, and the /claimterritory GM command) for the guild-owned zones - see GuildDominionManager.Declare's doc comment.</summary>
    DominionData Declare(ushort zoneId, uint expeditionId, House lodestone, Models.Game.Char.Character declarer);

    /// <summary>GM/testing counterpart to DominionManager.ClaimTerritory, guild-owned zones only.</summary>
    DominionData ClaimTerritory(ushort zoneId, Models.Game.Expeditions.Expedition expedition, Models.Game.Char.Character declarer);

    /// <summary>GM/testing tool - releases a claimed guild dominion back to unclaimed.</summary>
    bool UnclaimTerritory(ushort zoneId);

    /// <summary>Re-announces every claimed guild dominion in <paramref name="rawZoneId"/> to a (re)loaded Zone process - wire this to the same ZwOpcodes.ZoneLoaded hook DominionManager.RelayAllToZone uses.</summary>
    void RelayAllToZone(uint rawZoneId);

    /// <summary>Changes a claimed guild dominion's local tax rate; only a member of the owning Expedition may call this.</summary>
    void UpdateTaxRate(GameConnection connection, ushort zoneId, int taxRate);
}
