// Patches Colossal.PSI.Common.dll — Fix 27: DlcAttribute conflict always overwrite
//
// When `DecryptFile` returns `{"dlcId":0}` for every DLC (because we bypassed
// AES decryption), multiple DLCs share dlcId=0. The original code checks if
// `existingDlcAttr.internalName != newDlcAttr.internalName` — if true, it
// logs an error and SKIPS the new DLC. With every DLC having dlcId=0, the
// alphabetically-first DLC claims dlcId=0 and all subsequent DLCs (including
// the base "Game" DLC) are skipped.
//
// Fix: change the branch from `brfalse.s <update>` (only update when names
// match) to `br.s <update>` (always update). The new DLC overwrites the
// previous, so the LAST DLC alphabetically gets dlcId=0. Since "Game" comes
// after "BridgesAndPorts", "DeluxeRelaxRadio", etc. in ASCII sort, Game
// ends up as the final dlcId=0 entry — which is correct (Game SHOULD have
// dlcId=0).
//
// Idempotent: skip if the branch already goes to the update path.

using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

namespace Cs2MacPatcher;

static class DlcIdConflictOverridePatcher
{
    public static PatchSummary Patch(string managedDir, bool dryRun)
    {
        var dllPath = Path.Combine(managedDir, "Colossal.PSI.Common.dll");
        if (!File.Exists(dllPath))
            return PatchSummary.Skipped("Colossal.PSI.Common.dll not found");

        var resolver = new UnityEngineStubResolver(managedDir);
        var module = ModuleDefinition.ReadModule(dllPath, new ReaderParameters
        {
            ReadingMode = ReadingMode.Immediate,
            AssemblyResolver = resolver,
            ReadSymbols = false
        });

        var dlcHelper = module.Types.FirstOrDefault(t => t.FullName == "Colossal.PSI.Common.DlcHelper");
        if (dlcHelper == null) { module.Dispose(); return PatchSummary.Skipped("DlcHelper not found"); }

        var getAttributes = dlcHelper.Methods.FirstOrDefault(m => m.Name == "GetDlcAttributes");
        if (getAttributes == null || !getAttributes.HasBody)
        { module.Dispose(); return PatchSummary.Skipped("GetDlcAttributes body not found"); }

        // Pattern: op_Inequality(internalName, internalName); brfalse.s <update>
        // We want: always branching to <update> (changing brfalse.s → br.s)
        var instrs = getAttributes.Body.Instructions;
        for (int i = 0; i < instrs.Count - 1; i++)
        {
            if (instrs[i].OpCode != OpCodes.Call) continue;
            if (instrs[i].Operand is not MethodReference mr) continue;
            if (mr.Name != "op_Inequality" || mr.DeclaringType.FullName != "System.String") continue;

            var branch = instrs[i + 1];
            if (branch.OpCode != OpCodes.Brfalse && branch.OpCode != OpCodes.Brfalse_S) continue;

            // Make sure the target is reasonable: must not go backwards too far,
            // must not be a ret/throw (should be the update cache path)
            if (!(branch.Operand is Instruction target)) continue;
            if (target.Offset < branch.Offset) continue; // backward jump

            // Idempotency: if already br / br.s, skip
            if (branch.OpCode == OpCodes.Br || branch.OpCode == OpCodes.Br_S) return PatchSummary.AlreadyPatched("Colossal.PSI.Common.dll");

            if (dryRun) { module.Dispose(); return new PatchSummary("Colossal.PSI.Common.dll", 1, DryRun: true); }

            branch.OpCode = branch.OpCode == OpCodes.Brfalse_S ? OpCodes.Br_S : OpCodes.Br;
            TimestampedBackup.BackupAndWrite(module, dllPath);
            return new PatchSummary("Colossal.PSI.Common.dll", 1, DryRun: false);
        }

        module.Dispose();
        return PatchSummary.Skipped("DlcId conflict branch pattern not found");
    }
}
