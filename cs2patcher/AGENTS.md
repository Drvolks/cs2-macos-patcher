# AGENTS.md — CS2 macOS Patcher (development guide)

## Quick start

```bash
# Re-run the full patcher
echo "2" | python3 patch.py

# Full mode (Paradox Mods) without interaction
cd cs2patcher && dotnet run --project cs2patcher -- <managed-dir> full --apply
```

## File layout

```
patch.py                          # Main launcher (Python)
cs2-css-cohtml-fix.py             # CSS post-processor for Cohtml
ntl-reencrypt.py                  # .ntl manifest re-encryption (Fix 25 companion)
cs2patcher/
  cs2patcher.csproj               # .NET 9 project
  Program.cs                      # Patch pipeline (which DLLs get patched, in what order)
  PatchSummary.cs                 # Result record
  Patchers/
    ColossalIoPatcher.cs          # LongDirectory MoveNext + LongFile.Open fallback
    LongFileOpenWineFallbackPatcher.cs  # AleanniMods-style LongFile.Open/GetFileHandle fixes
    AssetDatabasePatcher.cs       # .priority file File.Exists NOP
    AssetDatabaseDataSourceGuardPatcher.cs  # Fix 18 (m_DataSource NRE guard) + Fix 19 (SteamCloud null Task)
    PsiCommonDlcGuardPatcher.cs   # Fix 24: NOP throw in Game catch handler
    HashHelperComputeHashPatcher.cs  # Fix 25: simplify ComputeHash to single xxHash3
    DecryptFileShortCiphertextPatcher.cs  # Fix 26: bypass AES, return hardcoded JSON
    DlcIdConflictOverridePatcher.cs  # Fix 27: always overwrite on dlcId conflict
    SkipDlcExistsCheckPatcher.cs  # Fix 29: skip LongFile.Exists check in GetDlcAttributes
    RiderPathLocatorPatcher.cs    # Fix 20: Wine File.Exists lie in RiderPathLocator
    PdxSdkPatcher.cs              # 17 Paradox Mods SDK fixes
    InjectDlcCachePatcher.cs      # Fix 31: inject hardcoded DLC cache entries
    SteamworksDlcMapperMapPatcher.cs  # Fix 32: Map() uses set_Item instead of Add
    PlatformManagerIsDlcOwnedPatcher.cs  # Fix 33: auto-own all non-Invalid DLCs on Wine
    TimestampedBackup.cs          # Shared backup helper (timestamped .bak files)
docs/
  technical.md                    # Detailed root-cause analysis
```

## Build

```bash
cd cs2patcher && dotnet build
```

Requires .NET 9 SDK. The project uses `<RollForward>Major</RollForward>` so it works with .NET 10+.

## Game file locations

```
# Game root
~/Library/Application Support/CrossOver/Bottles/Cities Skylines II/drive_c/
  Program Files (x86)/Steam/steamapps/common/Cities Skylines II/

# DLLs we patch
Cities2_Data/Managed/Colossal.IO.dll
Cities2_Data/Managed/Colossal.IO.AssetDatabase.dll
Cities2_Data/Managed/Colossal.PSI.Common.dll
Cities2_Data/Managed/Game.dll
Cities2_Data/Managed/PDX.SDK.dll

# Content directory (DLC .ntl files)
Cities2_Data/Content/<DLC>/<DLC>.ntl

# CSS for game UI
Cities2_Data/Content/Game/UI/index.css
```

## Game logs

```
# CrossOver bottle
~/Library/Application Support/CrossOver/Bottles/Cities Skylines II/drive_c
  users/crossover/AppData/LocalLow/Colossal Order/Cities Skylines II/

# Main log
Player.log

# Subsystem logs
Logs/FileSystem.log    # Database registrations, content integrity errors
Logs/SceneFlow.log     # GameManager lifecycle, version info
Logs/UI.log            # Cohtml UI loading, CSS warnings
Logs/Steamworks.log    # Steam platform errors
Logs/Modding.log       # Mod loading
Logs/PdxSdk.log        # Paradox Mods SDK operations
```

## How to restore original DLLs

```bash
cd ".../Cities2_Data/Managed"

# Restore from timestamped backup (recommended)
cp "Colossal.IO.dll.bak.20260626-122500" "Colossal.IO.dll"

# Restore from plain backup (most recent pre-patch state)
cp "Colossal.IO.dll.bak" "Colossal.IO.dll"

# List all backups
ls -la *.bak*

# Delete stale backups (caution: no way back)
rm -f *.bak *.bak.*
```

Alternatively, use Steam to verify game files:
- Right-click CS2 → Properties → Installed Files → Verify integrity

## How to test

```bash
# 1. Delete stale .bak files (they cause false "already patched" skips)
cd ".../Cities2_Data/Managed" && rm -f *.bak

# 2. Run the patcher
cd /path/to/cs2-macos-patcher && echo "2" | python3 patch.py

# 3. Enable Wine virtual desktop (REQUIRED for display)
# Edit ~/Library/Application Support/CrossOver/Bottles/Cities Skylines II/user.reg
# Add this section if not present:
#   [Software\\Wine\\Explorer]
#   "Desktop"="1920x1080"
# Without this, CrossOver reports screen as 1×1 and UI renders broken.

# 4. Launch the game via CrossOver

# 5. Check the Player.log for FATAL errors
tail -f ".../AppData/LocalLow/Colossal Order/Cities Skylines II/Player.log" | grep FATAL

# 6. Check FileSystem.log for DLC registrations
tail -f ".../Logs/FileSystem.log" | grep Registered
```

Expected log output on a working install:
```
[INFO]  Registered 'SteamCloud' database
[INFO]  Registered 'User' database
[INFO]  Registered 'BridgesAndPorts' database
[INFO]  Registered 'CityStations' database
[INFO]  Registered 'DeluxeRelaxRadio' database
[INFO]  Registered 'Game' database
[INFO]  Registered 'LeisureVenues' database
[INFO]  Registered 'ModernArchitecture' database
[INFO]  Registered 'Skyscrapers' database
[INFO]  Registered 'UrbanPromenades' database
[INFO]  Boot completed
[INFO]  Loading mode MainMenu with purpose Cleanup
[INFO]  MainMenu reached
```

## CrossOver settings

Located in `cxbottle.conf`:
```ini
[EnvironmentVariables]
WINEMSYNC = "1"           # MSync synchronization
CX_GRAPHICS_BACKEND = "d3dmetal"  # DirectX 12 through Metal
D3DM_ENABLE_METALFX = "1"  # DLSS / MetalFX
```

## Known issues & fixes

### Game v1.6.0f1 — new NullReferenceException in AssetDatabase\<T\>
**Fix:** Fix 18 (AssetDatabaseDataSourceGuardPatcher)

### Game v1.6.0f1 — SteamCloudDataSource returns null Task
**Fix:** Fix 19 (AssetDatabaseDataSourceGuardPatcher.PatchSteamCloudNullTask)

### Game v1.6.0f1 — DlcAttribute::ctor(int, Variant) NRE
**Root cause:** On Wine, `DlcHelper.GetDlcAttributes` decrypts .ntl files with the wrong xxHash3 key (directory enumeration order differs from Windows). JSON deserialization returns null Variant → `DlcAttribute(int, Variant)::ctor` calls `variant.TryGet("version")` without a null check → NRE.
**Fix:** Fix 31 (InjectDlcCachePatcher) — inject hardcoded `DlcAttribute(int)` entries for all 8 DLCs into `m_CachedAttributes` and return early, bypassing the broken loop. Use the single-arg `DlcAttribute(int)` ctor (NOT `int, Variant` — that one NREs).
**Important:** Must import the ctor manually from the `DlcAttribute` type via `module.ImportReference(typeDef.Methods.First(m => m.IsConstructor && m.Parameters.Count == 1))` — searching the existing body won't find it (only the 2-arg ctor is called in original IL).

### Game v1.6.0f1 — SteamworksDlcMapper.Map "Key already added" exception
**Root cause:** `Game.Dlc.SteamworksDlcsMapping..ctor` calls `Map(LandmarkBuildings=0, ...)`, `Map(SanFranciscoSet=1, ...)`, `Map(BridgesAndPorts=5, ...)` — on Wine the `m_Mapping` Dictionary ends up with a duplicate DlcId 0 entry (possibly from `Activator.CreateInstance` being invoked twice with stale state, or from interaction with `RemapAttributeUtility`).
**Fix:** Fix 32 (SteamworksDlcMapperMapPatcher) — replace `callvirt Dictionary::Add` with `callvirt Dictionary::set_Item` in `Map` so duplicate keys overwrite instead of throw.
**Important:** The new MethodReference for `set_Item` must be cloned from the existing `Add` reference (copy DeclaringType, HasThis, CallingConvention, Parameters) — `module.ImportReference(new MethodReference(...))` fails with `MissingMethodException` because Mono.Cecil can't properly import brand-new generic-instance method references.

### Game v1.6.0f1 — No DLC databases register ("database 'gameui' does not exist")
**Root cause:** `PlatformManager.IsDlcOwned(dlcId)` only auto-owns `DlcId.BaseGame (-2009)`. On Wine, Steam backend's `IsDlcOwned` always returns false (real Steam isn't running), so `ContentHelper.RegisterContent` skips all DLC database registration.
**Fix:** Fix 33 (PlatformManagerIsDlcOwnedPatcher) — replace the entire method body with a minimal version that returns false only for `DlcId.Invalid` and true for everything else.
**Important:** Don't just patch the existing branch in-place (e.g. `brfalse.s IL_001e` → `nop`) — leaving the original `try/catch/finally` block as unreachable code after the new `ret` fails the CLR IL verifier with `InvalidProgramException` on load. Clear the body, clear exception handlers, and rebuild.

### Game v1.6.0f1 — CrossOver reports screen as 1×1, UI renders 1 pixel
**Root cause:** D3DMetal/Direct3D detects the display as 1×1 on Wine (likely because the window isn't fully created when display mode is queried). Unity HDRP tries to create a RenderTexture with size 0/0 (after scaling 1×1 by sub-fraction), throws `RenderTexture.Create failed: width & height must be larger than 0`. Unity's Screenmanager registry settings get overwritten by the game on every launch.
**Fix:** Add `[Software\Wine\Explorer] "Desktop"="1920x1080"` to `user.reg`. This enables Wine's virtual desktop at 1920×1080, which makes the display always report that size. Setting Unity's `Screenmanager Resolution Width_h.../Height_h...` in registry is overridden on each launch and doesn't help alone.
**File:** `~/Library/Application Support/CrossOver/Bottles/Cities Skylines II/user.reg`

### Game v1.6.0f1 — D3D11 multithread video decoder unavailable
**Symptom:** Warning `Dedicated video D3D11 device multithread protection failed (error: 0x80004002). Will use software video decoding.`
**Cause:** D3DMetal (Apple's Game Porting Toolkit) doesn't expose D3D11's multithread video decoder interface. The game falls back to software video decoding.
**Impact:** Video playback (intro movies) is slower / choppier. Game itself unaffected. No patch needed.

### .ntl Content integrity check fails (PKCS7 padding error)
**Root cause:** xxHash3 key derivation differs between Wine and Windows (directory enumeration order)
**Workaround:** Fix 26 (DecryptFile bypass) — short-circuits `HashHelper.DecryptFile` to return `{"dlcId": 0}` when ciphertext is empty.
**Real fix:** Capture xxHash3 key from Windows VM, hardcode on macOS

### LongFile.Open throws IOException("Success")
**Fix:** LongFileOpenWineFallbackPatcher (AleanniMods approach) — try/catch fallback in `LongFile.Open` and retry in `GetFileHandle` with `\\?\` prefix stripped.

### Cohtml UI shows CSS parsing errors and unresolved custom variables
**Symptom:** UI renders but with missing styles. Cohtml 1.64 (the bundled UI renderer) doesn't fully support modern CSS features:
- `border-width: var(--X)` shorthand → Cohtml silently drops the property
- `gap:` flex property → "Unsupported CSS property"
- `:hover > .child` selectors → "syntax error near text: >"
- Some custom property definitions in `:root` blocks → "Unable to resolve custom variable"
**Fix:** `cs2-css-cohtml-fix.py` — expands `border-width: var(...)` to 4 long-form properties and replaces `gap:` with margin-based equivalent. Doesn't fix every CSS warning, but eliminates the ones that cause UI elements to collapse.

### RiderPathLocator crashes with .settings.json
**Fix:** Fix 20 (RiderPathLocatorPatcher) — NOP `File.Exists` + turn `brfalse` into `br`.

### Paradox Mods downloads freeze
**Fix:** PdxSdkPatcher (FIX 15 — GetLockToken timer removal)

## Typical patch workflow

1. **User reports issue** → check Player.log for FATAL stack trace
2. **Find the offending method** → `ilspycmd -il <dll>` to inspect IL
3. **Write a Patcher** → new file in cs2patcher/Patchers/
4. **Wire it in Program.cs** → `Print(MynewPatcher.Patch(...))`
5. **Build + test** → `dotnet build && echo "2" | python3 patch.py`
6. **Launch game** → check Player.log, FileSystem.log for improvements
7. **Iterate** → if still broken, check .bak file idempotency, restore original, retry

## Mono.Cecil tips

- **`IsConstructor` doesn't exist on MethodReference** → use `mr.Name == ".ctor"` for ctors
- **Generic method import fails for closed types in same assembly** → capture MethodReference from original body, don't clear or re-import
- **`InsertAfter(anchor, X)` inserts at anchor+N** → subsequent inserts go BEFORE earlier ones; advance anchor to keep order
- **`ldsfld` pushes value, `ldsflda` pushes address** → use correct opcode for field access
- **Value-type constructors take `this` by address** → use `ldloca` + `initobj` for default-required structs
- **Exception handlers must cover valid try/catch ranges** → wrong ranges cause `InvalidProgramException`
- **`BackupAndWrite` creates backup BEFORE writing patch** → original DLL state is preserved in .bak
