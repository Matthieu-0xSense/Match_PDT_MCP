# MATCH PDT MCP Server

MCP server for HYDAC PDT `.hdb` project files. Queries and modifies CAN messages, signals, error definitions, and ECU configuration.

## How it works

An `.hdb` file is a ZIP archive containing XML configuration files and binary `.dat` files. The server:

1. Parses XML files (strips null padding) into indexed lookup dicts
2. Deserializes `.dat` files via a .NET helper using the PDT assemblies
3. Serves read/write queries via MCP stdio transport (lazy-loaded, cached)
4. For writes: creates `.hdb.bak` backup, modifies XML in-place, rewrites ZIP

## Why use this instead of reading generated code?

The PDT generates C code in `AUTO_CEN_*` folders — structs, enums, config arrays. For **CAN messages, signals, and error definitions**, the MCP server is faster and more convenient. For other areas (pins, blocks), the generated code remains the better source.

**What this does well:**
- **CAN signal lookup** — scaling formula, bit position, units, and parent message in one query. In generated code this is scattered across `App_CanSigRec.c`, `Cfg_CRcv.c`, `Cfg_CSnd.c`.
- **Signal search** — find signals by name substring across all messages, optionally filtered by message.
- **Message details** — CAN ID, DLC, cycle time, direction, and all signals linked together. In generated code you'd cross-reference multiple files.
- **Error definitions** — SPN, description, severity, debounce times, and thresholds. In generated code this is split between `App_ErrDefine.h` and `Cfg_Err.c` without the PDT metadata.
- **XML modification** — edit CAN messages, signals, databases, and other XML-based config directly.
- **Always current** — reads the `.hdb` directly, so it reflects the latest PDT save even if code hasn't been regenerated yet.

**What still requires generated code:**
- Pin configurations (`project.dat` uses complex .NET types that can't be loaded outside .NET Framework 4.8)
- Software blocks and detailed parameter entries within databases

**What cannot be modified (binary .dat files):**
- Error definitions (`Errors.dat`)
- ISOBUS config (`Isobus.dat`)
- Project data (`project.dat`)

## Installation

### Prerequisites

**Required:**
- Python 3.10+ with `mcp` package

**Optional (for error tools only):**
- .NET SDK 8.0+
- HYDAC PDT installed (provides the .NET assemblies for `.dat` deserialization)

Without .NET SDK, all tools work except `list_errors` and `get_error` — they return an error message instead of crashing the server.

```bash
pip install "mcp[cli]"
```

### Build the .NET helper (optional)

Skip this step if you don't need error definition tools (`list_errors`, `get_error`).

```bash
cd Match_PDT_MCP/dotnet-helper
dotnet build -c Release
```

### Register with Claude Code (per project)

Add a `.mcp.json` file in the project root directory:

```json
{
  "mcpServers": {
    "Match_PDT_MCP": {
      "type": "stdio",
      "command": "python",
      "args": [
        "C:/Match/Tools/MCP/Match_PDT_MCP/server.py"
      ]
    }
  }
}
```

The server auto-discovers:
- **HDB_PATH** — first `*.hdb` file in the working directory
- **PDT_DIR** — latest version in `C:\Program Files\Hydac\Project Definition Tool\`

If the project has multiple `.hdb` files or a non-standard location, set environment variables explicitly:

```json
{
  "mcpServers": {
    "Match_PDT_MCP": {
      "type": "stdio",
      "command": "python",
      "args": ["C:/Match/Tools/MCP/Match_PDT_MCP/server.py"],
      "env": {
        "HDB_PATH": "C:/Match/Projects/MyProject/SpecificFile.hdb",
        "PDT_DIR": "C:/Program Files/Hydac/Project Definition Tool/2.12.100"
      }
    }
  }
}
```

The same `.mcp.json` works for any MATCH project — just drop it in the project root and Claude Code will start the server automatically.

## Available Tools

### CAN Read Tools (from XML)

| Tool | Parameters | Description |
|------|-----------|-------------|
| `get_can_message` | `name: str`, `can_id: int` | Look up message by name (case-insensitive, substring match) or CAN ID (decimal). Returns details + all signals + direction. |
| `list_can_messages` | `direction: str`, `name_filter: str` | List all messages. Filter by direction (`send`/`receive`) or name substring. |
| `get_can_signal` | `name: str`, `message: str` | Look up signal by name (case-insensitive). Returns scaling, bits, units, parent message. Use `message` to disambiguate duplicates. |
| `search_can_signals` | `query: str`, `message: str` | Search signals by name substring. Optionally restrict to signals in a specific message. |

### CAN Write Tools

| Tool | Parameters | Description |
|------|-----------|-------------|
| `add_can_message` | `name`, `can_id`, `direction`, `dlc`, `cycle_time`, `signals` | Add a complete CAN message with signals. Creates entries in `CanMessages.xml`, `CanMessageEcuLinks.xml`, and `CanSignals.xml`. Creates `.hdb.bak` backup before the first write. |

**`add_can_message` details:**
- `name` (required): Message name, e.g. `"VcuSendTestData"`
- `can_id` (required): CAN ID as decimal. Extended frame if > 0x7FF
- `direction`: `"SendCyclically"` (default), `"SendEventBased"`, or `"Receive"`
- `dlc`: Data Length Code, 0-8 (default 8)
- `cycle_time`: Cycle time in ms (default 100)
- `signals`: Comma-separated signal definitions as `name:startbit:sizebits`

**Example:**
```
add_can_message(
    name="VcuSendTestData",
    can_id=419365500,
    direction="SendCyclically",
    dlc=8,
    cycle_time=100,
    signals="testValue:0:16,status:16:8,mode:24:4"
)
```

This creates:
- The message definition (CAN ID 0x18FF017C, Extended, Intel byte order)
- ECU link with send buffer assignment
- 3 signals: `testValue` (16-bit at bit 0), `status` (8-bit at bit 16), `mode` (4-bit at bit 24)

### Database & ECU Tools (from XML)

| Tool | Parameters | Description |
|------|-----------|-------------|
| `list_databases` | *(none)* | List NvMem/RAM parameter databases with addresses and settings. |
| `get_ecu_config` | *(none)* | ECU app config: cycle time, watchdog, protocols, protocol parameters, project info. |

### Error Tools (from .dat via .NET helper — requires .NET SDK 8.0+ and HYDAC PDT)

These tools are optional. Without .NET SDK, they return an error message; all other tools remain functional.

| Tool | Parameters | Description |
|------|-----------|-------------|
| `list_errors` | `spn_filter: int`, `description_filter: str` | List error definitions with SPN, description, severity, debounce, thresholds. |
| `get_error` | `spn: int` | Look up a specific error by SPN number. Returns full details. |

### XML Write Tools

| Tool | Parameters | Description |
|------|-----------|-------------|
| `list_hdb_xml_files` | *(none)* | List all XML files in the HDB archive with sizes. |
| `read_hdb_xml` | `file: str`, `xpath: str` | Read raw XML content, optionally filtered by XPath. |
| `update_hdb_xml` | `file`, `xpath`, `action`, `tag`, `text`, `attributes` | Modify XML elements: `set_text`, `set_attr`, `add_child`, `remove`. Creates `.hdb.bak` backup. |

### Utility Tools

| Tool | Parameters | Description |
|------|-----------|-------------|
| `search_hdb` | `pattern: str` | Regex search across all XML content in the HDB archive (max 100 results). |
| `reload_hdb` | *(none)* | Force re-parse of all data after saving in PDT. |

## HDB Archive Structure

### XML Files (read/write via Python)

| File | Content |
|------|---------|
| `CanMessages.xml` | CAN message definitions (ID, DLC, cycle time, byte order) |
| `CanSignals.xml` | Signal definitions (bit position, scaling, units, min/max) |
| `CanMessageEcuLinks.xml` | Send/receive direction per message |
| `DatabaseLists.xml` | NvMem/RAM database layouts |
| `EcuApplications.xml` | ECU config (cycle time, watchdog) |
| `Protocols.xml` | Protocol instances (MST, ISO-Bus) |
| `ProtocolParameters.xml` | Protocol settings (source addresses, buffer config) |
| `PinEcuApplicationLinks.xml` | I/O pin mappings (GUIDs only) |
| `info.xml` | PDT version and file format |

### Binary .dat Files (read-only via .NET helper)

| File | Status | Content |
|------|--------|---------|
| `Errors.dat` | Read-only | Error definitions (SPN, severity, thresholds, reactions) |
| `CompileConfig.dat` | Read-only | Build mode, log level, flags |
| `Isobus.dat` | Read-only | ISOBUS configuration |
| `project.dat` | Not supported | Main project data (pins, blocks) — uses complex WPF types |

## Limitations

- **`.dat` files are read-only** — BinaryFormatter serialization with proprietary PDT types prevents write-back.
- **`project.dat`** uses .NET Framework 4.8 WPF types that can't be loaded in .NET 8. Pin names and software blocks are not available.
- **Pin name resolution** requires cross-referencing GUIDs through `project.dat`, so pin info shows only GUIDs.
- **Error GUID fields** (Fmi, DetectionMethod, MachineFunction, RestrictedMode) reference objects in `project.dat` and can't be resolved to names.
- **Signal name duplicates** — many signals share the same name across different messages. Use the `message` parameter in `get_can_signal` to disambiguate.

## Dependencies

**Required:**
- Python 3.10+
- `mcp` package (`pip install "mcp[cli]"`)

**Optional (error tools only):**
- .NET SDK 8.0+
- HYDAC PDT installation (provides the .NET assemblies)
