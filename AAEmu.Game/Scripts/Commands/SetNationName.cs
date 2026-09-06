using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

/// <summary>
/// GM/testing tool, added 2026-08-21. No live client trigger delivers a player-chosen nation name at founding
/// time (DeclareIndependence fires purely off quest completion, zero text payload - see NationManager's doc
/// comments) - this lets one be set/persisted server-side. Confirmed separately that SCDominionDataPacket/
/// DominionData has no wire field to display a custom name back to the client with at all (fully RE'd, fixed-
/// width struct, no string field) - so this name is not currently visible in-game anywhere; it exists for
/// forward compatibility with whatever surfaces this eventually (mail, a future custom packet, etc.).
/// </summary>
public class SetNationName : ICommand
{
    public string[] CommandNames { get; set; } = ["setnationname"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp() => "<zoneId> <name...>";

    public string GetCommandHelpText() =>
        "Sets a founded nation's name (persisted, but not currently displayable in-game - no wire field exists for it).";

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length < 2 || !ushort.TryParse(args[0], out var zoneId))
        {
            CommandManager.SendNormalText(this, messageOutput, "Usage: /setnationname <zoneId> <name...>");
            return;
        }

        var name = string.Join(' ', args[1..]).Trim();
        if (name.Length == 0)
        {
            CommandManager.SendNormalText(this, messageOutput, "Name cannot be empty.");
            return;
        }

        if (!NationManager.Instance.SetNationName(zoneId, name))
        {
            CommandManager.SendNormalText(this, messageOutput, $"No nation exists at zone {zoneId}.");
            return;
        }

        CommandManager.SendNormalText(this, messageOutput,
            $"Zone {zoneId}'s nation is now named '{name}' (persisted; not yet displayable client-side).");
    }
}
