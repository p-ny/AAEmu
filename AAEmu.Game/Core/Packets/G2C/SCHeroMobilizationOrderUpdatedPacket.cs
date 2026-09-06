using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

/// <summary>
/// Faction-wide broadcast of the Mobilization Order state/counters - the client's Lua surface refers to
/// this data as "maxMobilizationOrderCount"/"seasonMobilizationOrderCount". Sent alongside
/// SCFactionMobilizationOrderPacket - see HeroManager.IssueMobilizationOrder.
/// </summary>
/// <remarks>
/// 2026-08-31: brand new class - no class file existed before now, only an opcode entry (0x2B4) from
/// earlier research notes. RTTI-decoded the real Unpack (vtable+0x10 = FUN_39c62b80, which delegates its
/// tail sub-struct to FUN_39b4ab60):
/// <code>
/// +0x10: "action"     (1 byte, slot 0x90)  - primitive-confirmed name+width.
/// +0x18: zoneGroupId   (4 bytes, slot 0x80) - this project's established 4-byte-plain-int slot.
/// +0x20: unnamed       (8 bytes, slot 0x98) - this project's established 8-byte-plain-value slot;
///                       named "characterId" here as a best-effort guess (matches this project's
///                       convention of 8-byte id-shaped fields elsewhere), NOT primitive-confirmed.
/// +0x28: "todayCount"  (4 bytes, slot 0x80) - primitive-confirmed name+width.
/// +0x2c: "totalCount"  (4 bytes, slot 0x80) - primitive-confirmed name+width.
/// </code>
/// The +0x10-to-+0x18 gap (7 bytes) is ordinary C++ struct padding after a 1-byte field before an
/// 8-byte-aligned member - consistent with "action" genuinely being 1 byte, not evidence of a hidden field.
///
/// 2026-09-06: the +0x18 field resolved via live Ghidra+Frida trace - the client copies it into its cached
/// Hero-info struct and gates the whole "issue mobilization order" dialog on it exactly matching the flag
/// doodad's own zone group id (checked before the todayCount/max comparison even runs). Every call site was
/// sending 0 here, which can never match a real zone group, so the dialog was permanently unreachable and
/// always fell through to the generic "used all your orders" message regardless of the real count - not a
/// client bug, a real fixable server gap. Renamed from "unknown1".
/// </remarks>
public class SCHeroMobilizationOrderUpdatedPacket(byte action, uint zoneGroupId, ulong characterId, uint todayCount, uint totalCount)
    : GamePacket(SCOffsets.SCHeroMobilizationOrderUpdatedPacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(action);
        stream.Write(zoneGroupId);
        stream.Write(characterId);
        stream.Write(todayCount);
        stream.Write(totalCount);
        return stream;
    }
}
