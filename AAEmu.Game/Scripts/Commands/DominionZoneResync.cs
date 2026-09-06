using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

/// <summary>
/// GM catch-up tool, 2026-08-19: pushes WZDominionData to Zone for an already-claimed zone group without a
/// full unclaim/reclaim. Needed because Zone previously had zero awareness that any territory was ever claimed
/// (see WZDominionDataPacket's doc comment) - claims declared before this fix (or after Zone itself restarts
/// independently of World) won't resync on their own.
/// </summary>
public class DominionZoneResync : ICommand
{
    public string[] CommandNames { get; set; } = ["dominionresync"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp()
    {
        return "<zoneGroupId>";
    }

    public string GetCommandHelpText()
    {
        return "Re-sends the claimed Dominion's territory data to Zone (dominions.zone_id, i.e. the zone GROUP id, not a raw zone id).";
    }

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length != 1 || !ushort.TryParse(args[0], out var zoneGroupId))
        {
            CommandManager.SendNormalText(this, messageOutput,
                $"Usage: {CommandManager.CommandPrefix}{CommandNames[0]} <zoneGroupId>");
            return;
        }

        if (DominionManager.Instance.ResyncZone(zoneGroupId))
            CommandManager.SendNormalText(this, messageOutput, $"Resynced Dominion zone group {zoneGroupId} to Zone.");
        else
            CommandManager.SendErrorText(this, messageOutput, $"Zone group {zoneGroupId} is not claimed, or its House/zone could not be resolved.");
    }
}
