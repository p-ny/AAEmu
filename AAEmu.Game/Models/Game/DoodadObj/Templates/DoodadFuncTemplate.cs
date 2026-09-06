using AAEmu.Game.Models.Game.Units;
using NLog;

namespace AAEmu.Game.Models.Game.DoodadObj.Templates;

public abstract class DoodadFuncTemplate
{
    protected static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public uint Id { get; set; }
    public virtual bool CompletesFromClientPacket => false;

    /// <summary>
    /// When a func completes with next_phase=-1 ("stay on the current phase"), Doodad.CompleteFunc's
    /// default reading is "one-shot object, consume it" (despawns/deletes via Spawner.Despawn/Delete()) -
    /// correct for e.g. a lootable resource node, but wrong for a permanent multi-phase fixture (a
    /// completed monument/statue) whose post-completion interactions are meant to be repeatable and
    /// non-destructive. Override to false for func types that must survive a bare "stay in place" use.
    /// Defaults true - preserves every existing doodad's behavior unchanged.
    /// </summary>
    public virtual bool DespawnOnBareUse => true;
    public abstract void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0);
}
