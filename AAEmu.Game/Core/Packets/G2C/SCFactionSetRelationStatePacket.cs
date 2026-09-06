using System;

using AAEmu.Commons.Network;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Models.Game.Faction;

namespace AAEmu.Game.Core.Packets.G2C;

/// <summary>
/// Single faction-relation change. Client handler FUN_3933ac70 -> FUN_395bee00 adds one entry to the
/// client's relation table and fires UI event 0x1c8. Wire format corrected 2026-09-02 from the real
/// client Unpack FUN_39a9a7f0 (was a 5-field guess; the real struct is the same 10 fields as
/// SCFactionRelationListPacket's per-entry).
/// </summary>
public class SCFactionSetRelationStatePacket : GamePacket
{
    private readonly uint _id;
    private readonly uint _id2;
    private readonly RelationState _state;
    private readonly RelationState _nextState;
    private readonly DateTime _updateTime;
    private readonly DateTime _changeTime;
    private readonly string _updaterName;
    private readonly string _confirmerName;

    public SCFactionSetRelationStatePacket(uint id, uint id2, RelationState state,
        RelationState nextState = RelationState.Neutral, DateTime updateTime = default,
        DateTime changeTime = default, string updaterName = "", string confirmerName = "")
        : base(SCOffsets.SCFactionSetRelationStatePacket, 1)
    {
        _id = id;
        _id2 = id2;
        _state = state;
        _nextState = nextState;
        _updateTime = updateTime == default ? DateTime.UtcNow : updateTime;
        _changeTime = changeTime == default ? DateTime.UtcNow : changeTime;
        _updaterName = updaterName ?? "";
        _confirmerName = confirmerName ?? "";
    }

    public override PacketStream Write(PacketStream stream)
    {
        stream.Write(_id);              // faction/guild 1
        stream.Write(_id2);             // faction/guild 2
        stream.Write((byte)_state);     // "state"
        stream.Write((byte)_nextState); // "nextState" (pending)
        stream.Write(_updateTime);      // "updateTime"
        stream.Write(_changeTime);      // "changeTime"
        stream.Write(0L);               // "updaterId"
        stream.Write(_updaterName);     // "updaterName"
        stream.Write(0L);               // "confirmerId"
        stream.Write(_confirmerName);   // "confirmerName"
        return stream;
    }
}
