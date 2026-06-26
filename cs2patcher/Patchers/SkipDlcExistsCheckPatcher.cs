// Patches Colossal.PSI.Common.dll — Fix 29: skip LongFile.Exists in GetDlcAttributes
//
// NOP the `call LongFile::Exists` and replace `brfalse` with `pop` so the
// DLC loop always proceeds to read/decrypt the .ntl regardless of whether
// Wine's `LongPath.NormalizeLongPath` succeeds.

using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

namespace Cs2MacPatcher;

static class SkipDlcExistsCheckPatcher
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

        var instrs = getAttributes.Body.Instructions;
        for (int i = 0; i < instrs.Count - 1; i++)
        {
            if (instrs[i].OpCode != OpCodes.Call) continue;
            if (instrs[i].Operand is not MethodReference mr) continue;
            if (mr.Name != "Exists" || mr.DeclaringType.FullName != "System.IO.LongFile") continue;

            var branch = instrs[i + 1];
            if (branch.OpCode != OpCodes.Brfalse && branch.OpCode != OpCodes.Brfalse_S) continue;
            if (instrs[i].OpCode == OpCodes.Nop) { module.Dispose(); return PatchSummary.AlreadyPatched("Colossal.PSI.Common.dll"); }

            if (dryRun) { module.Dispose(); return new PatchSummary("Colossal.PSI.Common.dll", 1, DryRun: true); }

            instrs[i].OpCode = OpCodes.Nop;
            instrs[i].Operand = null;
            branch.OpCode = OpCodes.Pop;
            branch.Operand = null;

            TimestampedBackup.BackupAndWrite(module, dllPath);
            return new PatchSummary("Colossal.PSI.Common.dll", 1, DryRun: false);
        }

        module.Dispose();
        return PatchSummary.Skipped("LongFile.Exists pattern not found in GetDlcAttributes");
    }
}
