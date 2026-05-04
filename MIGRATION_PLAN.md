# Match_PDT_MCP — Migration Plan: dotnet-helper v1 → v2

## Context

The current dotnet-helper does BinaryFormatter (de)serialization of `.dat` files using PDT's assemblies as a passive type library. The Python server (`server.py`) implements all business logic by manipulating `.hdb` XML directly. This works but reimplements PDT's Business layer in Python+XML, which is brittle (per-PDT-version drift) and limited (e.g., `add_can_message` can't create CSND/CRCV blocks; `add_custom_error` needs a sibling-project ERR-block clone).

PDT already implements all of this internally as proper services (`MessageService`, `SignalService`, `ErrorBuilder`, `IErrorBlockBuilder`, `IErrorBlockFactory`, `IDetectionMethodFactory`, `DatabaseListService`, `DatabaseVariableService`, …) wired through Autofac and exposed via a static `ServiceLocator`. The CLI assembly (`Hydac.PDT.Cli.Business.dll`) is **already a headless host** for that container — it just exposes only three verbs (`open`, `build`, `check-feature-key`) on top.

**v2 plan: replace the dotnet-helper with a long-lived process that boots PDT in headless mode the same way the CLI does, resolves the business services it needs from `ServiceLocator`, and exposes them over stdio JSON-RPC to the Python MCP server.**

## v1 preservation

The XML-direct implementation (server.py + dotnet-helper/) is preserved on git as:
- branch: `legacy/xml-v1`
- tag: `v1-xml-final`

Both point at commit `3ed0390` ("Fix CAN message creation issue"). v1 stays in `dotnet-helper/` on `main` until v2 is proven; the branch/tag are the long-term archive.

## Why this is better than today

| Concern | Today (XML-direct) | v2 (PDT services) |
|---|---|---|
| `add_custom_error` | Hand-rolled XML+`.dat` mutation, sibling-project cloning hack | `ErrorBuilder.CreateError(...)` + `IErrorBlockFactory.Create(...)` + `IDetectionMethodFactory.Create(...)` — single transaction, no cloning |
| `add_can_message` | Three-XML-file dance, manual ECU-link bookkeeping, "Usage=None must be set in PDT" caveat | `MessageService` setters + `IBlockLoader` (DI'd into the service) handles CSND/CRCV creation natively |
| Validation | None — bad edits surface only when user reopens PDT | `Hydac.PDT.{Can,Database}.Business.Validation.*` available before save |
| PDT version drift | XML schema changes break the helper silently | Reference the installed PDT's binaries; breakage is a compile-time error |
| Unexposed surface (descriptions, scaling, mux signals, signal layers, error-record reactions, etc.) | Each requires hand-rolled XML | Already there on the service interfaces |

## Why this is NOT bypassing licensing

The v2 helper uses PDT's published in-process API. The user already has a licensed PDT install (required to build anyway). IntelliLock's module-cctor runs at startup like it does in `PDT.exe`. **Feature-key gates** (what `check-feature-key` queries) only block specific features — most CAN/database/error operations are core functionality and almost certainly aren't gated. Things that are likely gated: ISOBUS, certain protocols, specific codegen targets. We'll discover which by trying.

The current v1 dotnet-helper is arguably *less* legitimate: it depends on PDT's serialization types but never goes through PDT's runtime, so it never hits the license check at all. v2 is cleaner on every axis.

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│  server.py (Python MCP server, unchanged tool surface)      │
│  - Read tools (list_*, get_*, search_*) keep XML fast path  │
│  - Write tools forward to helper via stdio JSON-RPC         │
└────────────────────────────┬────────────────────────────────┘
                             │ stdin/stdout JSON-RPC
                             ▼
┌─────────────────────────────────────────────────────────────┐
│  Match_PDT_Helper_v2.exe  (long-lived, .NET Framework 4.8)  │
│                                                             │
│  ┌───────────────────────────────────────────────────────┐  │
│  │  STA thread w/ Dispatcher.Run() — "PDT thread"        │  │
│  │  ├─ HostBootstrap.Initialize(hdbPath)                 │  │
│  │  │    └─ CliSetup.CreateApplication()  (reflection)   │  │
│  │  │         ├─ CliInitialization.CreateApp(register)   │  │
│  │  │         ├─ InitializeApplicationSettings           │  │
│  │  │         ├─ KnowledgeBaseAgentInit (await)          │  │
│  │  │         ├─ InitializeMain                          │  │
│  │  │         └─ InitializeProjectConditioner            │  │
│  │  │     ServiceLocator.Resolve<ICliProjectCommands>    │  │
│  │  │       .LoadProjectAsync(hdb)                       │  │
│  │  │                                                    │  │
│  │  └─ Dispatcher.Invoke(serviceCall) for each request   │  │
│  └───────────────────────────────────────────────────────┘  │
│                                                             │
│  ┌───────────────────────────────────────────────────────┐  │
│  │  Stdio thread — reads JSON, queues onto PDT thread,   │  │
│  │  writes responses                                     │  │
│  └───────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
                             │ in-process
                             ▼
                  PDT business assemblies
                  (MessageService, ErrorBuilder, …)
                  resolved from ServiceLocator
```

## Phased migration

Don't migrate everything at once. Each phase is independently shippable.

### Phase 0 — keep what works
XML reads (`list_*`, `get_*`, `search_*`) stay in Python. They're fast, license-free, and good enough.

### Phase 1 — bootstrap PoC ⭐ biggest risk lives here
Goal: helper starts, container builds, project loads, `MessageService` resolves. No tools yet.
- Get the STA + Dispatcher loop running.
- Get `CliSetup.CreateApplication()` to return successfully (this is where most surprises happen).
- Verify `ServiceLocator.Resolve<T>()` returns a non-null instance for at least one business service.
- Save and re-open the project to confirm round-trip works.

**Stop here and verify before doing anything else.** If this works, the rest is mechanical.

### Phase 2 — `add_custom_error`
Highest-value migration target. Most complex Python implementation, cleanest service mapping.
- Resolve: `ErrorBuilder`, `IErrorBlockBuilder`, `IErrorBlockFactory`, `IDetectionMethodFactory`, `IDetectionMethodTemplateLoader`.
- Map MCP request → service calls.
- The "auto-discover sibling project for ERR block" Python hack disappears.

### Phase 3 — `add_can_message`
- Resolve: `MessageService`, `SignalService`, `CanMessageLifeCycleManager`, `CanMessageNameAndIdGenerator`, `ICanMessageEcuLinkService`.
- Use `SignalService.GetLastFreeStartBit(Guid)` for auto-placement (new feature unlocked by migration).
- "Usage=None must be set in PDT" caveat goes away.

### Phase 4 — DB variable CRUD
- Resolve: `DatabaseListService`, `DatabaseVariableService`, `DatabaseVariableLinkUpdater`, `DatabaseVariableIndexUpdater`, `DatabaseVariableRepositoryQuery`.

### Phase 5 — wrap CLI verbs as MCP tools
No helper changes — just `subprocess.run(["PDT.exe", "build", ...])` from Python.
- `build_project` → `PDT.exe build`
- `check_feature_keys` → `PDT.exe check-feature-key`
- `validate_project` → `PDT.exe open` + watch validation events

### Phase 6 (optional) — explicit validation
Surface `Hydac.PDT.Can.Business.Validation.*` results before save so the LLM can self-correct.

## Verified facts (dnSpyEx, PDT version loaded into dnSpy on the user's machine)

These were `VERIFY:` items in the original plan; confirmed before writing v2 code.

1. **`Hydac.PDT.Cli.Business.Environment.CliInitialization`** — `internal static class`. Methods (`internal static`):
   - `CreateApp(Action<ContainerBuilder> customRegister = null)` — but **passing `null` will leave the container without surrogates** (`IApplicationSettings`, `IConsoleLogger`, `IDispatcherAdapter`, `IMessageBoxAdapter`, `IMainWindow`, `ICloseProjectConfirmationService`, `ICliPathDefinitions`, …). `InitializeApplicationSettings()` will then throw on resolve. Use `CliSetup.CreateApplication()` instead — it registers all of these.
   - `InitializeApplicationSettings()`, `InitializeMain()`, `InitializeProjectConditioner()`, `Dispose()`, `AddMessageBoxProvider()`, `SetResourceResolver()`, `SetAutoCodeBuilderVerboseMode()`.

2. **`Hydac.PDT.Cli.Business.Environment.CliSetup`** — `internal class`. `CreateApplication()` (instance method) wraps the canonical bootstrap:
   ```csharp
   CliInitialization.CreateApp(RegisterCustomDependencies);   // registers ~13 surrogates
   CliInitialization.InitializeApplicationSettings();
   KnowledgeBaseAgentInitialization.InitializeKnowledgeAgentAsync().Wait();
   CliInitialization.InitializeMain();
   CliInitialization.InitializeProjectConditioner();
   ```
   **This is what we reflect-into.**

3. **`Hydac.PDT.PdtFramework.ServiceLocator`** — `public static class`, no reflection needed. Methods: `Resolve<T>()`, `InitContainer(IContainer)`, `DisposeContainer()`, `StartNewProjectScope()`, `CloseProjectScope()`. Internally uses `_projectScope` if set, else `_container`.

4. **`Hydac.PDT.Cli.Business.Environment.ICliProjectCommands`** — `internal interface`. `LoadProjectAsync(FileInfo) → Task<IProject>`, `BuildProjectAsync()`, `CloseProjectAsync()`, `WaitForValidationToEndAsync()`, `HasValidationErrors()`, `UpdateProjectOutputPath`, `UpdateCodeBuilderOutputPath`. Implemented by `CliProjectCommands` which delegates to `TMainVM.LoadProjectCommand.ExecuteAsync` — i.e. **must run on the WPF dispatcher**.

5. **`Hydac.PDT.KnowledgeBase.KnowledgeBaseAgentInitialization`** — lives in `Hydac.PDT.KnowledgeBase.dll` / `Hydac.PDT.KnowledgeBase` namespace (NOT `Hydac.PDT.Business.Knowledgebase` as the original skeleton assumed). Has static `InitializeKnowledgeAgentAsync() : Task`. Called transitively by `CliSetup.InitializeApplication()` — we don't invoke it directly.

6. **`Hydac.PDT.Can.Business.Message.MessageService`** — used as Phase 1 smoke-test target. Verify it resolves; do not call methods.

## Known gotchas

### Internal accessibility
`CliSetup`, `CliInitialization`, `RootInstaller`, `ICliProjectCommands` are all `internal` to `Hydac.PDT.Cli.Business.dll`. Three options for the helper:
1. **Reflection** — pragmatic, brittle on PDT updates. **Used for `CliSetup.CreateApplication()` and `ICliProjectCommands` resolution only.** Everything else uses public surface.
2. **`InternalsVisibleTo`** — requires modifying PDT, not viable.
3. **Replicate the bootstrap** — copy `RootInstaller.CreateAndRegisterIoCContainer` + `CliSetup.RegisterCustomDependencies` logic. More code, fewer surprises across PDT versions.

Option 1 is cheapest and reflects against `internal` types whose names are stable across the 2.12.x line. Switch to option 3 only if reflection becomes painful.

### STA + Dispatcher requirement
`CliInitialization.AddMessageBoxProvider()` calls `Dispatcher.CurrentDispatcher` and stores it in `TObjects`. `CliProjectCommands.LoadProjectAsync` delegates to `TMainVM.LoadProjectCommand.ExecuteAsync`, which requires the calling thread to be the dispatcher thread.

→ The helper's "PDT thread" must be `[STAThread]` and run `Dispatcher.Run()`. All service calls marshalled onto it via `Dispatcher.Invoke` / `InvokeAsync`. Background thread for stdio is fine and necessary (so reading stdin doesn't block the PDT thread).

### `KnowledgeBaseAgentInitialization.InitializeKnowledgeAgentAsync().Wait()`
Synchronous wait inside `CliSetup`. Loads the knowledge base from disk. Can take seconds on cold start. Helper reports "Ready." to the Python server only after the whole bootstrap completes (project loaded). First MCP call may want a long timeout (~30s).

### IntelliLock module cctor
Loading any `Hydac.PDT.*` assembly triggers IntelliLock's runtime init. Side effects:
- The helper must run on a machine with a valid PDT install.
- The helper's directory must contain (or have on PATH) every PDT assembly the container resolves at runtime — which is most of them. Safest: launch the helper with `WorkingDirectory = <PDT install dir>` and reference assemblies from there via csproj `HintPath` with `Private=false`.

### Project save semantics
`ICliProjectCommands.CloseProjectAsync()` calls `MainVm.CloseProjectCommand.ExecuteAsync(...)` followed by `ServiceLocator.CloseProjectScope()`. `IMain.AutoSaveProjects.AutoSaveEnabled = false` is set explicitly by `CliInitialization.InitializeMain()` — meaning we need to explicitly trigger save. The save method is somewhere on `IProject` / `IMain` — to verify in Phase 2.

### Process lifecycle
- Long-lived helper process, one per loaded `.hdb`.
- `server.py` spawns it with `HDB_PATH` and waits for "Ready." line on stdout.
- On `reload_hdb` MCP tool: call `CloseProjectAsync` + `LoadProjectAsync` again on the existing helper, don't restart the process (knowledge base reload is expensive).
- On Python-side shutdown: send `{"method": "shutdown"}`, wait for ack, kill if it doesn't exit.

### What stays in Python
- All read tools (XML is fine).
- `hdb_diff.py` (semantic diff is XML-comparable).
- The MCP protocol layer.
- The auto-discovery of `HDB_PATH` and `PDT_DIR` from `info.xml`.

The helper only handles writes that benefit from going through PDT services.

## Open questions (verify against the live binaries)

1. ~~Is `ServiceLocator` public?~~ **Yes.** Verified.
2. ~~What's the `IProject` save method?~~ **`IProjectAgent.Save(string fullName)`**, resolved via `IProjectService.ActualProjectAgent`. Not on `IProject` at all. Verified end-to-end: HDB round-trips through `Save` and the resulting archive is byte-different (PDT recompresses), with the project still re-openable. Phase 2 plumbing landed; now exposed as RPC method `save_project`.
3. Does `MessageService.Add*` exist, or is creation only via repositories? `MessageService` has setters but the constructor takes `ICanMessageRepository` — creation may be `repository.Add(new CanMessage { ... })`. **TODO Phase 3.**
4. Are there Autofac lifetime scope issues when the helper runs across multiple project loads? `ServiceLocator.CloseProjectScope()` exists — likely needs to be called between project loads. **TODO Phase 1 reload test.**
5. ~~Does `CliApplicationSettings` need a real `IFeatureAgent`?~~ Registered by `Hydac.PDT.FeatureAgent.Installer` inside `RootInstaller`. No action needed.

## Phase 1 — postmortem & surprises

**Phase 1 shipped** on commit ⟨pending⟩. End-to-end smoke test against `Project_Test_changed.hdb` (PDT 2.12.102.19 install):

```
[helper] Container built and bootstrapped (23658 ms)
[helper] Project loaded (20314 ms)
[helper] Smoke test: Resolved Hydac.PDT.Can.Business.Message.MessageService
Ready.
{"jsonrpc":"2.0","id":1,"result":"pong"}
{"jsonrpc":"2.0","id":2,"result":"ok"}
[helper] Dispatcher exited
```

Things the original plan got wrong, in order of how much pain they caused:

1. **`KnowledgeBaseAgentInitialization` lives in `Hydac.PDT.KnowledgeBase.dll` / `Hydac.PDT.KnowledgeBase` namespace, NOT `Hydac.PDT.Business.Knowledgebase`.** Doesn't matter once you switch to `CliSetup.CreateApplication()` (which calls it transitively).

2. **`CliInitialization.CreateApp(null)` is not enough.** The CLI's `CliSetup.RegisterCustomDependencies` registers thirteen surrogates (`IConsoleLogger`, `IDispatcherAdapter`, `IApplicationSettings` factory, `IMessageBoxAdapter`, `IMainWindow`, `ICloseProjectConfirmationService`, `ICliPathDefinitions`, `IDialogServiceAdapter`, `IBackgroundTaskIsRunningService`, `IDefaultTreeWoodsMan`, `ILogLevelConverter`, `IReactiveScheduler`, `IResourceResolver`) that the container needs. Reflectively invoking `new CliSetup().CreateApplication()` is the right entry — it bundles all of that.

3. **`ServiceLocator.Resolve<T>()` dispatches to `_projectScope` when one is open**, so concrete classes registered as `As<IFoo>()` only resolve via `IFoo`. Phase 1's smoke test originally tried `ServiceLocator.Resolve<MessageService>()` and got `ComponentNotRegisteredException`. Fixed by resolving `Hydac.PDT.Can.Business.Contracts.Message.IMessageService` instead.

4. **`Assembly.Load("Hydac.PDT.Cli.Business")` only probes the helper's own bin dir.** Setting the launcher's `WorkingDirectory` to the PDT install isn't enough on its own — you also need an `AppDomain.AssemblyResolve` handler that does `Assembly.LoadFrom(Path.Combine(Environment.CurrentDirectory, name + ".dll"))`. Without it, fusion fails before our code can react. Helper now hooks `AssemblyResolve` in `Main()` before any `Hydac.PDT.*` is touched.

5. **PDT refuses to load any project where `project.PdtVersionString > Assembly.GetEntryAssembly().Version`.** The check is `DataLayerFactory.CheckForNewerProjectVersion`, comparing `IApplicationVersionDetails.CurrentApplicationVersion` (derived from the entry assembly) to the project's stored version. As `Match_PDT_Helper_v2.exe v1.0.0.0`, every modern project failed with "saved with a newer software version of PDT" — even when the loaded PDT binaries matched exactly. Pinning `<AssemblyVersion>99.99.99.0</AssemblyVersion>` makes the upper check pass; the resulting "older project" branch (`CheckForOlderProjectVersion`) loads the project anyway, just attaching a non-fatal warning. Phase 2 should consider whether to override `IApplicationVersionDetails` via the customRegister callback to match the loaded PDT exactly, so saves don't bump the project's stored version up to 99.99.99.

6. **Bootstrap deadlocks if you run it from inside a `Dispatcher.BeginInvoke` callback while `Dispatcher.Run()` is active.** `KnowledgeBaseAgentInitialization.InitializeKnowledgeAgentAsync().Wait()` and `LoadProjectCommand.ExecuteAsync().Wait()` capture the dispatcher's `SynchronizationContext` for their continuations, and we're already blocking the dispatcher with our synchronous callback → classic sync-over-async deadlock. The PDT CLI doesn't run a dispatcher loop at all — it just blocks the main thread. Helper does the same: bootstrap synchronously on the STA thread, *then* start `Dispatcher.Run()` for the RPC loop. `DispatcherRunner` was deleted because it was misleading; logic moved into `Program.cs`.

7. **The PDT install dir picked at build time must match the project's PDT version**, because `<HintPath>` references DLLs by name and the AppDomain.AssemblyResolve hook resolves at runtime from `Environment.CurrentDirectory`. The MSBuild `PickLatestPdtVersion` task picks the newest installed; for older projects, override at run time (`Start-Process -WorkingDirectory ...\2.12.100`) and trust the AssemblyResolve hook to load matching binaries. (Long-term: `server.py` already resolves the right PDT for a given `.hdb` via `_resolve_pdt_dir`; the spawn logic feeds that through as `WorkingDirectory`.)

## Known cleanup left for Phase 2

- The `ConsoleWriterDialogServiceAdapter` registered by `CliSetup.RegisterCustomDependencies` writes dialog text to `Console.Out`, which is our JSON-RPC channel. server.py needs to drain stdout until the literal "Ready." line. Cleaner: register our own `IDialogServiceAdapter` via the customRegister callback that logs to stderr instead.
- AssemblyVersion=99.99.99.0 will leak into project saves' `PdtVersionString`. Override `IApplicationVersionDetails` in Phase 2 (read the loaded `Hydac.PDT.PdtFramework.dll`'s file version and report that).

## Phase 2 — `add_custom_error` ships in v2

**Status: end-to-end verified. The verb `add_custom_error` is un-gated; server.py can stop falling back to v1 once it's switched over.**

Verified flow on `Project_Test_changed.hdb` (clean copy):
- helper #1: `add_custom_error{ dm_name: "DM_PHASE2_FINAL", spn: 95000, … }` → `{status: ok, dm_guid, object_id}`. `Errors.dat` written with 76 errors (75 existing + 1 new).
- helper #2 (fresh process): `add_custom_error{ dm_name: "DM_PHASE2_FINAL", spn: 95001, … }` → rejected with `ArgumentException: DM name 'DM_PHASE2_FINAL' already exists.` Proves the new error round-tripped through save + reload and is visible in `IErrorCollection`.

### Save fix (this commit)

Three issues had to be solved before save+reload could even round-trip:

1. **`Action<ContainerBuilder>` type-identity**: PDT ships its own `Autofac.dll` and loads it at runtime. We were compiling against the NuGet `Autofac` package, so any callback we handed to `CliInitialization.CreateApp` failed at the reflection boundary with *"Impossible de convertir l'objet de type 'Action<ContainerBuilder>' en type 'Action<ContainerBuilder>'"*. Fix: drop the package, reference PDT's `Autofac.dll` directly with `Private=false`.
2. **Version checks at load**: `IApplicationVersionDetails.CurrentApplicationVersion` is read from the entry assembly's version. As `Match_PDT_Helper_v2.exe v1.0.0.0` we triggered `CheckForOlderProjectVersion`'s "older than the helper" warning at load. Fix: replicate `CliSetup.CreateApplication` manually so we can wedge our own `customRegister` into `CliInitialization.CreateApp`. The wedge calls `CliSetup.RegisterCustomDependencies` reflectively for PDT's 13 surrogates, then registers our `HostApplicationVersionDetails` last (Autofac's last-registration-wins). `Delegate.CreateDelegate` doesn't bind to internal methods, so the wrapper invokes via reflection.
3. **Save drops files / wrong version stamped**: `IProjectAgent.Save` recreates the .hdb from scratch and only writes back sub-systems that were loaded into memory. On a headless run, sub-managers like `Errors`, `KnowledgebaseAgent`, `CanMessages` lazy-load when their VMs are accessed in the UI — which never happens. Result: 16 of 32 entries silently disappear from the saved archive. **AND** `DataLayer.AddInfoFile` reads `Assembly.GetEntryAssembly().GetName().Version` directly (bypassing `IApplicationVersionDetails`), so info.xml's `PdtVersionString` becomes our `1.0.0.0`. Fixes:
   - **Snapshot** the pristine .hdb at load (copy to `%TEMP%`).
   - **Merge** at save: copy any entry from the snapshot that PDT didn't write back. Stale-but-valid is better than missing.
   - **Patch** info.xml's `PdtVersionString` to the real loaded PDT version.

Verified: clean Project_Test_changed.hdb (1.40 MB) → save_project → 1.61 MB → reload via the same helper succeeds (ping/pong, no exceptions). 16 carry-forward entries logged. info.xml correctly stamped 2.12.102.19.

### What persists

- **DM** created via `ITRepository.NewDetectionMethod` lives in `project.dat` / `Repository.DetectionMethods` — PDT writes project.dat back. After reload, `IDetectionMethodLoader.GetByDefinition` finds it.
- **Error** created via `IErrorBuilder.CreateError` + `IProjectErrorFactory.AddToProject` lives in `Errors.dat` — which we carry forward from the pristine snapshot, so the **mutation is lost on save**. The DM exists post-reload; its error doesn't.

### Errors persistence (resolved)

`ProjectAgent.Save` doesn't emit `Errors.dat` because no errors-persistence adapter is registered for headless flows. But the project's existing 75 errors auto-populate into `IErrorCollection` during `CliInitialization.InitializeMain` via the LoadErrorsFromRepository fallback. So we don't need to load them — but we do need to write them.

Solution (in `HostBootstrap.WriteErrorsDat`): after `IProjectAgent.Save`, resolve `ISaveErrorsToDataLayer` and call `Save(IErrorCollection)`. That serializes the in-memory error list (existing + our newly-added) to `Errors.dat` inside the saved .hdb via BinaryFormatter. Runs BEFORE the snapshot-merge step so the merge sees `Errors.dat` already in place and skips it.

The other lazy-loaded sub-systems (`HymlEcuTemplates.xml`, `CanMessages.xml`, `DatabaseLists.xml`, …) still rely on the snapshot carry-forward — their persistence pipelines will need similar wiring per phase (CAN messages in Phase 3, DB variables in Phase 4).

### Other dup-check fix

`IDetectionMethod` (the project-level model) exposes `Name`, not `Detection`. v1's CustomErrorAdd checked `TDetectionMethodTemplate.Detection` because that lived on a different layer (project.dat's customDMs collection). The v2 dup check now reads `dm.Name`.



What works in `add_custom_error_experimental`:
- v1 JSON contract (`template`, `dm_name`, `bit`, `spn`, `block_name`, `description`, `severity`, `fmi`, `fmi_extended`, `set/release_debounce_ms`, `set/release_threshold`) parsed and validated.
- ERR block resolved by name via `IBlockRepository.GetByType("ERR")` then by `Name` match.
- SPN + DM-name uniqueness verified against `IErrorCollection`.
- TDetectionMethod created via `ITRepository.NewDetectionMethod(ITBlock, IHymlError)` (atomic: creates, links to block, registers in `Repository.DetectionMethods`).
- Synthetic `IHymlError` (helper-internal `HymlErrorImpl`) carries the FMI/debounce values.
- Error created via `IErrorBuilder.CreateError(Guid ownerId, uint spn, IHymlError, IDetectionMethod)` (private; invoked reflectively to bypass the redundant DM resolution in `CreateAndAddBuildingBlockError`).
- Error added to project via `IProjectErrorFactory.AddToProject`.
- Threshold/Description/Severity overrides applied post-creation.
- `IProjectAgent.Save(string)` called; the resulting HDB is written without exception.
- v1-shaped success result returned: `{status, message, spn, dm_name, dm_guid, template, block_name, object_id, new_block}`.

What's broken: re-loading the saved HDB fails with `"The given key was not present in the dictionary"` from `CliProjectCommands.LoadProjectAsync` → `MainVm.LoadProjectCommand`. The save itself doesn't throw, but some referenced-by-key state (likely a missing `Idx` on TDetectionMethod, a missing `IDetectionMethodTemplate` entry, or an unfilled FmiExt link) isn't filled in by `IErrorBuilder.CreateError` for the custom path. v1's CustomErrorAdd explicitly populated more fields (Idx, LinkedBlockIds, PinType, Type, GUID on the template object) — those still need to be reconciled with the new-world API surface.

Resolved facts:
- Concrete TDetectionMethod type used: `Hydac.PDT.ViewModel.TDetectionMethod`.
- `IDetectionMethodFactory.Create(IHymlError)` returns a free-floating instance — does NOT register it into the project. Use `ITRepository.NewDetectionMethod(ITBlock, IHymlError)` instead.
- `IDetectionMethodLoader.GetByDefinition` queries `_projectAgent.Repository.DetectionMethods` filtered by `BlockObjectId` then by `Name` (exact, case-sensitive). The DM's `Name` must be set explicitly after `NewDetectionMethod` because the factory uses `IHymlError.Detection`, not `Name`.
- `IDtc.Fmi` / `IDtc.FmiEx` are `IFailureModeIdentifier` references, not byte. `DtcFactory.Create(spn, byte, byte)` resolves the bytes into the right instances; don't try to override post-creation as bytes.
- `ITRepository` uses explicit interface implementation for several methods (incl. `NewDetectionMethod`) — `repo.GetType().GetMethods()` misses them; reflect off the interface type instead.

## Phase 2 — foundation in place, mapping pending

What's landed (this commit):
- `HostBootstrap.SaveProject()` — resolves `IProjectService.ActualProjectAgent` and invokes `Save(string)` with the original `.hdb` path. Verified: hdb round-trips, archive recompresses to a different byte stream, project re-opens cleanly.
- `RpcLoop` dispatches `save_project` → returns `{"saved": "<absolute path>"}`.
- `dotnet-helper-v2/Services/ErrorService.cs` — skeleton handler. `add_custom_error` returns `MethodNotFound` until the mapping is implemented; server.py keeps using v1 in the meantime.
- csproj now references `Hydac.PDT.Errors.Business` and `Hydac.PDT.Errors.Business.Contracts` — ready for the mapping work.

What's pending for the actual `add_custom_error` mapping:
- `IErrorBuilder.CreateError(Guid blockId, uint bit, IHymlError, IDetectionMethod)` — needs `IHymlError` from the knowledge base (FMI templates) and `IDetectionMethod` constructed via `IDetectionMethodFactory`.
- `IErrorBlockFactory.Create` for the "create new ERR block" path — replaces v1's sibling-project clone hack.
- `IDetectionMethodTemplateLoader` to find/create template entries.
- v1 contract to preserve (matches Python `add_custom_error` signature):
  - in: `template`, `dm_name`, `bit`, `spn`, `block_name`, `description`, `severity`, `fmi`, `fmi_extended`, `set_debounce_ms`, `release_debounce_ms`, `set_threshold`, `release_threshold`
  - out: `{status, message, spn, dm_name, dm_guid, template, block_name, object_id, new_block}`
- Save semantics: helper should call `SaveProject` after a successful mutation; the v2 server.py wiring will assume single-shot "mutate + save" rather than multiple staged mutations.

## Success criteria for Phase 1

```
$ dotnet run --project dotnet-helper-v2 -- C:\path\to\project.hdb
[helper] STA dispatcher running
[helper] Calling CliSetup.CreateApplication...
[helper] Container built and bootstrapped (xxxx ms)
[helper] Project loaded (xxxx ms)
[helper] Smoke test: Resolved Hydac.PDT.Can.Business.Message.MessageService
Ready.
> {"jsonrpc":"2.0","id":1,"method":"ping"}
< {"jsonrpc":"2.0","id":1,"result":"pong"}
> {"jsonrpc":"2.0","id":2,"method":"shutdown"}
< {"jsonrpc":"2.0","id":2,"result":"ok"}
$
```

If you can get to "Ready." and the ping round-trip, **the architectural risk is gone**. Everything after is filling in service handlers.
