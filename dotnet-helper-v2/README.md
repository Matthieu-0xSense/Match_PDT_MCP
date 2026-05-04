# dotnet-helper-v2

Long-lived headless PDT host. Boots `Hydac.PDT.Cli.Business.CliSetup` on an STA dispatcher thread, then exposes PDT business services over stdio JSON-RPC to the Python MCP server.

See `../MIGRATION_PLAN.md` for full context, architecture, and phased plan.

## Layout

```
dotnet-helper-v2/
├── README.md
├── Match_PDT_Helper_v2.csproj   references PDT assemblies, WPF, Autofac
├── Program.cs                    [STAThread] entry, wires up dispatcher + bootstrap + RPC loop
├── Bootstrap/
│   └── HostBootstrap.cs          reflection wrapper around CliSetup.CreateApplication + project load
├── Rpc/
│   ├── DispatcherRunner.cs       STA thread + Dispatcher.Run, marshals work onto it
│   ├── RpcLoop.cs                stdio JSON-RPC reader/writer
│   └── Messages.cs               request/response types
└── Services/
    └── (handlers added per phase — empty for Phase 1)
```

## Build

PDT install path is auto-detected at build time:
1. `$(PdtInstallDir)` MSBuild property if set.
2. `$(PDT_DIR)` env var if set.
3. The highest version directory found under `C:\Program Files\Hydac\Project Definition Tool\`.

Override with:

```powershell
dotnet build .\dotnet-helper-v2\Match_PDT_Helper_v2.csproj `
    /p:PdtInstallDir="C:\Program Files\Hydac\Project Definition Tool\2.12.102"
```

## Run (Phase 1 smoke test)

The compiled helper must run with `WorkingDirectory = <PDT install dir>` so AppDomain.AssemblyResolve finds the rest of the PDT runtime. Easiest:

```powershell
$pdt = "C:\Program Files\Hydac\Project Definition Tool\2.12.102"
$exe = "$PSScriptRoot\dotnet-helper-v2\bin\Debug\net48\Match_PDT_Helper_v2.exe"
Start-Process -FilePath $exe `
    -ArgumentList "C:\Match\Projects\Test\Test.hdb" `
    -WorkingDirectory $pdt `
    -NoNewWindow -Wait
```

Or run interactively from the PDT directory:

```powershell
cd "C:\Program Files\Hydac\Project Definition Tool\2.12.102"
& "C:\Match\Tools\MCP\Match_PDT_MCP\dotnet-helper-v2\bin\Debug\net48\Match_PDT_Helper_v2.exe" `
    "C:\Match\Projects\Test\Test.hdb"
```

Phase 1 success means you see:

```
[helper] STA dispatcher running
[helper] Calling CliSetup.CreateApplication...
[helper] Container built and bootstrapped (xxxx ms)
[helper] Project loaded (xxxx ms)
[helper] Smoke test: Resolved Hydac.PDT.Can.Business.Message.MessageService
Ready.
```

Then ping it via stdin:

```
{"jsonrpc":"2.0","id":1,"method":"ping"}
{"jsonrpc":"2.0","id":1,"result":"pong"}
{"jsonrpc":"2.0","id":2,"method":"shutdown"}
{"jsonrpc":"2.0","id":2,"result":"ok"}
```

If you get the `Ready.` line and the ping round-trip, ship Phase 1 and stop.

## What's intentionally missing in Phase 1

- **Service handlers** (`ErrorService`, `CanMessageService`, etc.) — added per phase once Phase 1 is proven.
- **Project save logic.** `MIGRATION_PLAN.md` flags this as an open question for Phase 2.
- **Python-side wiring.** `server.py` still uses the v1 `dotnet-helper`. v2 lives alongside until proven.
