using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.G2C;

/// <summary>
/// A second, older client code path that writes the same periodLeadershipPoint value
/// SCCharacterGamePointsPacket's slot 12 carries - must always be sent alongside it, never instead of it.
/// </summary>
/// <remarks>
/// Confirmed via a rejected community PR's independent reverse-engineering (github.com/AAEmu/AAEmu/pull/1516):
/// the client's handler for this opcode stores `score` straight into ClientPlayer+0xef0 (the exact same native
/// slot SCCharacterGamePointsPacket's slot 12 also writes) and raises a UI refresh event, ignoring `type`
/// entirely. That memory slot is the one and only place X2Hero:IsVoter() reads periodLeadershipPoint from -
/// previously this packet was never constructed anywhere in this codebase, so even after
/// SCCharacterGamePointsPacket started sending the real value, whichever of the two paths the client's Hero UI
/// actually keys off could still have been left stale. `type`'s value doesn't matter (ignored natively); sent
/// as 0.
/// </remarks>
public class SCHeroSeasonOffPacket(int @type, int score) : GamePacket(SCOffsets.SCHeroSeasonOffPacket, 1)
{
    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(@type);
        stream.Write(score);
        return stream;
    }
}
