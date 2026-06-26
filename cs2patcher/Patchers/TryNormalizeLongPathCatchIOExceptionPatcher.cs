// Patches Colossal.IO.dll — Fix 28: TryNormalizeLongPath catch IOException
//
// `LongPath.TryNormalizeLongPath` only catches `ArgumentException` and
// `PathTooLongException`. Under Wine, `NormalizeLongPath` throws
// `IOException("Success")` when `GetFullPathName` returns 0. This propagates
// out of `TryNormalizeLongPath`, causing `LongFile.Exists` to fail, which
// makes `DlcHelper.GetDlcAttributes` skip every DLC (the .ntl existence
// check throws instead of returning false).
//
// Fix: add `catch [IOException] { pop; leave.s <failure>; }` handler.

using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

namespace Cs2MacPatcher;

static class TryNormalizeLongPathCatchIOExceptionPatcher
{
    public static PatchSummary Patch(string managedDir, bool dryRun)
    {
        var dllPath = Path.Combine(managedDir, "Colossal.IO.dll");
        if (!File.Exists(dllPath))
            return PatchSummary.Skipped("Colossal.IO.dll not found");

        var module = ModuleDefinition.ReadModule(dllPath,
            new ReaderParameters { ReadingMode = ReadingMode.Immediate });

        var longPathType = module.Types.FirstOrDefault(t => t.FullName == "System.IO.LongPath");
        if (longPathType == null) { module.Dispose(); return PatchSummary.Skipped("LongPath not found"); }

        var tryNorm = longPathType.Methods.FirstOrDefault(m =>
            m.Name == "TryNormalizeLongPath" && m.Parameters.Count == 2 && m.HasBody);
        if (tryNorm == null) { module.Dispose(); return PatchSummary.Skipped("TryNormalizeLongPath not found"); }

        // Idempotency: already has IOException handler?
        if (tryNorm.Body.ExceptionHandlers.Any(e => e.CatchType != null &&
            e.CatchType.FullName == "System.IO.IOException"))
        {
            module.Dispose();
            return PatchSummary.AlreadyPatched("Colossal.IO.dll");
        }

        if (dryRun)
        {
            module.Dispose();
            return new PatchSummary("Colossal.IO.dll", 1, DryRun: true);
        }

        // Find existing catch handler chain. The last catch leads to the failure path.
        // The failure path after all catches: pop, result=null, return false.
        // Find the FIRST `stind.ref` before `ldc.i4.0; ret` at the end — that's the failure path.
        var instrs = tryNorm.Body.Instructions;
        Instruction? failureTarget = null;
        for (int i = 0; i < instrs.Count - 4; i++)
        {
            if (instrs[i].OpCode == OpCodes.Stind_Ref &&
                instrs[i + 1].OpCode == OpCodes.Ldc_I4_0 &&
                instrs[i + 2].OpCode == OpCodes.Ret)
            {
                failureTarget = instrs[i];
                break;
            }
        }
        if (failureTarget == null)
        {
            module.Dispose();
            return PatchSummary.Skipped("Failure path (stind.ref; ldc.i4.0; ret) not found in TryNormalizeLongPath");
        }

        // Add a new catch (IOException) that does `pop; leave.s failureTarget`.
        // Find the existing catch(PathTooLongException) handler — its end is where
        // the next catch goes.
        var existingHandler = tryNorm.Body.ExceptionHandlers.LastOrDefault();
        if (existingHandler == null)
        {
            module.Dispose();
            return PatchSummary.Skipped("No existing exception handlers in TryNormalizeLongPath");
        }

        var mscorlib = module.AssemblyReferences.First(r => r.Name == "mscorlib");
        var ioExceptionType = new TypeReference("System.IO", "IOException", module, mscorlib);

        var popInstr = Instruction.Create(OpCodes.Pop);
        var leaveInstr = Instruction.Create(OpCodes.Leave_S, failureTarget);

        // Insert the new instructions AFTER the last existing handler end and
        // BEFORE the failure path instructions start.
        var ilProcessor = tryNorm.Body.GetILProcessor();
        ilProcessor.InsertBefore(failureTarget, popInstr);
        ilProcessor.InsertAfter(popInstr, leaveInstr);

        tryNorm.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
        {
            TryStart = existingHandler.TryStart,
            TryEnd = popInstr,          // the catch covers the same try block
            HandlerStart = popInstr,
            HandlerEnd = failureTarget,
            CatchType = ioExceptionType
        });

        TimestampedBackup.BackupAndWrite(module, dllPath);
        return new PatchSummary("Colossal.IO.dll", 1, DryRun: false);
    }
}
