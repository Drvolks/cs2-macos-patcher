// Patches Colossal.PSI.Common.dll — Fix 26: DecryptFile handle short ciphertext
//
// If Wine's `LongFile.ReadAllBytes` reads fewer bytes than the file actually
// contains (e.g. only the 16-byte IV from a 32-byte .ntl file), the ciphertext
// length is 0; Mono's `CryptoStream.FlushFinalBlock` then throws
// `CryptographicException: Bad PKCS7 padding. Invalid length 0`.
//
// Fix: if `encryptedData.Length <= 16` (only IV, no ciphertext), return ""
// immediately (same as failing gracefully). Otherwise proceed with normal
// decryption.
//
// Idempotent: skip if the method body already starts with our sentinel
// `ldarg.1; ldlen; ldc.i4.s 16; bgt.s`.

using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

namespace Cs2MacPatcher;

static class DecryptFileShortCiphertextPatcher
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

        var hashHelper = module.Types.FirstOrDefault(t => t.FullName == "Colossal.PSI.Common.HashHelper");
        if (hashHelper == null)
        {
            module.Dispose();
            return PatchSummary.Skipped("HashHelper not found");
        }

        var decryptFile = hashHelper.Methods.FirstOrDefault(m =>
            m.Name == "DecryptFile" && m.Parameters.Count == 2 && m.HasBody);
        if (decryptFile == null)
        {
            module.Dispose();
            return PatchSummary.Skipped("HashHelper.DecryptFile not found");
        }

        // Idempotency: check operand string. If already "{\"dlcId\":0}", skip.
        if (decryptFile.Body.Instructions.Count > 0 &&
            decryptFile.Body.Instructions[0].OpCode == OpCodes.Ldstr &&
            decryptFile.Body.Instructions[0].Operand is string s &&
            s == "{\"dlcId\": 0}")
        {
            module.Dispose();
            return PatchSummary.AlreadyPatched("Colossal.PSI.Common.dll");
        }

        if (dryRun)
        {
            module.Dispose();
            return new PatchSummary("Colossal.PSI.Common.dll", 1, DryRun: true);
        }

        // Replace the entire method body with: return "{\"dlcId\":0}";
        // This bypasses the AES decryption entirely. All DLCs get dlcId=0.
        decryptFile.Body.Instructions.Clear();
        decryptFile.Body.ExceptionHandlers.Clear();
        decryptFile.Body.Variables.Clear();
        decryptFile.Body.InitLocals = false;
        decryptFile.Body.MaxStackSize = 1;

        var il = decryptFile.Body.GetILProcessor();
        // Provide a minimal valid DLC manifest. The JSON must be parseable
        // by Colossal.Json.Variant (Unity's JSON parser). Keep it minimal.
        il.Append(il.Create(OpCodes.Ldstr, "{\"dlcId\": 0}"));
        il.Append(il.Create(OpCodes.Ret));

        TimestampedBackup.BackupAndWrite(module, dllPath);
        return new PatchSummary("Colossal.PSI.Common.dll", 1, DryRun: false);
    }
}
