using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

/// <summary>
/// No client UI/skill trigger was found for National Monument placement (checked - no dedicated Lua panel, no
/// special_effects row referencing the monument doodad ids), so this is exposed as a Sovereign-only chat
/// command rather than a guessed trigger, same reasoning as NationRelation/NationRelationRespond. Places the
/// monument at the caller's current position. See NationManager.PlaceNationalMonument.
/// </summary>
public class PlaceMonument : ICommand
{
    public string[] CommandNames { get; set; } = ["placemonument"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp() => "";

    public string GetCommandHelpText() => "Sovereign-only: places your National Monument at your current position, inside your own Dominion.";

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        var pos = character.Transform.World.Position;
        NationManager.Instance.PlaceNationalMonument(character, pos.X, pos.Y, pos.Z);
        CommandManager.SendNormalText(this, messageOutput, "National Monument placement attempted at your current position.");
    }
}
