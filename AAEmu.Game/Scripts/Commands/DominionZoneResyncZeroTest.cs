using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

/// <summary>
/// DIAGNOSTIC ONLY, 2026-08-20 - same as /dominionresync but sends an all-zero DominionData (see
/// DominionManager.ResyncZoneWithZeroedTestData). Isolates whether a WZDominionData crash is data-dependent
/// (a real non-zero value getting misread as a huge count) vs. purely structural. Remove once the remaining
/// WZDominionData field-width questions are resolved - see aaemu-zone-wire-format-danger memory.
/// </summary>
public class DominionZoneResyncZeroTest : ICommand
{
    public string[] CommandNames { get; set; } = ["dominionresynczero"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp() => "<zoneGroupId> [paddingBytes]";

    public string GetCommandHelpText() => "DIAGNOSTIC: re-sends WZDominionData for a claimed zone group with every field zeroed except ZoneId/ExpeditionId, plus optional trailing zero-byte padding to bisect the real required packet length.";

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length is < 1 or > 2 || !ushort.TryParse(args[0], out var zoneGroupId))
        {
            CommandManager.SendNormalText(this, messageOutput,
                $"Usage: {CommandManager.CommandPrefix}{CommandNames[0]} <zoneGroupId> [paddingBytes]");
            return;
        }

        var paddingBytes = 0;
        if (args.Length == 2 && !int.TryParse(args[1], out paddingBytes))
        {
            CommandManager.SendNormalText(this, messageOutput, "paddingBytes must be an integer.");
            return;
        }

        if (DominionManager.Instance.ResyncZoneWithZeroedTestData(zoneGroupId, paddingBytes))
            CommandManager.SendNormalText(this, messageOutput, $"Sent zeroed test WZDominionData for zone group {zoneGroupId} with {paddingBytes} padding bytes.");
        else
            CommandManager.SendErrorText(this, messageOutput, $"Zone group {zoneGroupId} is not claimed, or its House/zone could not be resolved.");
    }
}
