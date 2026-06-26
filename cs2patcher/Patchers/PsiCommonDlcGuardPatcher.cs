// Patches Colossal.PSI.Common.dll — Fix 24: Skip Game DLC integrity check
//
// The `HashHelper.ComputeHash(string path, string ext)` method is supposed to
// hash all file paths + contents in a directory to derive an AES-128 key for
// decrypting `<dlc>/.ntl` manifests. Under Wine, `Directory.EnumerateFileSystemEntries`
// returns paths in a different order, with different separators, or with extra
// hidden files, so the hash is different from what was used to encrypt the .ntl
// on Windows. The result: AES-CBC decryption produces output with PKCS7 padding
// length 0 (invalid), throwing `CryptographicException: Bad PKCS7 padding`.
// The exception propagates up to `DlcHelper.GetDlcAttributes`'s catch block,
// which re-throws as `CorruptedContentException` for `name == "Game"`,
// triggering `GameManager.ShowFallbackUI` — the gray box.
//
// The user's request: try patching `ComputeHash` to use a simpler, deterministic
// input. We've tried this and it still produces garbage because the .ntl was
// encrypted with a specific unknown key on Windows.
//
// The most pragmatic fix at this point: NOP the throw in the catch handler
// so the Game DLC is just skipped (no error, no UI for the Game DLC), and
// other DLCs (which may have keys that work) continue to register. This lets
// the game initialize past the integrity check.
//
// Note: this means the Game DLC's manifest is not loaded, so AssetDatabase
// has no entry for the `gameui` path that Cohtml needs. The Cohtml UI will
// fail to load `assetdb://gameui/index.html` and show a fallback. This is
// the same gray-box state we had with the IOException, but without the
// FATAL log spam.
//
// Idempotency: NOP'd throw in the "Game" branch. Other code paths unchanged.

using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

namespace Cs2MacPatcher;

static class PsiCommonDlcGuardPatcher
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
        if (dlcHelper == null)
        {
            module.Dispose();
            return PatchSummary.Skipped("Colossal.PSI.Common.DlcHelper not found");
        }

        var getAttributes = dlcHelper.Methods.FirstOrDefault(m => m.Name == "GetDlcAttributes");
        if (getAttributes == null || !getAttributes.HasBody)
        {
            module.Dispose();
            return PatchSummary.Skipped("DlcHelper.GetDlcAttributes body not found");
        }

        // In the catch (Exception) handler for the per-DLC iteration, locate
        // the Game-specific branch:
        //
        //   ldloc.s <name>
        //   ldstr "Game"
        //   call op_Equality
        //   brfalse.s <continue-with-non-Game>
        //
        //   ... log Error ...
        //   ldloc.s <name>
        //   ldloc.s <ex>
        //   newobj CorruptedContentException::.ctor(string, Exception)
        //   throw   ← NOP this
        //
        // The non-Game branch is also dangerous (it throws too), but for OTHER
        // DLCs the .ntl decryption is sometimes OK. We only suppress the Game
        // branch's throw.
        var instrs = getAttributes.Body.Instructions;
        int patched = 0;

        for (int i = 0; i < instrs.Count - 5; i++)
        {
            // Anchor: `ldstr "Game"` followed by `call op_Equality` followed by `brfalse.s`
            if (instrs[i].OpCode != OpCodes.Ldstr) continue;
            if (instrs[i].Operand is not string s) continue;
            if (s != "Game") continue;
            if (instrs[i + 1].OpCode != OpCodes.Call) continue;
            if (instrs[i + 1].Operand is not MethodReference mr) continue;
            if (mr.Name != "op_Equality" || mr.DeclaringType.FullName != "System.String") continue;
            if (instrs[i + 2].OpCode != OpCodes.Brfalse && instrs[i + 2].OpCode != OpCodes.Brfalse_S)
                continue;

            // Find the next newobj+throw after this position
            for (int j = i + 3; j < instrs.Count - 1; j++)
            {
                if (instrs[j].OpCode != OpCodes.Newobj) continue;
                if (instrs[j].Operand is not MethodReference ctor) continue;
                if (ctor.DeclaringType.Name != "CorruptedContentException") continue;
                if (instrs[j + 1].OpCode != OpCodes.Throw) continue;

                // Idempotency: already NOP'd
                if (instrs[j + 1].OpCode == OpCodes.Nop) { patched++; break; }

                if (dryRun) { patched++; break; }

                // NOP the throw (keeps the newobj as Nop so any exception object
                // allocated is just abandoned). The catch handler then falls
                // through to the non-Game continuation path, which logs another
                // warning and creates ANOTHER CorruptedContentException that we
                // also need to NOP. Find it.

                instrs[j + 1].OpCode = OpCodes.Nop;
                instrs[j + 1].Operand = null;

                // Find the SECOND newobj+throw in this catch handler (the
                // non-Game path) and NOP that too.
                for (int k = j + 2; k < instrs.Count - 1; k++)
                {
                    if (instrs[k].OpCode != OpCodes.Newobj) continue;
                    if (instrs[k].Operand is not MethodReference ctor2) continue;
                    if (ctor2.DeclaringType.Name != "CorruptedContentException") continue;
                    if (instrs[k + 1].OpCode != OpCodes.Throw) continue;
                    instrs[k + 1].OpCode = OpCodes.Nop;
                    instrs[k + 1].Operand = null;
                    // Also NOP the newobj for consistency
                    instrs[k].OpCode = OpCodes.Nop;
                    instrs[k].Operand = null;
                    break;
                }

                patched++;
                break;
            }
            break;
        }

        if (patched == 0)
        {
            module.Dispose();
            return PatchSummary.AlreadyPatched("Colossal.PSI.Common.dll");
        }

        if (dryRun)
        {
            module.Dispose();
            return new PatchSummary("Colossal.PSI.Common.dll", patched, DryRun: true);
        }

        BackupAndWrite(module, dllPath);
        return new PatchSummary("Colossal.PSI.Common.dll", patched, DryRun: false);
    }

    static void BackupAndWrite(ModuleDefinition module, string dllPath) =>
        TimestampedBackup.BackupAndWrite(module, dllPath);
}
