// Patches Colossal.PSI.Common.dll — Fix 30: hardcode DLC cache
// Replaces GetDlcAttributes entirely to pre-populate m_CachedAttributes.

using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.IO;
using System.Linq;

namespace Cs2MacPatcher;

static class HardcodeDlcCachePatcher
{
    static readonly (string name, int dlcId)[] DLCs = {
        ("BridgesAndPorts", 1), ("CityStations", 2), ("DeluxeRelaxRadio", 3),
        ("Game", 0), ("LeisureVenues", 4), ("ModernArchitecture", 5),
        ("Skyscrapers", 6), ("UrbanPromenades", 7),
    };

    public static PatchSummary Patch(string managedDir, bool dryRun)
    {
        var dllPath = Path.Combine(managedDir, "Colossal.PSI.Common.dll");
        if (!File.Exists(dllPath)) return PatchSummary.Skipped("Colossal.PSI.Common.dll not found");

        var module = ModuleDefinition.ReadModule(dllPath,
            new ReaderParameters { ReadingMode = ReadingMode.Immediate });

        var dlcHelper = module.Types.FirstOrDefault(t => t.FullName == "Colossal.PSI.Common.DlcHelper");
        if (dlcHelper == null) { module.Dispose(); return PatchSummary.Skipped("DlcHelper not found"); }

        var getAttributes = dlcHelper.Methods.FirstOrDefault(m => m.Name == "GetDlcAttributes");
        if (getAttributes == null || !getAttributes.HasBody)
        { module.Dispose(); return PatchSummary.Skipped("GetDlcAttributes body not found"); }

        // Capture Dict.ctor from original body. For Add, we use set_Item instead.
        MethodReference? origDictCtor = null;
        foreach (var instr in getAttributes.Body.Instructions)
        {
            if (instr.OpCode == OpCodes.Newobj && instr.Operand is MethodReference mr &&
                mr.Name == ".ctor" && mr.DeclaringType.Name.StartsWith("Dictionary"))
            { origDictCtor = mr; break; }
        }
        if (origDictCtor == null)
        { module.Dispose(); return PatchSummary.Skipped("Dict.ctor not found in original body"); }

        // Build set_Item(DlcId, DlcAttribute) on the same declaring type as origDictCtor
        var dictType = ((GenericInstanceType)origDictCtor.DeclaringType);
        var setItem = new MethodReference("set_Item", module.TypeSystem.Void, dictType) { HasThis = true };
        setItem.Parameters.Add(new ParameterDefinition(dictType.GenericArguments[0]));
        setItem.Parameters.Add(new ParameterDefinition(dictType.GenericArguments[1]));
        var origSetItem = module.ImportReference(setItem);

        // Idempotency: patched body has ≤ 80 instructions (original has ~120)
        if (getAttributes.Body.Instructions.Count <= 80)
        { module.Dispose(); return PatchSummary.AlreadyPatched("Colossal.PSI.Common.dll"); }

        if (dryRun) { module.Dispose(); return new PatchSummary("Colossal.PSI.Common.dll", 1, DryRun: true); }

        // Resolve types from the module itself
        var dlcIdType = module.GetType("Colossal.PSI.Common.DlcId");
        var attrType = module.GetType("Colossal.PSI.Common.DlcAttribute");
        var variantType = new TypeReference("Colossal.Json", "Variant", module,
            module.AssemblyReferences.First(r => r.Name == "Colossal.Core"));
        if (dlcIdType == null || attrType == null)
        { module.Dispose(); return PatchSummary.Skipped("DlcId or DlcAttribute type not found"); }

        var cachedAttrField = dlcHelper.Fields.First(f => f.Name == "m_CachedAttributes");
        var attrCtor = attrType.Resolve().Methods.First(m =>
            m.IsConstructor && m.Parameters.Count == 2 &&
            m.Parameters[0].ParameterType.MetadataType == MetadataType.Int32);
        var dlcIdCtor = dlcIdType.Resolve().Methods.First(m =>
            m.IsConstructor && m.Parameters.Count == 1 &&
            m.Parameters[0].ParameterType.MetadataType == MetadataType.Int32);
        var internalNameField = attrType.Resolve().Fields.First(f => f.Name == "internalName");

        var attrCtorRef = module.ImportReference(attrCtor);
        var dlcIdCtorRef = module.ImportReference(dlcIdCtor);
        var internalNameRef = module.ImportReference(internalNameField);
        var cachedAttrRef = module.ImportReference(cachedAttrField);

        getAttributes.Body.Instructions.Clear();
        getAttributes.Body.ExceptionHandlers.Clear();
        getAttributes.Body.Variables.Clear();
        getAttributes.Body.InitLocals = true;
        getAttributes.Body.MaxStackSize = 8;

        var v_attr = new VariableDefinition(attrType);
        var v_variant = new VariableDefinition(variantType);
        var v_dlcId = new VariableDefinition(dlcIdType);
        getAttributes.Body.Variables.Add(v_attr);
        getAttributes.Body.Variables.Add(v_variant);
        getAttributes.Body.Variables.Add(v_dlcId);

        var il = getAttributes.Body.GetILProcessor();

        // new Dictionary<DlcId, DlcAttribute>()
        il.Append(il.Create(OpCodes.Newobj, origDictCtor));
        il.Append(il.Create(OpCodes.Stsfld, cachedAttrRef));

        foreach (var (name, id) in DLCs)
        {
            il.Append(il.Create(OpCodes.Ldloca_S, v_variant));
            il.Append(il.Create(OpCodes.Initobj, variantType));
            il.Append(il.Create(OpCodes.Ldc_I4, id));
            il.Append(il.Create(OpCodes.Ldloc, v_variant));
            il.Append(il.Create(OpCodes.Newobj, attrCtorRef));
            il.Append(il.Create(OpCodes.Stloc, v_attr));
            il.Append(il.Create(OpCodes.Ldloc, v_attr));
            il.Append(il.Create(OpCodes.Ldstr, name));
            il.Append(il.Create(OpCodes.Stfld, internalNameRef));
            il.Append(il.Create(OpCodes.Ldloca_S, v_dlcId));
            il.Append(il.Create(OpCodes.Ldc_I4, id));
            il.Append(il.Create(OpCodes.Call, dlcIdCtorRef));
            il.Append(il.Create(OpCodes.Ldsfld, cachedAttrRef));
            il.Append(il.Create(OpCodes.Ldloc, v_dlcId));
            il.Append(il.Create(OpCodes.Ldloc, v_attr));
            il.Append(il.Create(OpCodes.Callvirt, origSetItem));
        }

        il.Append(il.Create(OpCodes.Ret));

        TimestampedBackup.BackupAndWrite(module, dllPath);
        return new PatchSummary("Colossal.PSI.Common.dll", 1, DryRun: false);
    }
}
