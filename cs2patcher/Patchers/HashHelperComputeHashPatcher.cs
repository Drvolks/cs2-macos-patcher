// Patches Colossal.PSI.Common.dll — Fix 25: HashHelper.ComputeHash simple key
//
// The existing `HashHelper.ComputeHash(string path, string ignoreExtension)`
// enumerates every file in the directory, filters by extension, and hashes
// each file's PATH (substring(1)) + CONTENTS through xxHash3 to produce the
// AES-128 key for decrypting `<DLC>/.ntl`. Under Wine the enumeration can
// return paths in a different order, with different separators, or with extra
// files, so the xxHash3 output differs from the key used to encrypt the .ntl
// on Windows. The result: AES-CBC decryption produces garbage → PKCS7 padding
// error → `CryptographicException` → caught-and-re-throw by GetDlcAttributes →
// `[FATAL] Data is corrupted in Game database`.
//
// Fix: replace the entire method body with a deterministic single-pass xxHash3
// over just `path + "." + ignoreExtension` (e.g. "Game.ntl"). The key is now
// stable across Wine and Windows — as long as the .ntl file was re-encrypted
// with the same key by the companion `ntl-reencrypt.py` script (which the
// patcher runs automatically).
//
// The new key is `xxHash3("Game.ntl")` for the Game DLC.
//
// NOTE: this change breaks decryption of ORIGINAL .ntl files (the files
// shipped/updated by Steam). The `ntl-reencrypt.py` script in the patcher's
// top-level directory must run once after each game install or update to
// re-encrypt the .ntl files with the new keys. Without re-encryption,
// the .ntl files would still decrypt to garbage.
//
// Idempotent: skip if the method body already starts with `stloc.s 1`
// (the saved-key local used by the new body).

using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

namespace Cs2MacPatcher;

static class HashHelperComputeHashPatcher
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

        // ── Part A: Patch HashHelper.ComputeHash ────────────────────────
        var hashHelper = module.Types.FirstOrDefault(t => t.FullName == "Colossal.PSI.Common.HashHelper");
        if (hashHelper == null)
        {
            module.Dispose();
            return PatchSummary.Skipped("HashHelper not found");
        }

        var computeHash = hashHelper.Methods.FirstOrDefault(m =>
            m.Name == "ComputeHash" &&
            m.Parameters.Count == 2 &&
            m.Parameters[0].ParameterType.MetadataType == MetadataType.String &&
            m.Parameters[1].ParameterType.MetadataType == MetadataType.String &&
            m.HasBody);
        if (computeHash == null)
        {
            module.Dispose();
            return PatchSummary.Skipped("HashHelper.ComputeHash(string,string) not found");
        }

        int applied = 0;
        applied += PatchComputeHash(module, hashHelper, computeHash, dryRun);

        if (applied == 0)
        {
            module.Dispose();
            return PatchSummary.AlreadyPatched("Colossal.PSI.Common.dll");
        }

        if (dryRun)
        {
            module.Dispose();
            return new PatchSummary("Colossal.PSI.Common.dll", applied, DryRun: true);
        }

        TimestampedBackup.BackupAndWrite(module, dllPath);
        return new PatchSummary("Colossal.PSI.Common.dll", applied, DryRun: false);
    }

    static int PatchComputeHash(ModuleDefinition module, TypeDefinition hashHelper, MethodDefinition computeHash, bool dryRun)
    {
        // Idempotency: if first instruction is `ldarg.0` (our new body's start), skip.
        if (computeHash.Body.Instructions.Count > 2 &&
            computeHash.Body.Instructions[0].OpCode == OpCodes.Ldarg_0 &&
            computeHash.Body.Instructions[1].OpCode == OpCodes.Ldstr)
            return 0;

        // Extract the method references we need from the EXISTING body.
        // The old ComputeHash already calls StreamState::.ctor, HashHelper::Update,
        // StreamState::DigestHash128, and Hash128::.ctor(uint4). We capture the
        // MethodReference objects which are already properly imported into the
        // module by the DLL compiler.
        MethodReference? stateCtor = null, digest = null;
        foreach (var instr in computeHash.Body.Instructions)
        {
            if (instr.OpCode != OpCodes.Call && instr.OpCode != OpCodes.Newobj) continue;
            if (instr.Operand is not MethodReference mr) continue;

            if (mr.Name == ".ctor" && mr.DeclaringType.Name.StartsWith("StreamingState"))
                stateCtor = mr;
            else if (mr.Name == "DigestHash128")
                digest = mr;
        }

        // HashHelper.Update is a static method on HashHelper itself, so we can
        // resolve it directly from the type (the old body's MethodReference
        // for Update might have a stale token after we clear the body,
        // causing Mono to report IL_0020: call 0x0600007c — an unresolvable
        // token).
        var hashUpdate = hashHelper.Methods.FirstOrDefault(m =>
            m.Name == "Update" && m.Parameters.Count == 2 && m.IsStatic);
        if (hashUpdate == null) return 0;
        var hashUpdateRef = module.ImportReference(hashUpdate);

        // Hash128::.ctor(uint4) — find from the old body
        MethodReference? hash128Ctor = null;
        foreach (var instr in computeHash.Body.Instructions)
        {
            if (instr.OpCode != OpCodes.Newobj) continue;
            if (instr.Operand is not MethodReference mr) continue;
            if (mr.Name == ".ctor" && mr.DeclaringType.Name == "Hash128")
                hash128Ctor = mr;
        }

        if (stateCtor == null || digest == null || hash128Ctor == null)
            return 0; // pattern not found — maybe a different DLL version

        if (dryRun) return 1;

        // Build the new body. Locals: [0] = StreamingState, [1] = combinedKeyString
        var stateType = stateCtor.DeclaringType;
        var stringType = module.TypeSystem.String;

        computeHash.Body.Instructions.Clear();
        computeHash.Body.ExceptionHandlers.Clear();
        computeHash.Body.Variables.Clear();
        computeHash.Body.InitLocals = true;
        computeHash.Body.MaxStackSize = 8;

        var v_state = new VariableDefinition(stateType);
        var v_keyStr = new VariableDefinition(stringType);
        computeHash.Body.Variables.Add(v_state);
        computeHash.Body.Variables.Add(v_keyStr);

        var il = computeHash.Body.GetILProcessor();

        il.Append(il.Create(OpCodes.Ldarg_0));                     // path
        il.Append(il.Create(OpCodes.Ldarg_1));                     // ignoreExtension
        il.Append(il.Create(OpCodes.Call, GetConcat2(module)));    // Concat(path, ext)
        il.Append(il.Create(OpCodes.Stloc, v_keyStr));             // → keyStr

        // 2. StreamingState(false, 0L)
        il.Append(il.Create(OpCodes.Ldloca_S, v_state));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(OpCodes.Conv_I8));
        il.Append(il.Create(OpCodes.Call, stateCtor));

        // 3. HashHelper.Update(ref state, keyStr)
        // NOTE: Update takes StreamingState by VALUE (not by ref), so we
        // push the struct value with `ldloc` (not `ldloca.s`). The original
        // body does the same — see `ldloc.1` before the call.
        il.Append(il.Create(OpCodes.Ldloc, v_state));
        il.Append(il.Create(OpCodes.Ldloc, v_keyStr));
        il.Append(il.Create(OpCodes.Call, hashUpdateRef));

        // 4. state.DigestHash128()
        il.Append(il.Create(OpCodes.Ldloca_S, v_state));
        il.Append(il.Create(OpCodes.Call, digest));

        // 5. new Hash128(uint4)
        il.Append(il.Create(OpCodes.Newobj, hash128Ctor));
        il.Append(il.Create(OpCodes.Ret));

        return 1;
    }

    static MethodReference GetConcat2(ModuleDefinition module)
    {
        var stringType = module.TypeSystem.String;
        var concat = new MethodReference("Concat", stringType, module.TypeSystem.String);
        concat.DeclaringType = stringType;
        concat.HasThis = false;
        concat.Parameters.Add(new ParameterDefinition(stringType));
        concat.Parameters.Add(new ParameterDefinition(stringType));
        return module.ImportReference(concat);
    }

    static MethodReference GetConcat(ModuleDefinition module)
    {
        var stringType = module.TypeSystem.String;
        var concat = new MethodReference("Concat", stringType, module.TypeSystem.String);
        concat.DeclaringType = stringType;
        concat.HasThis = false;
        concat.Parameters.Add(new ParameterDefinition(stringType));
        concat.Parameters.Add(new ParameterDefinition(stringType));
        concat.Parameters.Add(new ParameterDefinition(stringType));
        return module.ImportReference(concat);
    }
}
