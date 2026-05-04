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
2. What's the `IProject` save method? `SaveAsync`? Via `IMain`? **TODO Phase 2.**
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
