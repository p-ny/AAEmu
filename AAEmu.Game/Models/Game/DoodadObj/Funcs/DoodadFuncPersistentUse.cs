namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

/// <summary>
/// Identical to <see cref="DoodadFuncUse"/> (same skill-cast pipeline, no behavior override) except it
/// survives a bare "stay on the current phase" completion instead of being despawned/deleted - see
/// <see cref="DoodadFuncTemplate.DespawnOnBareUse"/>. Used for the Hero capital-city Statue's
/// post-completion interactions (the "완공"/Complete func groups' DoodadFuncUse rows all have
/// next_phase=-1, which Doodad.CompleteFunc otherwise reads as "one-shot object, consume it" - real,
/// reproducible bug confirmed live 2026-08-31: using one of these skills freed the statue's ObjId
/// immediately to unrelated nearby doodads).
/// </summary>
public class DoodadFuncPersistentUse : DoodadFuncUse
{
    public override bool DespawnOnBareUse => false;
}
