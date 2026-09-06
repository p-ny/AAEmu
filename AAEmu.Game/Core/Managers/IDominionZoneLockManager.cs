namespace AAEmu.Game.Core.Managers;

/// <summary>
/// GM-controlled per-zone-group lock on the castle system, 2026-08-24. Scope is deliberately narrow (per user
/// direction): blocks NEW claims only - DeclareDominion.cs's skill-driven claim and the /claimterritory GM
/// command both check this before creating anything. Existing claims (and everything derived from them - guard
/// tower progress, castle tier, buildings, nation founding) are completely untouched by a lock; this is a "no
/// new territories here" switch, not a freeze. Applies uniformly across both castle systems (Hero/faction via
/// DominionManager, guild-owned via GuildDominionManager) from one shared list, since neither manager's own
/// claimed-state changes - checked at the two real entry points instead of threaded through either manager.
/// </summary>
public interface IDominionZoneLockManager : ILoadable
{
    IEnumerable<ushort> LockedZones { get; }
    bool IsLocked(ushort zoneId);
    void Lock(ushort zoneId);
    void Unlock(ushort zoneId);
}
