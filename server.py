"""MCP Server for HYDAC PDT .hdb project files.

An .hdb file is a ZIP archive containing null-padded XML configuration files.
This server parses them into indexed lookup dicts and exposes query/modify tools.
"""

import os
import re
import uuid
import xml.etree.ElementTree as ET
import zipfile

from mcp.server.fastmcp import FastMCP

from parser import (
    HDB_PATH, PDT_DIR, _resolve_pdt_dir,
    get_cache, clear_cache, get_errors,
    get_detection_methods as _get_detection_methods,
    get_fmi_definitions as _get_fmi_definitions,
    get_error_templates as _get_error_templates,
    read_xml_from_hdb, write_xml_to_hdb,
    list_db_variables as _list_db_variables,
    get_db_variable as _get_db_variable,
    add_db_variable as _add_db_variable,
    update_db_variable as _update_db_variable,
    delete_db_variable as _delete_db_variable,
    add_custom_error as _add_custom_error,
)
from formatters import fmt_can_id, fmt_message, fmt_signal

mcp = FastMCP(
    "Match_PDT_MCP",
    instructions=(
        "Query CAN messages, signals, parameters, and ECU config "
        "from a HYDAC PDT .hdb project file. "
        "Also manage database variables (list, get, add, update, delete)."
    ),
)


# ---------------------------------------------------------------------------
# MCP Tools — CAN
# ---------------------------------------------------------------------------

@mcp.tool()
def get_can_message(name: str = "", can_id: int = 0) -> str:
    """Look up a CAN message by name or CAN ID.

    Returns message details including all signals, direction, timing.
    Provide either name (case-insensitive) or can_id (decimal).
    """
    data = get_cache()

    if name:
        msg = data["messages_by_name"].get(name.lower())
        if not msg:
            matches = [m for k, m in data["messages_by_name"].items() if name.lower() in k]
            if len(matches) == 1:
                msg = matches[0]
            elif matches:
                return (
                    f"Multiple messages match '{name}':\n"
                    + "\n".join(f"  - {m['name']}" for m in matches)
                    + "\n\nPlease use the exact name."
                )
            else:
                return f"No message found matching '{name}'."
    elif can_id:
        msg = data["messages_by_canid"].get(can_id)
        if not msg:
            return f"No message found with CAN ID {can_id} (0x{can_id:X})."
    else:
        return "Provide either 'name' or 'can_id'."

    return fmt_message(msg)


@mcp.tool()
def list_can_messages(direction: str = "", name_filter: str = "", bus: int = 0) -> str:
    """List all CAN messages, optionally filtered.

    Args:
        direction: Filter by direction — 'send', 'receive', or '' for all.
                   Matches 'Receive', 'SendCyclically', 'SendOnEvent'.
        name_filter: Filter by name substring (case-insensitive).
        bus: Filter by bus number (1-indexed). 0 means all buses.
    """
    data = get_cache()
    messages = list(data["messages_by_id"].values())

    if bus > 0:
        buses = data.get("buses", [])
        if bus <= len(buses):
            bus_id = buses[bus - 1]["bus_id"]
            messages = [m for m in messages if m["bus_id"] == bus_id]
        else:
            return f"Invalid bus number {bus}. Project has {len(buses)} bus(es)."

    if direction:
        d = direction.lower()
        messages = [m for m in messages if d in m["direction"].lower()]

    if name_filter:
        nf = name_filter.lower()
        messages = [m for m in messages if nf in m["name"].lower()]

    if not messages:
        return "No messages match the filter."

    lines = [f"**CAN Messages ({len(messages)})**\n"]
    for msg in sorted(messages, key=lambda m: m["name"]):
        sig_count = len(msg["signals"])
        lines.append(
            f"  {msg['name']:40s}  "
            f"{fmt_can_id(msg['can_id'], msg['message_type']):>12s}  "
            f"DLC={msg['dlc']}  "
            f"{msg['cycle_time']:>4d}ms  "
            f"{msg['direction']:20s}  "
            f"({sig_count} signals)"
        )
    return "\n".join(lines)


@mcp.tool()
def get_can_signal(name: str, message: str = "") -> str:
    """Look up a CAN signal by name (case-insensitive).

    Returns scaling formula, bit position, units, and parent message.
    Many signals share the same name across messages — use 'message' to disambiguate.

    Args:
        name: Signal name (exact or substring).
        message: Optional message name to disambiguate duplicate signal names.
    """
    data = get_cache()
    sigs = data["signals_by_name"].get(name.lower())

    if not sigs:
        sigs = []
        for k, sig_list in data["signals_by_name"].items():
            if name.lower() in k:
                sigs.extend(sig_list)

    if not sigs:
        return f"No signal found matching '{name}'."

    if message:
        msg_lower = message.lower()
        sigs = [s for s in sigs if msg_lower in data["messages_by_id"].get(s["owner_id"], {}).get("name", "").lower()]
        if not sigs:
            return f"No signal '{name}' found in message '{message}'."

    parts = []
    for sig in sigs:
        parent = data["messages_by_id"].get(sig["owner_id"])
        msg_name = parent["name"] if parent else "?"
        parts.append(fmt_signal(sig, msg_name))

    return "\n\n".join(parts)


@mcp.tool()
def search_can_signals(query: str, message: str = "") -> str:
    """Search CAN signals by name substring.

    Args:
        query: Substring to search for in signal names (case-insensitive).
        message: Optionally restrict to signals in this message name.
    """
    data = get_cache()
    q = query.lower()

    results = []
    for sig in data["signals_by_id"].values():
        if q not in sig["name"].lower():
            continue
        if message:
            parent = data["messages_by_id"].get(sig["owner_id"])
            if not parent or message.lower() not in parent["name"].lower():
                continue
        results.append(sig)

    if not results:
        return f"No signals matching '{query}'" + (f" in message '{message}'" if message else "") + "."

    lines = [f"**Signals matching '{query}' ({len(results)} results)**\n"]
    for sig in sorted(results, key=lambda s: s["name"]):
        parent = data["messages_by_id"].get(sig["owner_id"])
        msg_name = parent["name"] if parent else "?"
        scaling = ""
        if sig["multiplier"] != 1 or sig["divisor"] != 1 or sig["offset"] != 0:
            scaling = f"  = (raw*{sig['multiplier']:.6g}+{sig['offset']:.6g})/{sig['divisor']:.6g}"
        lines.append(
            f"  {sig['name']:30s}  [{sig['start_bit']}:{sig['start_bit']+sig['size_bits']-1}]"
            f"  {sig['unit'] or sig['raw_unit']:8s}"
            f"  in {msg_name}{scaling}"
        )
    return "\n".join(lines)


# ---------------------------------------------------------------------------
# MCP Tools — Databases & ECU Config
# ---------------------------------------------------------------------------

@mcp.tool()
def list_databases() -> str:
    """List all NvMem and RAM parameter databases with addresses and settings."""
    data = get_cache()

    if not data["databases"]:
        return "No databases found."

    type_names = {1: "RAM", 2: "NvMem"}
    mode_names = {0: "Standard", 1: "WithBackup"}

    lines = [f"**Parameter Databases ({len(data['databases'])})**\n"]
    for db in sorted(data["databases"], key=lambda d: d["name"]):
        t = type_names.get(db["list_type"], f"Type{db['list_type']}")
        m = mode_names.get(db["list_mode"], f"Mode{db['list_mode']}")
        backup = f"  backup=0x{db['backup_start_address']:04X}" if db["backup_start_address"] else ""
        crc = []
        if db["nv_crc_protected"]:
            crc.append("NV-CRC")
        if db["ram_crc_protected"]:
            crc.append("RAM-CRC")
        crc_str = f"  [{', '.join(crc)}]" if crc else ""

        lines.append(
            f"  {db['name']:30s}  {t:6s}  addr=0x{db['start_address']:04X}{backup}"
            f"  {m}  datasets={db['default_data_set_count']}{crc_str}"
        )
    return "\n".join(lines)


@mcp.tool()
def get_ecu_config() -> str:
    """Get ECU application config: cycle time, watchdog, protocols, project info."""
    data = get_cache()

    lines = []

    # Project info
    if data["info"]:
        lines.append("**Project Info**")
        lines.append(f"  PDT Version: {data['info'].get('pdt_version', '?')}")
        lines.append(f"  File Format: {data['info'].get('file_format', '?')}")
        lines.append(f"  HDB Path: {HDB_PATH}")
        lines.append("")

    # ECU Applications
    if data["ecu_apps"]:
        lines.append("**ECU Applications**")
        for app in data["ecu_apps"]:
            lines.append(f"  {app['name']}")
            lines.append(f"    Cycle: {app['cycle_time']} ms  |  Execution: {app['execution_time']} ms  |  Offset: {app['offset_time']} ms")
            lines.append(f"    Watchdog: {app['watchdog_time']} ms  (reaction={app['watchdog_reaction']})")
            lines.append(f"    Priority: {app['task_priority']}  |  Safety Level: {app['safety_level']}")
            flags = []
            if app["is_supervisor"]:
                flags.append("Supervisor")
            if app["is_diagnosis"]:
                flags.append("Diagnosis")
            if flags:
                lines.append(f"    Flags: {', '.join(flags)}")
            lines.append(f"    Flash: start=0x{app['flash_start']:X}  size={app['flash_size']} KB")
        lines.append("")

    # Protocols
    if data["protocols"]:
        lines.append("**Protocols**")
        for ptc in data["protocols"]:
            status = "enabled" if ptc["enabled"] else "disabled"
            lines.append(f"  {ptc['name']}  ({ptc['type']})")
            lines.append(f"    Version: {ptc['protocol_version']}  |  MATCH: {ptc['match_version']}  |  {status}")
            lines.append(f"    ECU: {ptc['ecu_code']}")

            params = data["protocol_params"].get(ptc["guid"], [])
            if params:
                lines.append(f"    Parameters ({len(params)}):")
                for p in params:
                    lines.append(f"      {p['key']:40s} = {p['value']}")
        lines.append("")

    # Pin links summary
    if data["pin_links"]:
        lines.append(f"**I/O Pin Links**: {len(data['pin_links'])} pins assigned")
        lines.append("  (Pin names require project.dat — only GUIDs available)")

    return "\n".join(lines)


# ---------------------------------------------------------------------------
# MCP Tools — DB Variables
# ---------------------------------------------------------------------------

VALID_VAR_TYPES = {"TBOOLEAN", "TUINT8", "TUINT16", "TINT16", "TUINT32", "TFLOAT32"}


def _fmt_var_compact(v: dict) -> str:
    """Format a variable as a compact one-line string."""
    return (
        f"  {v['name']:35s}  {v['var_type']:10s}  "
        f"def={v['default_value']:>8s}  [{v['min']}..{v['max']}]  "
        f"{v['unit']:8s}  {v['description']}"
    )


@mcp.tool()
def list_db_variables(database: str = "") -> str:
    """List all variables in a database with their type, default, range, and unit.

    Requires PDT_DIR environment variable.

    Args:
        database: Database name (e.g. 'NvMemSensors'). Empty to list all variables.
    """
    try:
        variables = _list_db_variables(database)
    except Exception as e:
        return f"Error: {e}"

    if not variables:
        return f"No variables found" + (f" in database '{database}'" if database else "") + "."

    # Group by database
    if not database:
        grouped: dict[str, list] = {}
        for v in variables:
            db = v.get("database", "?")
            grouped.setdefault(db, []).append(v)

        lines = [f"**DB Variables ({len(variables)} total)**\n"]
        for db_name in sorted(grouped.keys()):
            db_vars = sorted(grouped[db_name], key=lambda v: v.get("idx", 0))
            lines.append(f"\n**{db_name}** ({len(db_vars)} variables)")
            for v in db_vars:
                lines.append(_fmt_var_compact(v))
        return "\n".join(lines)

    variables.sort(key=lambda v: v.get("idx", 0))
    lines = [f"**{database}** ({len(variables)} variables)\n"]
    for v in variables:
        lines.append(_fmt_var_compact(v))
    return "\n".join(lines)


@mcp.tool()
def get_db_variable(database: str, variable: str) -> str:
    """Get detailed info for one database variable including access levels and dataset values.

    Requires PDT_DIR environment variable.

    Args:
        database: Database name (e.g. 'NvMemSensors').
        variable: Variable name (case-insensitive, e.g. 'usSensorSor').
    """
    try:
        v = _get_db_variable(database, variable)
    except Exception as e:
        return f"Error: {e}"

    lines = [
        f"**{v['name']}** ({v['database']})",
        f"  Type: {v['var_type']} (prefix: {v['type_prefix']}, byte: {v['var_type_byte']})",
        f"  Default: {v['default_value']}",
        f"  Range: [{v['min']} .. {v['max']}]",
        f"  Unit: {v['unit']}",
        f"  Description: {v['description']}",
    ]
    if v.get("notes"):
        lines.append(f"  Notes: {v['notes']}")
    lines.extend([
        f"  CommID: {v['comm_id']}  |  Idx: {v['idx']}  |  NvMem Address: {v['nv_mem_address']}",
        f"  GUID: {v['guid']}",
        f"  HST Scaling: offset={v['hst_scaling_offset']} factor={v['hst_scaling_factor']} unit={v['hst_scaling_unit']}",
    ])

    if v.get("access_levels"):
        lines.append(f"  Access Levels:")
        for role, access in v["access_levels"].items():
            lines.append(f"    {role}: {access}")

    if v.get("dataset_values"):
        lines.append(f"  Dataset Values:")
        for ds in v["dataset_values"]:
            lines.append(f"    [{ds['index']}] = {ds['value']}")

    return "\n".join(lines)


@mcp.tool()
def add_db_variable(
    database: str,
    name: str,
    type: str,
    default: str,
    min: str = "",
    max: str = "",
    unit: str = "[-]",
    description: str = "",
) -> str:
    """Add a new variable to a database in the HDB project.

    Creates a .hdb.bak backup before the first modification.
    The variable is cloned from an existing variable of the same type to ensure
    all internal PDT fields are correctly initialized.
    Requires PDT_DIR environment variable.

    Args:
        database: Database name (e.g. 'NvMemSensors').
        name: Variable name in camelCase without type prefix (e.g. 'radarFilterGain').
              The type prefix (e.g. 'u16', 'bo') is added automatically by PDT.
        type: Data type. One of: TBOOLEAN, TUINT8, TUINT16, TINT16, TUINT32, TFLOAT32.
        default: Default value (e.g. '100', 'TRUE', '3.14').
        min: Minimum value. Empty uses the type default (e.g. 'U16_MIN').
        max: Maximum value. Empty uses the type default (e.g. 'U16_MAX').
        unit: Unit string (e.g. 'mm', 'ms', '[-]'). Default '[-]'.
        description: Human-readable description.
    """
    type_upper = type.upper()
    if type_upper not in VALID_VAR_TYPES:
        return f"Invalid type '{type}'. Must be one of: {', '.join(sorted(VALID_VAR_TYPES))}"

    try:
        result = _add_db_variable(database, name, type_upper, default, min, max, unit, description)
    except Exception as e:
        return f"Error: {e}"

    if result.get("status") == "ok":
        return (
            f"OK — Added variable '{result['name']}' to {result['database']}.\n"
            f"  Type: {result['var_type']}  Default: {result['default_value']}\n"
            f"  Range: [{result['min']} .. {result['max']}]  Unit: {result.get('unit', '[-]')}\n"
            f"  CommID: {result['comm_id']}  Idx: {result['idx']}  GUID: {result['guid']}\n"
            f"\nCache cleared. Use reload_hdb to verify."
        )
    return f"Unexpected result: {result}"


@mcp.tool()
def update_db_variable(
    database: str,
    variable: str,
    default: str = "",
    min: str = "",
    max: str = "",
    unit: str = "",
    description: str = "",
) -> str:
    """Modify properties of an existing database variable in the HDB project.

    Only provided (non-empty) arguments are changed; omit or leave empty to keep current value.
    Creates a .hdb.bak backup before the first modification.
    Requires PDT_DIR environment variable.

    Args:
        database: Database name (e.g. 'NvMemSensors').
        variable: Variable name (case-insensitive, e.g. 'usSensorSor').
        default: New default value. Empty to keep current.
        min: New minimum value. Empty to keep current.
        max: New maximum value. Empty to keep current.
        unit: New unit string. Empty to keep current.
        description: New description. Empty to keep current.
    """
    kwargs = {}
    if default:
        kwargs["default"] = default
    if min:
        kwargs["min"] = min
    if max:
        kwargs["max"] = max
    if unit:
        kwargs["unit"] = unit
    if description:
        kwargs["description"] = description

    if not kwargs:
        return "No changes specified. Provide at least one of: default, min, max, unit, description."

    try:
        result = _update_db_variable(database, variable, **kwargs)
    except Exception as e:
        return f"Error: {e}"

    if result.get("status") == "ok":
        changes = ", ".join(f"{k}={v}" for k, v in kwargs.items())
        return f"OK — Updated '{result['name']}' in {result['database']}: {changes}\nCache cleared."
    return f"Unexpected result: {result}"


@mcp.tool()
def delete_db_variable(database: str, variable: str) -> str:
    """Remove a variable from a database in the HDB project.

    Creates a .hdb.bak backup before the first modification.
    Requires PDT_DIR environment variable.

    Args:
        database: Database name (e.g. 'NvMemSensors').
        variable: Variable name to delete (case-insensitive).
    """
    try:
        result = _delete_db_variable(database, variable)
    except Exception as e:
        return f"Error: {e}"

    if result.get("status") == "ok":
        return f"OK — Deleted '{result['name']}' from {result['database']}.\nCache cleared."
    return f"Unexpected result: {result}"


# ---------------------------------------------------------------------------
# MCP Tools — HDB Search
# ---------------------------------------------------------------------------

@mcp.tool()
def search_hdb(pattern: str) -> str:
    """Regex search across all XML files in the HDB archive.

    Useful for finding raw data not exposed by other tools.
    Returns matching lines with file context.
    """
    if not HDB_PATH or not os.path.isfile(HDB_PATH):
        return f"HDB file not found: {HDB_PATH!r}"

    try:
        regex = re.compile(pattern, re.IGNORECASE)
    except re.error as e:
        return f"Invalid regex: {e}"

    zf = zipfile.ZipFile(HDB_PATH, "r")
    results = []
    xml_files = [f for f in zf.namelist() if f.endswith(".xml")]

    for fname in xml_files:
        try:
            text = zf.read(fname).rstrip(b"\x00").decode("utf-8")
        except Exception:
            continue
        for i, line in enumerate(text.splitlines(), 1):
            if regex.search(line):
                results.append(f"  {fname}:{i}  {line.strip()}")
                if len(results) >= 100:
                    break
        if len(results) >= 100:
            break

    zf.close()

    if not results:
        return f"No matches for '{pattern}' in XML files."

    header = f"**Search results for '{pattern}' ({len(results)} matches, max 100)**\n"
    return header + "\n".join(results)


# ---------------------------------------------------------------------------
# MCP Tools — Errors
# ---------------------------------------------------------------------------

@mcp.tool()
def list_errors(spn_filter: int = 0, description_filter: str = "") -> str:
    """List error definitions from the PDT project.

    Returns SPN, description, severity, debounce times, and thresholds.
    Requires PDT_DIR environment variable pointing to the PDT installation.

    Args:
        spn_filter: Filter by exact SPN number (0 = show all).
        description_filter: Filter by description substring (case-insensitive).
    """
    try:
        errors = get_errors()
    except Exception as e:
        return f"Error loading errors: {e}"

    if spn_filter:
        errors = [e for e in errors if e["spn"] == spn_filter]

    if description_filter:
        df = description_filter.lower()
        errors = [e for e in errors if df in e["description"].lower()]

    if not errors:
        return "No errors match the filter."

    lines = [f"**Error Definitions ({len(errors)})**\n"]
    for e in sorted(errors, key=lambda x: x["spn"]):
        if e["set_debounce_enabled"]:
            set_info = f"  set: {e['set_debounce_ms']}ms debounce, threshold={e['set_threshold']}"
        else:
            set_info = f"  set: threshold={e['set_threshold']}"

        if e["release_debounce_enabled"]:
            rel_info = f"  rel: {e['release_debounce_ms']}ms debounce, threshold={e['release_threshold']}"
        else:
            rel_info = f"  rel: threshold={e['release_threshold']}"

        lines.append(
            f"  SPN {e['spn']:>5d}  sev={e['severity']}  "
            f"type={e['error_type']}  store={e['store_behaviour']}"
        )
        lines.append(f"           {e['description']}")
        lines.append(f"          {set_info}  |{rel_info}")
    return "\n".join(lines)


@mcp.tool()
def get_error(spn: int) -> str:
    """Look up a specific error definition by SPN number.

    Returns full details including debounce, thresholds, and reaction info.
    Requires PDT_DIR environment variable.

    Args:
        spn: The SPN (Suspect Parameter Number) to look up.
    """
    try:
        errors = get_errors()
    except Exception as e:
        return f"Error loading errors: {e}"

    matches = [e for e in errors if e["spn"] == spn]
    if not matches:
        return f"No error found with SPN {spn}."

    e = matches[0]
    lines = [
        f"**SPN {e['spn']}: {e['description']}**",
        f"  Severity: {e['severity']}  |  Type: {e['error_type']}  |  Store: {e['store_behaviour']}",
    ]
    if e["comment"]:
        lines.append(f"  Comment: {e['comment']}")
    if e["symbol"]:
        lines.append(f"  Symbol: {e['symbol']}")

    lines.append(f"  Set: debounce={'ON' if e['set_debounce_enabled'] else 'OFF'}"
                 f" {e['set_debounce_ms']}ms  threshold={e['set_threshold']}")
    lines.append(f"  Release: debounce={'ON' if e['release_debounce_enabled'] else 'OFF'}"
                 f" {e['release_debounce_ms']}ms  threshold={e['release_threshold']}")
    lines.append(f"  Reaction: advanced_info={e['reaction_advanced_info']}")
    lines.append(f"  Error Info Page: {e['error_info_page']}")

    # GUID references
    if e.get("detection_method"):
        lines.append(f"  DetectionMethod: {e['detection_method']}")
    if e.get("fmi"):
        lines.append(f"  Fmi: {e['fmi']}")
    if e.get("fmi_extended"):
        lines.append(f"  FmiExtended: {e['fmi_extended']}")
    if e.get("owner_id"):
        lines.append(f"  OwnerId: {e['owner_id']}")
    if e.get("restricted_mode"):
        lines.append(f"  RestrictedMode: {e['restricted_mode']}")

    return "\n".join(lines)


@mcp.tool()
def add_custom_error(
    template: str,
    dm_name: str,
    bit: int,
    spn: int,
    block_name: str = "",
    description: str = "",
    severity: int = 3,
    fmi: str = "FMI_31_CONDITION_EXISTS",
    fmi_extended: str = "FMIEX_GLOBAL",
    set_debounce_ms: int = 500,
    release_debounce_ms: int = 0,
    set_threshold: int = 500,
    release_threshold: int = 1000,
) -> str:
    """Add a custom error to the HDB project (atomic: updates both project.dat and Errors.dat).

    Creates a detection method in the template and a matching error entry.
    If the template doesn't exist, a new error block is created.
    Backup created before first write.

    Args:
        template: Error template/block name (e.g. 'Error index us'). Creates new block if not found.
        dm_name: Detection method name (e.g. 'DM_US_HINDEX_EOB_NEW'). Must be unique.
        bit: Bit position within the block (0-7).
        spn: SPN number for the error. Must be unique.
        block_name: Software error block name (e.g. 'ERR_INDEX_US'). UPPER_SNAKE_CASE, no spaces. Required when creating a new template. Used to create the TBlock in Ecu.TBlocks for code generation.
        description: Error description text.
        severity: Severity level (1=info, 3=warning, 5=critical). Default 3.
        fmi: FMI name (e.g. 'FMI_31_CONDITION_EXISTS'). Default FMI_31.
        fmi_extended: FMI extension/component name (e.g. 'FMIEX_GLOBAL'). Default FMIEX_GLOBAL.
        set_debounce_ms: Error set debounce time in ms. Default 500.
        release_debounce_ms: Error release debounce time in ms. Default 0.
        set_threshold: Error set threshold. Default 500.
        release_threshold: Error release threshold. Default 1000.
    """
    try:
        result = _add_custom_error(
            template, dm_name, bit, spn,
            block_name=block_name,
            description=description, severity=severity,
            fmi=fmi, fmi_extended=fmi_extended,
            set_debounce_ms=set_debounce_ms,
            release_debounce_ms=release_debounce_ms,
            set_threshold=set_threshold,
            release_threshold=release_threshold,
        )
    except Exception as e:
        return f"Error: {e}"

    if result.get("status") == "ok":
        lines = [
            f"OK — {result.get('message', '')}",
            f"  SPN: {result.get('spn')}",
            f"  DM: {result.get('dm_name')} (GUID: {result.get('dm_guid', '')})",
            f"  Template: {result.get('template')}",
            f"  TBlock: {result.get('block_name', '') or '(not set)'}",
            f"  Error ObjectId: {result.get('object_id', '')}",
            f"  New block: {result.get('new_block', False)}",
            "Cache cleared.",
        ]
        return "\n".join(lines)
    return f"Unexpected result: {result}"


@mcp.tool()
def list_detection_methods(filter: str = "") -> str:
    """List custom detection methods from the PDT project.

    Shows detection method names, bit positions, and default FMI values.
    Use to resolve DetectionMethod GUIDs from error definitions.
    Requires PDT_DIR environment variable.

    Args:
        filter: Optional name substring filter (case-insensitive).
    """
    try:
        dms = _get_detection_methods(filter)
    except Exception as e:
        return f"Error loading detection methods: {e}"

    if not dms:
        return "No detection methods found."

    # Separate by source
    custom = [d for d in dms if d.get("source") == "Custom"]
    repo = [d for d in dms if d.get("source") == "Repo"]

    lines = []
    if custom:
        lines.append(f"**Custom Detection Methods ({len(custom)})**\n")
        for d in custom:
            lines.append(
                f"  {d['detection']:<40s}  bit={d['bit']}  "
                f"fmi={d['default_fmi']}  fmiEx={d['default_fmi_ex']}  "
                f"({d['detection_method_name']})"
            )

    if repo:
        lines.append(f"\n**Repo Detection Methods ({len(repo)})**\n")
        for d in repo:
            guid = d.get("guid", "")
            lines.append(f"  {d['detection']:<40s}  GUID={guid}")

    return "\n".join(lines) if lines else "No detection methods found."


@mcp.tool()
def list_fmi_definitions() -> str:
    """List FMI and FMI extension (component) definitions.

    Shows the GUID-to-name mapping for FMI codes and FMI extensions.
    Use to resolve Fmi and FmiExtended GUIDs from error definitions.
    Requires PDT_DIR environment variable.
    """
    try:
        data = _get_fmi_definitions()
    except Exception as e:
        return f"Error loading FMI definitions: {e}"

    lines = []
    fmis = data.get("fmis", [])
    if fmis:
        lines.append(f"**FMI Definitions ({len(fmis)})**\n")
        for f in fmis:
            lines.append(f"  {f['name']:<45s}  val={f['value']}  GUID={f['guid']}")
            if f.get("description"):
                lines.append(f"    {f['description']}")

    fmi_exts = data.get("fmi_exts", [])
    if fmi_exts:
        lines.append(f"\n**FMI Extensions / Components ({len(fmi_exts)})**\n")
        for f in fmi_exts:
            lines.append(f"  {f['name']:<45s}  val={f['value']}  GUID={f['guid']}")
            if f.get("description"):
                lines.append(f"    {f['description']}")

    return "\n".join(lines) if lines else "No FMI definitions found."


@mcp.tool()
def list_error_templates() -> str:
    """List error templates from the PDT project.

    Shows custom error block templates (e.g. 'Error index radar').
    Requires PDT_DIR environment variable.
    """
    try:
        templates = _get_error_templates()
    except Exception as e:
        return f"Error loading error templates: {e}"

    if not templates:
        return "No error templates found."

    lines = [f"**Error Templates ({len(templates)})**\n"]
    for t in templates:
        lines.append(f"  {t['type']:<30s}  source={t['source']}")
        if t.get("description"):
            lines.append(f"    {t['description']}")

    return "\n".join(lines)


# ---------------------------------------------------------------------------
# MCP Tools — Write
# ---------------------------------------------------------------------------

@mcp.tool()
def list_hdb_xml_files() -> str:
    """List all XML files in the HDB archive with their sizes.

    Use this to discover which files can be modified with update_hdb_xml.
    """
    if not HDB_PATH or not os.path.isfile(HDB_PATH):
        return f"HDB file not found: {HDB_PATH!r}"

    zf = zipfile.ZipFile(HDB_PATH, "r")
    xml_files = []
    for info in sorted(zf.infolist(), key=lambda i: i.filename):
        if info.filename.endswith(".xml") and not info.is_dir():
            xml_files.append((info.filename, info.file_size))
    zf.close()

    if not xml_files:
        return "No XML files found in HDB archive."

    lines = [f"**XML files in HDB ({len(xml_files)})**\n"]
    for fname, size in xml_files:
        lines.append(f"  {fname:45s}  {size:>8,} bytes")
    return "\n".join(lines)


@mcp.tool()
def read_hdb_xml(file: str, xpath: str = "") -> str:
    """Read raw XML content from the HDB archive.

    Returns the XML as text, optionally filtered by XPath.
    Useful for inspecting XML structure before making changes.

    Args:
        file: XML filename in the HDB archive (e.g. 'CanMessages.xml').
        xpath: Optional XPath to select specific elements (e.g. './/Name').
    """
    try:
        root = read_xml_from_hdb(file)
    except Exception as e:
        return f"Error: {e}"

    if xpath:
        elements = root.findall(xpath)
        if not elements:
            return f"No elements match XPath '{xpath}' in {file}."
        parts = []
        for el in elements[:50]:
            parts.append(ET.tostring(el, encoding="unicode"))
        header = f"**{len(elements)} elements matching '{xpath}'**"
        if len(elements) > 50:
            header += " (showing first 50)"
        return header + "\n\n" + "\n".join(parts)

    return ET.tostring(root, encoding="unicode")


@mcp.tool()
def update_hdb_xml(file: str, xpath: str, action: str,
                   tag: str = "", text: str = "", attributes: str = "") -> str:
    """Modify an XML element in the HDB archive.

    Creates a .hdb.bak backup before the first modification.
    Cache is automatically cleared after writing.

    Args:
        file: XML filename in the HDB archive (e.g. 'CanMessages.xml').
        xpath: XPath to select the target element(s).
        action: One of 'set_text', 'set_attr', 'add_child', 'remove'.
            - set_text: Set the text content of matched elements.
            - set_attr: Set attributes on matched elements (provide attributes as 'key=value,key2=value2').
            - add_child: Add a child element to matched elements (provide tag, optional text and attributes).
            - remove: Remove matched elements from their parents.
        tag: Tag name for add_child action.
        text: Text content for set_text or add_child actions.
        attributes: Comma-separated key=value pairs for set_attr or add_child (e.g. 'Name=foo,Value=bar').
    """
    try:
        root = read_xml_from_hdb(file)
    except Exception as e:
        return f"Error reading: {e}"

    elements = root.findall(xpath)
    if not elements:
        return f"No elements match XPath '{xpath}' in {file}."

    # Parse attributes string
    attr_dict = {}
    if attributes:
        for pair in attributes.split(","):
            if "=" in pair:
                k, v = pair.split("=", 1)
                attr_dict[k.strip()] = v.strip()

    count = len(elements)

    if action == "set_text":
        for el in elements:
            el.text = text
        msg = f"Set text to '{text}' on {count} element(s)"

    elif action == "set_attr":
        if not attr_dict:
            return "Error: 'attributes' parameter required for set_attr (e.g. 'key=value,key2=value2')."
        for el in elements:
            el.attrib.update(attr_dict)
        msg = f"Set attributes {attr_dict} on {count} element(s)"

    elif action == "add_child":
        if not tag:
            return "Error: 'tag' parameter required for add_child."
        for el in elements:
            child = ET.SubElement(el, tag, attrib=attr_dict)
            if text:
                child.text = text
        msg = f"Added <{tag}> child to {count} element(s)"

    elif action == "remove":
        removed = 0
        for el in elements:
            for parent in root.iter():
                if el in list(parent):
                    parent.remove(el)
                    removed += 1
                    break
        msg = f"Removed {removed} element(s)"

    else:
        return f"Unknown action '{action}'. Use: set_text, set_attr, add_child, remove."

    try:
        write_xml_to_hdb(file, root)
    except Exception as e:
        return f"Error writing: {e}"

    return f"OK — {msg} in {file}. Cache cleared. Use reload_hdb to verify."


# ---------------------------------------------------------------------------
# MCP Tools — CAN Buses
# ---------------------------------------------------------------------------

@mcp.tool()
def list_can_buses() -> str:
    """List all CAN buses in the project with message counts and buffer IDs.

    Use the bus number (1-indexed) with add_can_message to target a specific bus.
    """
    data = get_cache()
    buses = data.get("buses", [])
    if not buses:
        return "No CAN buses found in the project."

    lines = [f"**CAN Buses ({len(buses)})**\n"]
    for i, bus in enumerate(buses, 1):
        total = bus["send_count"] + bus["recv_count"]
        lines.append(
            f"  Bus {i}: {bus['bus_id']}"
            f"  ({total} messages: {bus['send_count']} send, {bus['recv_count']} recv)"
        )
        lines.append(f"    ECU:         {bus['ecu_id']}")
        lines.append(f"    Send buffer: {bus['send_buffer'] or '(none)'}")
        lines.append(f"    Recv buffer: {bus['recv_buffer'] or '(none)'}")
    return "\n".join(lines)


# ---------------------------------------------------------------------------
# MCP Tools — Add CAN Signal to existing message
# ---------------------------------------------------------------------------

@mcp.tool()
def add_can_signal(
    message: str,
    name: str,
    start_bit: int,
    size_bits: int,
    unit: str = "[-]",
    description: str = "new signal",
) -> str:
    """Add a new CAN signal to an existing message.

    Args:
        message: Name of the existing message (case-insensitive).
        name: Signal name (e.g. 'hIndexSetpoint').
        start_bit: Start bit position in the message.
        size_bits: Signal width in bits.
        unit: Engineering unit (default '[-]').
        description: Signal description.
    """
    data = get_cache()
    msg_data = data["messages_by_name"].get(message.lower())
    if not msg_data:
        return f"Message '{message}' not found."

    msg_guid = msg_data["guid"]
    dt_id = data.get("data_type_id", "")
    ecu_dt_id = data.get("ecu_data_type_id", dt_id)
    if not dt_id:
        return "Cannot determine DataTypeId — no existing signals found."

    sig_el = _build_signal_element(msg_guid, name, str(start_bit), str(size_bits),
                                   dt_id, ecu_dt_id)
    # Set unit in all layers
    sdl = sig_el.find("SignalDefinitionLayer")
    if sdl is not None:
        u = sdl.find("Unit")
        if u is not None:
            u.text = unit
        d = sdl.find("Description")
        if d is not None:
            d.text = description
    for layer in ("EcuApplicationLayer", "ServiceToolDefinitionLayer"):
        lyr = sig_el.find(layer)
        if lyr is not None:
            su = lyr.find("ScalingUnit")
            if su is not None:
                su.text = unit

    try:
        sig_root = read_xml_from_hdb("CanSignals.xml")
    except Exception as e:
        return f"Error reading CanSignals.xml: {e}"

    sig_root.append(sig_el)

    try:
        write_xml_to_hdb("CanSignals.xml", sig_root)
    except Exception as e:
        return f"Error writing CanSignals.xml: {e}"

    clear_cache()
    return f"OK — Added signal '{name}' (bits [{start_bit}:{start_bit + size_bits - 1}], {unit}) to {message}."


# ---------------------------------------------------------------------------
# MCP Tools — Add CAN Message (high-level)
# ---------------------------------------------------------------------------

def _get_bus_info(bus: int) -> dict:
    """Get bus info by 1-indexed bus number. Raises ValueError on invalid bus."""
    data = get_cache()
    buses = data.get("buses", [])
    if not buses:
        raise ValueError("No CAN buses found in the project.")
    if bus < 1 or bus > len(buses):
        raise ValueError(
            f"Invalid bus number {bus}. Project has {len(buses)} bus(es). Use 1-{len(buses)}."
        )
    return buses[bus - 1]


def _sub(parent, tag, text=None):
    """Add a sub-element with optional text content."""
    el = ET.SubElement(parent, tag)
    if text is not None:
        el.text = str(text)
    return el


def _build_message_element(msg_guid, name, can_id, dlc, cycle_time, bus_id):
    """Build a complete CanMessageDataObject element."""
    msg = ET.Element("CanMessageDataObject")
    _sub(msg, "Id", msg_guid)
    _sub(msg, "BusId", bus_id)
    _sub(msg, "Name", name)
    _sub(msg, "CanId", can_id)
    _sub(msg, "ByteOrder", "DataIntel")
    _sub(msg, "MessageType", "Extended" if can_id > 0x7FF else "Standard")
    _sub(msg, "Dlc", dlc)
    _sub(msg, "DefaultByte", "255")
    _sub(msg, "Description")
    _sub(msg, "CycleTime", cycle_time)
    _sub(msg, "StartOffsetTime", "0")
    _sub(msg, "MinimumCycleTime", "10")
    _sub(msg, "TimeOut", "0")
    safety = _sub(msg, "Safety")
    _sub(safety, "SendInverseMessage", "false")
    _sub(safety, "DelayTimeInverseMessage", "0")
    _sub(safety, "InverseMessageId", "0")
    _sub(safety, "MaxSlotTimeInverseMessage", "0")
    _sub(safety, "CheckSafetyCounter", "false")
    _sub(msg, "ConsumerApplications")
    _sub(msg, "IsMuxed", "false")
    return msg


def _build_ecu_link_element(msg_guid, direction, ecu_id, send_buffer, recv_buffer):
    """Build a complete CanMessageEcuLinkDataObject element."""
    link = ET.Element("CanMessageEcuLinkDataObject")
    _sub(link, "VirtualEcuId", ecu_id)
    _sub(link, "CanMessageId", msg_guid)
    _sub(link, "Usage", direction)
    buf = recv_buffer if direction == "Receive" else send_buffer
    _sub(link, "BufferBlockObjectId", buf)
    _sub(link, "CanBlockObjectId", str(uuid.uuid4()))
    return link


def _build_signal_element(msg_guid, name, start_bit, size_bits, data_type_id,
                          ecu_data_type_id=None):
    """Build a complete CanSignalDataObject with all required sub-elements.

    Args:
        ecu_data_type_id: DataTypeId for EcuApplicationLayer and
            ServiceToolDefinitionLayer.  Falls back to data_type_id when not
            provided, which keeps backward-compatible behaviour.
    """
    ecu_dt = ecu_data_type_id or data_type_id

    sig = ET.Element("CanSignalDataObject")
    _sub(sig, "Id", str(uuid.uuid4()))
    _sub(sig, "OwnerId", msg_guid)
    _sub(sig, "StartBit", start_bit)
    _sub(sig, "SizeBits", size_bits)
    _sub(sig, "Name", name)

    # SignalDefinitionLayer — all fields required by PDT
    sdl = _sub(sig, "SignalDefinitionLayer")
    _sub(sdl, "DataTypeId", data_type_id)
    _sub(sdl, "Unit", "[-]")
    _sub(sdl, "MinValue", "VAR_MIN")
    _sub(sdl, "MaxValue", "VAR_MAX")
    pv = _sub(sdl, "PredefinedValues")
    for _ in range(4):
        _sub(pv, "string")
    _sub(sdl, "InitialValue", "0")
    _sub(sdl, "DefaultValue", "0")
    _sub(sdl, "ErrorRecordReaction", "ErrSigDflt")
    _sub(sdl, "ReceiverAddress", "0")
    _sub(sdl, "Description", "new signal")

    # EcuApplicationLayer — all fields required by PDT
    eal = _sub(sig, "EcuApplicationLayer")
    _sub(eal, "DataTypeId", ecu_dt)
    _sub(eal, "ScalingUnit", "[-]")
    _sub(eal, "ScalingOffset", "0")
    _sub(eal, "ScalingMultiplier", "1")
    _sub(eal, "ScalingDivisor", "1")
    pv2 = _sub(eal, "PredefinedValues")
    for _ in range(4):
        _sub(pv2, "string")
    _sub(eal, "SignalEcuLinks")

    # ServiceToolDefinitionLayer
    stl = _sub(sig, "ServiceToolDefinitionLayer")
    _sub(stl, "DataTypeId", ecu_dt)
    _sub(stl, "ScalingUnit", "[-]")
    _sub(stl, "ScalingOffset", "0")
    _sub(stl, "ScalingMultiplier", "1")
    _sub(stl, "ScalingDivisor", "1")

    return sig


@mcp.tool()
def add_can_message(
    name: str,
    can_id: int,
    direction: str = "SendCyclically",
    dlc: int = 8,
    cycle_time: int = 100,
    signals: str = "",
    bus: int = 1,
) -> str:
    """Add a new CAN message to the HDB project with all required XML structure.

    Creates entries in CanMessages.xml, CanMessageEcuLinks.xml, and CanSignals.xml.
    All required sub-elements are included so PDT can load the project without errors.
    Creates a .hdb.bak backup before the first modification.

    Args:
        name: Message name (e.g. 'VcuSendTestData').
        can_id: CAN ID as decimal (e.g. 419365500). Extended if > 0x7FF.
        direction: 'SendCyclically', 'SendEventBased', or 'Receive'.
        dlc: Data Length Code, 0-8 (default 8).
        cycle_time: Cycle time in milliseconds (default 100).
        signals: Comma-separated signal definitions as 'name:startbit:sizebits'.
                 Example: 'testValue:0:16,status:16:8'
                 Leave empty for a message with no signals.
        bus: Bus number (1-indexed). Use list_can_buses to see available buses.
             Default 1 (first/only bus).
    """
    valid_dirs = ("SendCyclically", "SendEventBased", "Receive")
    if direction not in valid_dirs:
        return f"Invalid direction '{direction}'. Use: {', '.join(valid_dirs)}"

    if dlc < 0 or dlc > 8:
        return "DLC must be 0-8."

    # Look up bus info
    try:
        bus_info = _get_bus_info(bus)
    except ValueError as e:
        return str(e)

    data = get_cache()
    dt_id = data.get("data_type_id", "")
    if not dt_id:
        return "Cannot determine DataTypeId — no existing signals found in the project."
    ecu_dt_id = data.get("ecu_data_type_id", dt_id)

    # Parse signals
    sig_defs = []
    if signals.strip():
        for part in signals.split(","):
            part = part.strip()
            if not part:
                continue
            pieces = part.split(":")
            if len(pieces) != 3:
                return f"Invalid signal format '{part}'. Expected 'name:startbit:sizebits'."
            try:
                sig_defs.append((pieces[0], int(pieces[1]), int(pieces[2])))
            except ValueError:
                return f"Invalid signal numbers in '{part}'. startbit and sizebits must be integers."

    msg_guid = str(uuid.uuid4())

    # 1. Add message to CanMessages.xml
    try:
        msg_root = read_xml_from_hdb("CanMessages.xml")
    except Exception as e:
        return f"Error reading CanMessages.xml: {e}"

    msg_el = _build_message_element(msg_guid, name, can_id, dlc, cycle_time, bus_info["bus_id"])
    msg_root.append(msg_el)

    try:
        write_xml_to_hdb("CanMessages.xml", msg_root)
    except Exception as e:
        return f"Error writing CanMessages.xml: {e}"

    # 2. Add ECU link to CanMessageEcuLinks.xml
    try:
        link_root = read_xml_from_hdb("CanMessageEcuLinks.xml")
    except Exception as e:
        return f"Error reading CanMessageEcuLinks.xml: {e}"

    link_el = _build_ecu_link_element(
        msg_guid, direction,
        bus_info["ecu_id"], bus_info["send_buffer"], bus_info["recv_buffer"],
    )
    link_root.append(link_el)

    try:
        write_xml_to_hdb("CanMessageEcuLinks.xml", link_root)
    except Exception as e:
        return f"Error writing CanMessageEcuLinks.xml: {e}"

    # 3. Add signals to CanSignals.xml
    if sig_defs:
        try:
            sig_root = read_xml_from_hdb("CanSignals.xml")
        except Exception as e:
            return f"Error reading CanSignals.xml: {e}"

        for sig_name, start_bit, size_bits in sig_defs:
            sig_el = _build_signal_element(msg_guid, sig_name, start_bit, size_bits, dt_id, ecu_dt_id)
            sig_root.append(sig_el)

        try:
            write_xml_to_hdb("CanSignals.xml", sig_root)
        except Exception as e:
            return f"Error writing CanSignals.xml: {e}"

    # 4. Reload cache and return result
    clear_cache()
    try:
        data = get_cache()
    except Exception as e:
        return f"Message added but reload failed: {e}"

    msg_data = data["messages_by_name"].get(name.lower())
    if msg_data:
        return f"OK — Added message to HDB.\n\n{fmt_message(msg_data)}"
    return f"OK — Added '{name}' (CAN ID {can_id}) with {len(sig_defs)} signal(s)."


# ---------------------------------------------------------------------------
# MCP Tools — Reload
# ---------------------------------------------------------------------------

@mcp.tool()
def reload_hdb() -> str:
    """Force re-parse of the HDB file. Use after saving in PDT."""
    clear_cache()
    try:
        data = get_cache()
    except Exception as e:
        return f"Error reloading: {e}"

    msg_count = len(data["messages_by_id"])
    sig_count = len(data["signals_by_id"])
    db_count = len(data["databases"])
    info = f"Reloaded {HDB_PATH}\n  {msg_count} CAN messages, {sig_count} signals, {db_count} databases"

    pdt_dir = _resolve_pdt_dir(HDB_PATH) if HDB_PATH else PDT_DIR
    if pdt_dir:
        try:
            errors = get_errors()
            info += f", {len(errors)} errors"
        except Exception:
            info += " (errors: reload failed — check PDT_DIR)"
        info += f"\n  PDT: {pdt_dir}"

    return info


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    mcp.run()
