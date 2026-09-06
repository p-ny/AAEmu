using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

/// <summary>
/// A Hero submits a Mobilization Order (X2Faction:RequestIssuanceOfMobilizationOrder, the actual
/// "issuance" action - distinct from CSFactionMobilizationOrderPacket, which per its own debug-string
/// name ("RequestMobilizationOrder") looks like a plain status query, not a submit). See
/// HeroManager.IssueMobilizationOrder.
/// </summary>
/// <remarks>
/// 2026-08-31: RTTI-decoded the real Unpack (FUN_39c5d570) - a single optional-presence Bc-encoded id,
/// matching this project's established optional-bracket pattern (slot 0x1a0/0x1a8). Most likely the
/// mobilization-order doodad's own ObjId (matching the DoodadFuncHeroElection precedent, where
/// interacting with a doodad round-trips through the server before the UI opens) rather than a target
/// character/zone - not decompile-confirmed which, but the order TYPE (war/choice/peace) is derived
/// server-side from which flag item the Hero holds (see HeroManager.IssueMobilizationOrder's own remarks
/// for why), so this field isn't actually needed to act correctly regardless of its exact meaning.
/// </remarks>
public class CSFactionIssuanceOfMobilizationOrderPacket() : GamePacket(CSOffsets.CSFactionIssuanceOfMobilizationOrderPacket, 1)
{
    public uint Bc { get; private set; }

    public override void Read(PacketStream stream)
    {
        Bc = stream.ReadBc();

        if (Connection.ActiveChar != null)
            HeroManager.Instance.IssueMobilizationOrder(Connection.ActiveChar);
    }
}
