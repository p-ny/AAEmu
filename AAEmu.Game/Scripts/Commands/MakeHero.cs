using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

public class MakeHero : ICommand
{
    public string[] CommandNames { get; set; } = ["makehero"];

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp()
    {
        return "";
    }

    public string GetCommandHelpText()
    {
        return "Toggles a testing shortcut that marks you as your faction's currently elected Hero " +
               "(UnitReqsKindType.Hero), without waiting for a real election cycle. Not persisted through " +
               "the real hero_schedules machinery - only one test-Hero per faction at a time.";
    }

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        var nowHero = HeroManager.Instance.ToggleTestHero(character);
        // Push a fresh candidate/ranking snapshot immediately - SendHeroInfo otherwise only fires at login,
        // so without this the client would show stale data (or none) until the next relog.
        HeroManager.Instance.SendHeroInfo(character);
        CommandManager.SendNormalText(this, messageOutput,
            nowHero
                ? "You are now marked as your faction's Hero (testing only)."
                : "Test-Hero status cleared.");
    }
}
