using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Network.Game;

namespace AAEmu.Game.Core.Packets.C2G;

/// <summary>
/// Wire format confirmed empirically 2026-08-16 by trimming the diagnostic hex dump to the real received length
/// (stream.Count, not stream.Buffer.Length - Buffer is a Roundup-sized oversized array, and dumping it directly
/// had been misreading trailing zero padding as real fields). A live single-candidate vote came in as an exact
/// 24-byte frame: 4-byte header (opcode) + 20-byte payload. 20 bytes is exactly count(int32) + count*charId(u64)
/// + voterId(u64) for count=1 - no second/"dead" count field exists. The previous version of this reader assumed
/// TWO leading count-shaped fields (from a Ghidra read of the packet object's constructor that turned out to be
/// misattributed) which overran the real 20-byte payload by 4 bytes into buffer padding, reading a phony
/// ids=[4294967296]/voterId=0 - that capture's realCount still came out as 1 by coincidence (equal to the true
/// count), which is what made the bug look outline-correct for so long. Single count field fixes it: this
/// capture's charId decoded to 1, the actual standing candidate's real character id.
/// </summary>
public class CSHeroVotingPacket() : GamePacket(CSOffsets.CSHeroVotingPacket, 1)
{
    public List<ulong> CandidateCharacterIds { get; private set; } = [];
    public ulong VoterCharacterId { get; private set; }

    public override void Read(PacketStream stream)
    {
        var count = stream.ReadInt32();
        CandidateCharacterIds = new List<ulong>(Math.Max(count, 0));
        for (var i = 0; i < count; i++)
            CandidateCharacterIds.Add(stream.ReadUInt64());
        VoterCharacterId = stream.ReadUInt64();

        Logger.Debug("CSHeroVotingPacket: count={0}, ids=[{1}], voterId={2}",
            count, string.Join(",", CandidateCharacterIds), VoterCharacterId);

        // Whole ballot in one call, not one Vote() per id - see HeroManager.Vote's doc comment for why
        // (a per-id loop combined with hero_votes' now-fixed primary key would reject every pick after the
        // first with "already voted").
        HeroManager.Instance.Vote(Connection, CandidateCharacterIds);
    }
}
