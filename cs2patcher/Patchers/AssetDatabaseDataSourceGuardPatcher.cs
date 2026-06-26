// Patches Colossal.IO.AssetDatabase.dll — Fix 18: null m_DataSource guard (v1.6.0f1+)
//
// Symptom: After the existing patches are applied, the game launches but shows a
// black window. Player.log shows:
//
//   [SceneFlow] [ERROR] A platform service integration failed to initialize
//   ...
//   [SceneFlow] [FATAL] Object reference not set to an instance of an object
//     at Colossal.IO.AssetDatabase.AssetDatabase`1[T].PopulateFromDataSource
//        (bool priorityData, CancellationToken ct, TaskProgress progress) [0x000f7]
//     at Colossal.IO.AssetDatabase.AssetDatabase+<>c__DisplayClass115_0
//        .<CacheAssets>g__UpdateDatabase|0 (...)
//     at Game.SceneFlow.GameManager.Initialize ()
//
// Root cause: Under CrossOver, when a platform service integration fails to
// initialize (e.g. Steam, Paradox), the AssetDatabase<T>.m_DataSource field ends
// up null because the descriptor's get_dataSourceProvider() returns null. The
// async state machine in <PopulateFromDataSource>d__N::MoveNext then attempts
// callvirt IDataSourceProvider::PopulateDataSource on a null receiver, which
// throws NullReferenceException. The state machine's outer catch [Exception]
// handler re-throws via AsyncTaskMethodBuilder.SetException, which propagates
// up through GameManager.Initialize as the FATAL above.
//
// Without this guard, the FATAL leaves the engine running with a half-
// initialized GameManager (subsequent Update/OnGUI calls throw NRE
// repeatedly) and the user sees a black window with no UI.
//
// Fix: insert a null check on m_DataSource at the very top of the state
// machine's MoveNext. If null, complete the task cleanly with no work done
// (state = -2, builder.SetResult(), return). If non-null, fall through to
// the original code unchanged.
//
// This is a v1.6.0f1+ regression — the patcher's other fixes (LongDirectory,
// .priority file) are necessary for the game to reach this point at all, so
// this guard is layered on top of them.
//
// ─────────────────────────────────────────────────────────────────────────────
// Fix 19: SteamCloudDataSource returns null Task when Steam isn't initialized
// ─────────────────────────────────────────────────────────────────────────────
//
// Symptom: After Fix 18 above, FATAL moves to a different code path:
//
//   [SceneFlow] [FATAL] Object reference not set to an instance of an object
//     at Colossal.IO.AssetDatabase.AssetDatabase`1[T].PopulateFromDataSource
//        (bool priorityData, CancellationToken ct, TaskProgress progress) [0x00113]
//        where T = Colossal.IO.AssetDatabase.SteamCloud
//
// Root cause: SteamCloudDataSource.PopulateDataSource explicitly returns a
// null Task when Steam isn't running:
//
//   if (!m_PlatformManager.isInitialized) {
//       return null;   // IL: ldnull; stloc.1; leave IL_0194
//   }
//
// This is by design on Windows (caller checks for null) but the generic
// AssetDatabase<SteamCloud>.PopulateFromDataSource in v1.6.0f1+ no longer
// checks for null — it calls GetAwaiter() on the returned Task unconditionally,
// which throws NullReferenceException. Same downstream FATAL as Fix 18.
//
// Fix: replace the `ldnull` with `Task.FromResult(new List<...>())` so the
// SteamCloud data source reports an empty result instead of null. The caller
// gets a completed Task with an empty collection and the await succeeds
// normally — AssetDatabase<SteamCloud>.PopulateFromDataSource completes with
// no assets registered for Steam Cloud, which is the correct behaviour when
// Steam isn't available.
//
// This is a v1.6.0f1+ regression — in v1.5.8f1 the Steam Cloud database
// instance isn't instantiated at all when Steam isn't running (the descriptor
// get_dataSourceProvider returns null for the right reasons upstream, Fix 18
// catches that case). v1.6.0f1 starts always creating the descriptor; the
// SteamCloudDataSource then bails on `!isInitialized` by returning null, which
// the rest of the pipeline doesn't handle.

using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

namespace Cs2MacPatcher;

static class AssetDatabaseDataSourceGuardPatcher
{
    public static PatchSummary Patch(string managedDir, bool dryRun)
    {
        var dllPath = Path.Combine(managedDir, "Colossal.IO.AssetDatabase.dll");
        if (!File.Exists(dllPath))
            return PatchSummary.Skipped("Colossal.IO.AssetDatabase.dll not found");

        var resolver = new UnityEngineStubResolver(managedDir);
        var module = ModuleDefinition.ReadModule(dllPath, new ReaderParameters
        {
            ReadingMode = ReadingMode.Immediate,
            AssemblyResolver = resolver,
            ReadSymbols = false
        });

        // Locate AssetDatabase<T> class (compiled name: "AssetDatabase`1")
        var adb = module.Types.FirstOrDefault(t =>
            t.Name.StartsWith("AssetDatabase`", StringComparison.Ordinal) &&
            t.HasGenericParameters);
        if (adb == null)
        {
            module.Dispose();
            return PatchSummary.Skipped("AssetDatabase<T> class not found");
        }

        // Locate the <PopulateFromDataSource>d__N nested state machine
        var sm = adb.NestedTypes.FirstOrDefault(t =>
            t.Name.StartsWith("<PopulateFromDataSource>d__", StringComparison.Ordinal));
        if (sm == null)
        {
            module.Dispose();
            return PatchSummary.Skipped("<PopulateFromDataSource>d__N state machine not found");
        }

        // Locate its MoveNext method
        var moveNext = sm.Methods.FirstOrDefault(m => m.Name == "MoveNext");
        if (moveNext == null || !moveNext.HasBody || moveNext.Body.Instructions.Count < 5)
        {
            module.Dispose();
            return PatchSummary.Skipped("MoveNext body not found");
        }

        // Locate required fields
        var m_DataSource = adb.Fields.FirstOrDefault(f => f.Name == "m_DataSource");
        var stateField = sm.Fields.FirstOrDefault(f => f.Name == "<>1__state");
        var builderField = sm.Fields.FirstOrDefault(f => f.Name == "<>t__builder");
        if (m_DataSource == null || stateField == null || builderField == null)
        {
            module.Dispose();
            return PatchSummary.Skipped("Required fields (m_DataSource, <state>, <builder>) not found");
        }

        // Find the prologue pattern at the very start of MoveNext:
        //   ldarg.0
        //   ldfld '<>1__state'
        //   stloc.0
        //   ldarg.0
        //   ldfld '<>4__this'
        //   stloc.1
        // (the try block follows immediately after)
        //
        // We insert our guard right after the stloc.1 (index of last prologue instr).
        var instructions = moveNext.Body.Instructions;
        int stlocThisIndex = -1;
        for (int i = 0; i < Math.Min(instructions.Count, 20); i++)
        {
            if (instructions[i].OpCode != OpCodes.Stloc_1) continue;
            if (i < 1) continue;
            if (instructions[i - 1].OpCode != OpCodes.Ldfld) continue;
            var fr = instructions[i - 1].Operand as FieldReference;
            if (fr == null || fr.Name != "<>4__this") continue;
            stlocThisIndex = i;
            break;
        }
        if (stlocThisIndex < 0)
        {
            module.Dispose();
            return PatchSummary.Skipped("MoveNext prologue (ldfld '<>4__this' / stloc.1) not found");
        }

        // Idempotency: if we already inserted our guard, we expect to see two
        // consecutive ldfld m_DataSource within the first ~30 instructions.
        // The first one is our guard; the second is the original (now shifted).
        // Easier check: search for our sentinel "ldc.i4.s -2" within the first
        // 40 instructions — that's only present if we've patched.
        for (int i = 0; i < Math.Min(instructions.Count, 40); i++)
        {
            if (instructions[i].OpCode == OpCodes.Ldc_I4_S &&
                instructions[i].Operand is sbyte sb && sb == -2)
            {
                module.Dispose();
                return PatchSummary.AlreadyPatched("Colossal.IO.AssetDatabase.dll");
            }
        }

        // Build the guard instructions. The flow:
        //
        //   stloc.1                          (existing prologue end)
        //   ldloc.1                          (load `this` db for the null check)
        //   ldfld m_DataSource               (push m_DataSource)
        //   brtrue.S <originalNext>          (if non-null, skip guard; pops value)
        //   ldarg.0                          (state machine — null path)
        //   ldc.i4.s -2                      (final state sentinel)
        //   stfld '<>1__state'               (state = -2)
        //   ldarg.0                          (state machine again)
        //   ldflda '<>t__builder'            (builder by ref)
        //   call AsyncTaskMethodBuilder::SetResult()
        //   ret
        //   <originalNext>                   (continues existing code)
        //
        // Important: `brtrue.s` consumes the value on the stack (pops it to test),
        // so the stack is empty after it regardless of branch taken. We do NOT
        // need a `pop` in the null path. An earlier version had a redundant `pop`
        // here which Mono's verifier rejected with InvalidProgramException at
        // runtime (it would have been caught at JIT verify time).
        //
        // This matches the existing completion path that the catch handler and
        // normal completion both already use — see the IL around the end of
        // MoveNext for the exact same opcode sequence.
        if (dryRun)
        {
            module.Dispose();
            return new PatchSummary("Colossal.IO.AssetDatabase.dll", 1, DryRun: true);
        }

        var originalNext = instructions[stlocThisIndex + 1];

        var guardLdloc = Instruction.Create(OpCodes.Ldloc_1);
        var guardLdfld = Instruction.Create(OpCodes.Ldfld, m_DataSource);
        var guardBrtrue = Instruction.Create(OpCodes.Brtrue_S, originalNext);

        var nullPathLdarg0 = Instruction.Create(OpCodes.Ldarg_0);
        var nullPathLdcMinus2 = Instruction.Create(OpCodes.Ldc_I4_S, (sbyte)-2);
        var nullPathStfldState = Instruction.Create(OpCodes.Stfld, stateField);
        var nullPathLdarg0b = Instruction.Create(OpCodes.Ldarg_0);
        var nullPathLdfldaBuilder = Instruction.Create(OpCodes.Ldflda, builderField);
        var nullPathCallSetResult = Instruction.Create(OpCodes.Call, ImportSetResult(module));
        var nullPathRet = Instruction.Create(OpCodes.Ret);

        var ilProcessor = moveNext.Body.GetILProcessor();
        var anchor = instructions[stlocThisIndex];
        ilProcessor.InsertAfter(anchor, guardLdloc);
        ilProcessor.InsertAfter(guardLdloc, guardLdfld);
        ilProcessor.InsertAfter(guardLdfld, guardBrtrue);
        ilProcessor.InsertAfter(guardBrtrue, nullPathLdarg0);
        ilProcessor.InsertAfter(nullPathLdarg0, nullPathLdcMinus2);
        ilProcessor.InsertAfter(nullPathLdcMinus2, nullPathStfldState);
        ilProcessor.InsertAfter(nullPathStfldState, nullPathLdarg0b);
        ilProcessor.InsertAfter(nullPathLdarg0b, nullPathLdfldaBuilder);
        ilProcessor.InsertAfter(nullPathLdfldaBuilder, nullPathCallSetResult);
        ilProcessor.InsertAfter(nullPathCallSetResult, nullPathRet);

        // Brtrue_S has a 1-byte signed offset limit (±127). Our guard adds
        // ~12 instructions / ~25 bytes, well within range.
        // Mono.Cecil's SimplifyBranches() (called automatically on Write)
        // resolves any range issues by widening Brtrue_S -> Brtrue if needed.

        BackupAndWrite(module, dllPath);
        return new PatchSummary("Colossal.IO.AssetDatabase.dll", 1, DryRun: false);
    }

    static MethodReference ImportSetResult(ModuleDefinition module)
    {
        // Import the parameterless AsyncTaskMethodBuilder.SetResult() from mscorlib.
        // Mono.Cecil doesn't ship with a built-in "core library" — we resolve mscorlib
        // via the assembly resolver and look up the type by full name.
        var mscorlibAsm = module.AssemblyResolver.Resolve(
            new AssemblyNameReference("mscorlib", new Version(4, 0, 0, 0)));
        if (mscorlibAsm == null)
            throw new InvalidOperationException("mscorlib not resolvable");
        var resolved = mscorlibAsm.MainModule.GetType(
            "System.Runtime.CompilerServices.AsyncTaskMethodBuilder");
        if (resolved == null)
            throw new InvalidOperationException("AsyncTaskMethodBuilder type not found in mscorlib");
        var setResult = resolved.Methods.First(m =>
            m.Name == "SetResult" && m.Parameters.Count == 0 && !m.HasGenericParameters);
        return module.ImportReference(setResult);
    }

    // Fix 19 — see file header for the rationale.
    //
    // Replaces the `ldnull` instruction that SteamCloudDataSource.PopulateDataSource
    // uses to signal "Steam not running" with a Task.FromResult(emptyList) so the
    // generic AssetDatabase<SteamCloud>.PopulateFromDataSource awaiter doesn't NRE.
    public static PatchSummary PatchSteamCloudNullTask(string managedDir, bool dryRun)
    {
        var dllPath = Path.Combine(managedDir, "Colossal.IO.AssetDatabase.dll");
        if (!File.Exists(dllPath))
            return PatchSummary.Skipped("Colossal.IO.AssetDatabase.dll not found");

        var resolver = new UnityEngineStubResolver(managedDir);
        var module = ModuleDefinition.ReadModule(dllPath, new ReaderParameters
        {
            ReadingMode = ReadingMode.Immediate,
            AssemblyResolver = resolver,
            ReadSymbols = false
        });

        var scds = module.Types.FirstOrDefault(t => t.Name == "SteamCloudDataSource");
        if (scds == null)
        {
            module.Dispose();
            return PatchSummary.Skipped("SteamCloudDataSource not found");
        }

        // SteamCloudDataSource.PopulateDataSource is a non-async method that returns
        // Task<T> directly (no separate state-machine type). Its body is where the
        // !isInitialized null-return happens.
        var popMethod = scds.Methods.FirstOrDefault(m => m.Name == "PopulateDataSource");
        if (popMethod == null || !popMethod.HasBody)
        {
            module.Dispose();
            return PatchSummary.Skipped("SteamCloudDataSource.PopulateDataSource body not found");
        }

        // Pattern we want (or, after a previous patch run, the equivalent
        // "already-patched" pattern):
        //
        //   ldfld m_PlatformManager
        //   callvirt SteamworksPlatform::get_isInitialized()
        //   brtrue.s <skipNullPath>
        //   ldnull                  ← unpatched shape
        //   stloc.1
        //   leave <completion>
        //
        //   …or, after Fix 19 has been applied once already…
        //
        //   brtrue.s <skipNullPath>
        //   newobj List<(Type,Identifier)>::.ctor()
        //   call Task.FromResult<IReadOnlyCollection<(Type,Identifier)>>(list)
        //   stloc.1
        //   leave <completion>
        //
        // The unpatched shape has 3 instructions (ldnull; stloc.1; leave).
        // The patched shape has 4 (newobj; call; stloc.1; leave) because Fix 19
        // inserts `call Task.FromResult` between newobj and stloc.1. We accept
        // both shapes and use the first instruction's opcode to decide
        // alreadyPatched.
        MethodDefinition targetMethod = popMethod;
        Instruction? targetInstruction = null;
        bool alreadyPatched = false;
        var instrs = popMethod.Body.Instructions;
        for (int i = 0; i < instrs.Count - 4; i++)
        {
            OpCode first = instrs[i].OpCode;
            bool isUnpatched = first == OpCodes.Ldnull;
            bool isAlready = first == OpCodes.Newobj;
            if (!isUnpatched && !isAlready) continue;

            // Validate the rest of the shape based on which we matched.
            int stloc1At = -1, leaveAt = -1;
            if (isUnpatched)
            {
                // ldnull ; stloc.1 ; leave
                if (instrs[i + 1].OpCode != OpCodes.Stloc_1) continue;
                if (instrs[i + 2].OpCode != OpCodes.Leave && instrs[i + 2].OpCode != OpCodes.Leave_S)
                    continue;
                stloc1At = i + 1;
                leaveAt = i + 2;
            }
            else
            {
                // newobj ; call ; stloc.1 ; leave
                if (instrs[i + 1].OpCode != OpCodes.Call) continue;
                if (instrs[i + 2].OpCode != OpCodes.Stloc_1) continue;
                if (instrs[i + 3].OpCode != OpCodes.Leave && instrs[i + 3].OpCode != OpCodes.Leave_S)
                    continue;
                stloc1At = i + 2;
                leaveAt = i + 3;
            }

            // Walk back to confirm there's a get_isInitialized nearby
            bool foundIsInit = false;
            for (int j = Math.Max(0, i - 15); j < i; j++)
            {
                if (instrs[j].OpCode == OpCodes.Callvirt &&
                    instrs[j].Operand is MethodReference mr &&
                    mr.Name == "get_isInitialized")
                {
                    foundIsInit = true;
                    break;
                }
            }
            if (!foundIsInit) continue;
            targetInstruction = instrs[i];
            alreadyPatched = isAlready;
            break;
        }

        if (targetInstruction == null)
        {
            module.Dispose();
            return PatchSummary.Skipped("SteamCloud !isInitialized null-return pattern not found");
        }

        if (alreadyPatched)
        {
            module.Dispose();
            return PatchSummary.AlreadyPatched("Colossal.IO.AssetDatabase.dll");
        }

        if (dryRun)
        {
            module.Dispose();
            return new PatchSummary("Colossal.IO.AssetDatabase.dll", 1, DryRun: true);
        }

        // Build replacement: newobj List<(Type,Identifier)>::.ctor() ;
        //                    call Task.FromResult<IReadOnlyCollection<(Type,Identifier)>>(<the list>)
        // The Task.FromResult return type is implicitly cast from List<...> (which
        // implements IReadOnlyCollection<...>). The original `stloc.1` and `leave`
        // instructions stay — they store the returned Task in loc.1 and jump to the
        // completion path (which then returns loc.1).
        var listCtor = ImportListTupleCtor(module);
        var fromResult = ImportFromResultForTuple(module);

        var ilProcessor = targetMethod.Body.GetILProcessor();
        var newList = Instruction.Create(OpCodes.Newobj, listCtor);
        var callFromResult = Instruction.Create(OpCodes.Call, fromResult);

        // Replace ldnull with newList; callFromResult. Original stloc.1 stays.
        ilProcessor.Replace(targetInstruction, newList);
        ilProcessor.InsertAfter(newList, callFromResult);

        BackupAndWrite(module, dllPath);
        return new PatchSummary("Colossal.IO.AssetDatabase.dll", 1, DryRun: false);
    }

    static MethodReference ImportListTupleCtor(ModuleDefinition module)
    {
        // List<(Type, Identifier)>::.ctor() — must be a CLOSED generic instance,
        // otherwise Mono rejects the IL with BadImageFormatException("Method with
        // open type while not compiling gshared").
        var mscorlib = module.AssemblyResolver.Resolve(
            new AssemblyNameReference("mscorlib", new Version(4, 0, 0, 0)));
        var listType = mscorlib.MainModule.GetType("System.Collections.Generic.List`1");
        if (listType == null)
            throw new InvalidOperationException("List`1 not found in mscorlib");
        var ctor = listType.Methods.First(m =>
            m.IsConstructor && m.Parameters.Count == 0 && !m.HasGenericParameters);

        // Build a closed GenericInstanceType: List<(Type, Identifier)>
        var typeType = mscorlib.MainModule.GetType("System.Type");
        var tupleType = mscorlib.MainModule.GetType("System.ValueTuple`2");
        var idType = module.GetType("Colossal.IO.AssetDatabase.Identifier");
        if (typeType == null || tupleType == null || idType == null)
            throw new InvalidOperationException("Required types for List<(Type,Identifier)> not found");

        var valueTupleInst = new GenericInstanceType(tupleType);
        valueTupleInst.GenericArguments.Add(typeType);
        valueTupleInst.GenericArguments.Add(idType);
        var listInst = new GenericInstanceType(listType);
        listInst.GenericArguments.Add(valueTupleInst);

        // Create a MethodReference on the closed List<(Type, Identifier)>.
        // The .ctor of List<T> is HasThis=true, no parameters, returns void.
        var closedListCtor = new MethodReference(".ctor",
            mscorlib.MainModule.TypeSystem.Void, listInst);
        closedListCtor.HasThis = true;
        return module.ImportReference(closedListCtor);
    }

    static MethodReference ImportFromResultForTuple(ModuleDefinition module)
    {
        // Task.FromResult<IReadOnlyCollection<(Type, Identifier)>>(<the value>)
        var mscorlib = module.AssemblyResolver.Resolve(
            new AssemblyNameReference("mscorlib", new Version(4, 0, 0, 0)));
        var taskType = mscorlib.MainModule.GetType("System.Threading.Tasks.Task");
        if (taskType == null)
            throw new InvalidOperationException("Task not found in mscorlib");
        var fromResult = taskType.Methods.First(m =>
            m.Name == "FromResult" && m.HasGenericParameters && m.Parameters.Count == 1);
        // Bind the generic parameter with the right instantiation: IReadOnlyCollection<(Type, Identifier)>
        // The IL in the DLL already references this same instantiation, so we can find it
        // by searching the module's type references for any method that uses Task.FromResult
        // with the matching generic args.
        MethodReference? existingCall = null;
        foreach (var tr in module.GetTypeReferences())
        {
            // not enumerable — fall through
        }
        // Easier: enumerate all method references reachable from any IL body,
        // looking for one that calls Task.FromResult with our exact generic args.
        foreach (var t in module.Types)
        {
            if (existingCall != null) break;
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var instr in m.Body.Instructions)
                {
                    if (instr.OpCode != OpCodes.Call) continue;
                    if (instr.Operand is not GenericInstanceMethod gi) continue;
                    if (gi.Name != "FromResult") continue;
                    if (gi.DeclaringType.FullName != "System.Threading.Tasks.Task") continue;
                    if (gi.GenericArguments.Count != 1) continue;
                    var arg = gi.GenericArguments[0].FullName;
                    if (arg == "System.Collections.Generic.IReadOnlyCollection`1<System.ValueTuple`2<System.Type, Colossal.IO.AssetDatabase.Identifier>>")
                    {
                        existingCall = gi;
                        break;
                    }
                }
                if (existingCall != null) break;
            }
        }
        if (existingCall != null)
            return module.ImportReference(existingCall);

        // Last resort: construct the reference manually. Task.FromResult<T>(T result)
        // returns Task<T>. We want FromResult<IReadOnlyCollection<(Type,Identifier)>>
        // with argument matching the List constructor's output.
        var bound = new GenericInstanceMethod(fromResult);
        // Build IReadOnlyCollection<(Type, Identifier)> generic instance
        var iroColTypeRef = mscorlib.MainModule.GetType(
            "System.Collections.Generic.IReadOnlyCollection`1");
        var tupleType = mscorlib.MainModule.GetType("System.ValueTuple`2");
        var typeType = mscorlib.MainModule.GetType("System.Type");
        var idType = module.GetType("Colossal.IO.AssetDatabase.Identifier");
        if (iroColTypeRef == null || tupleType == null || typeType == null || idType == null)
            throw new InvalidOperationException("Required generic types not found");

        var valueTupleInst = new GenericInstanceType(tupleType);
        valueTupleInst.GenericArguments.Add(typeType);
        valueTupleInst.GenericArguments.Add(idType);
        var iroColInst = new GenericInstanceType(iroColTypeRef);
        iroColInst.GenericArguments.Add(valueTupleInst);

        bound.GenericArguments.Add(iroColInst);
        return module.ImportReference(bound);
    }

    static void BackupAndWrite(ModuleDefinition module, string dllPath) =>
        TimestampedBackup.BackupAndWrite(module, dllPath);
}

// Resolver that returns a stub assembly for UnityEngine.* and other references
// not present in the Managed/ directory. Mono.Cecil 0.11.6 + ReadingMode.Immediate
// eagerly resolves custom-attribute enum types via TypeReference.Resolve(), which
// in turn calls AssemblyResolver.Resolve() and throws on failure. We don't need
// the actual UnityEngine bodies — just enough for the resolver to not throw so
// IL rewriting can proceed.
sealed class UnityEngineStubResolver : DefaultAssemblyResolver
{
    static readonly HashSet<string> StubNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "UnityEngine.CoreModule",
        "UnityEngine.IMGUIModule",
        "UnityEngine.SharedInternalsModule",
        "UnityEngine.AudioModule",
        "UnityEngine.UnityWebRequestAudioModule",
        "UnityEngine.UnityWebRequestModule",
        "UnityEngine.VirtualTexturingModule",
        "UnityEngine.PropertiesModule",
        "UnityEngine.AssetBundleModule",
        "UnityEngine.JSONSerializeModule",
        "UnityEngine.TextRenderingModule",
        "UnityEngine.UnityAnalyticsModule",
        "UnityEngine.ParticleSystemModule",
        "UnityEngine.PhysicsModule",
        "UnityEngine.UIModule",
        "UnityEngine.UIElementsModule",
        "UnityEngine.InputLegacyModule",
        "UnityEngine.InputModule",
        "UnityEngine.AndroidJNIModule",
        "UnityEngine.AnimationModule",
        "UnityEngine.UnityWebRequestTextureModule",
        "UnityEngine.TerrainModule",
        "UnityEngine.IMGUIModule",
        "UnityEngine.DirectorModule",
        "UnityEngine.StreamingModule",
        "UnityEngine.SubstanceModule",
        "UnityEngine.TilemapModule",
        "UnityEngine.VRModule",
        "UnityEngine.WindModule",
        "UnityEngine.ClusterInputModule",
        "UnityEngine.ClusterRendererModule",
        "UnityEngine.ScreenCaptureModule",
        "UnityEngine.SpriteMaskModule",
        "UnityEngine.SpriteShapeModule",
        "UnityEngine.SubsystemsModule",
        "UnityEngine.TLSModule",
        "UnityEngine.TextCoreModule",
        "UnityEngine.UIElementsNativeModule",
        "UnityEngine.UnityAnalyticsCommonModule",
        "UnityEngine.UnityWebRequestAssetBundleModule",
        "UnityEngine.UnityWebRequestWSAModule",
        "UnityEngine.VideoModule",
        "UnityEngine.GameCenterModule",
        "UnityEngine.AIModule",
        "UnityEngine.CrashReportingModule",
        "UnityEngine.dll",
        "netstandard",
        "mscorlib",
        "System.Runtime",
        "System.Private.CoreLib",
    };

    static readonly AssemblyNameReference StubRef = new("UnityEngine.CoreModule", new Version(0, 0, 0, 0));

    public UnityEngineStubResolver(string managedDir) => AddSearchDirectory(managedDir);

    public override AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        try
        {
            return base.Resolve(name, parameters);
        }
        catch (AssemblyResolutionException) when (StubNames.Contains(name.Name))
        {
            return BuildStub(name);
        }
    }

    static AssemblyDefinition BuildStub(AssemblyNameReference name)
    {
        var asmName = new AssemblyNameDefinition(name.Name, name.Version);
        var asm = AssemblyDefinition.CreateAssembly(asmName, "stub", ModuleKind.Dll);
        // Add an empty type so TypeReference.Resolve() doesn't blow up
        asm.MainModule.Types.Add(new TypeDefinition("Stub", "Stub",
            TypeAttributes.NotPublic | TypeAttributes.Abstract));
        return asm;
    }
}
