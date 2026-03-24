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
    HDB_PATH, PDT_DIR,
    get_cache, clear_cache, get_errors,
    read_xml_from_hdb, write_xml_to_hdb,
)
from formatters import fmt_can_id, fmt_message, fmt_signal

mcp = FastMCP(
    "Match_PDT_MCP",
    instructions=(
        "Query CAN messages, signals, parameters, and ECU config "
        "from a HYDAC PDT .hdb project file."
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
def list_can_messages(direction: str = "", name_filter: str = "") -> str:
    """List all CAN messages, optionally filtered.

    Args:
        direction: Filter by direction — 'send', 'receive', or '' for all.
                   Matches 'Receive', 'SendCyclically', 'SendOnEvent'.
        name_filter: Filter by name substring (case-insensitive).
    """
    data = get_cache()
    messages = list(data["messages_by_id"].values())

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
# MCP Tools — Add CAN Message (high-level)
# ---------------------------------------------------------------------------

# Constants from the project's HDB structure
_BUS_ID = "0efc7807-4ab1-42f6-b245-cf4b9c90f449"
_ECU_ID = "35d59957-4a75-481f-a9a8-44b3e8440e9d"
_SEND_BUFFER = "10e0dca0-64bd-4508-bf53-3d9a76105bf4"
_RECV_BUFFER = "657ff079-4b37-460c-9c7e-15a81ce3b65a"
_DATA_TYPE_ID = "f1d98cd0-0d83-44db-b23f-74cc3ba1808e"


def _sub(parent, tag, text=None):
    """Add a sub-element with optional text content."""
    el = ET.SubElement(parent, tag)
    if text is not None:
        el.text = str(text)
    return el


def _build_message_element(msg_guid, name, can_id, dlc, cycle_time):
    """Build a complete CanMessageDataObject element."""
    msg = ET.Element("CanMessageDataObject")
    _sub(msg, "Id", msg_guid)
    _sub(msg, "BusId", _BUS_ID)
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


def _build_ecu_link_element(msg_guid, direction):
    """Build a complete CanMessageEcuLinkDataObject element."""
    link = ET.Element("CanMessageEcuLinkDataObject")
    _sub(link, "VirtualEcuId", _ECU_ID)
    _sub(link, "CanMessageId", msg_guid)
    _sub(link, "Usage", direction)
    buf = _RECV_BUFFER if direction == "Receive" else _SEND_BUFFER
    _sub(link, "BufferBlockObjectId", buf)
    _sub(link, "CanBlockObjectId", str(uuid.uuid4()))
    return link


def _build_signal_element(msg_guid, name, start_bit, size_bits):
    """Build a complete CanSignalDataObject with all required sub-elements."""
    sig = ET.Element("CanSignalDataObject")
    _sub(sig, "Id", str(uuid.uuid4()))
    _sub(sig, "OwnerId", msg_guid)
    _sub(sig, "StartBit", start_bit)
    _sub(sig, "SizeBits", size_bits)
    _sub(sig, "Name", name)

    # SignalDefinitionLayer — all fields required by PDT
    sdl = _sub(sig, "SignalDefinitionLayer")
    _sub(sdl, "DataTypeId", _DATA_TYPE_ID)
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
    _sub(eal, "DataTypeId", _DATA_TYPE_ID)
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
    _sub(stl, "DataTypeId", _DATA_TYPE_ID)
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
    """
    valid_dirs = ("SendCyclically", "SendEventBased", "Receive")
    if direction not in valid_dirs:
        return f"Invalid direction '{direction}'. Use: {', '.join(valid_dirs)}"

    if dlc < 0 or dlc > 8:
        return "DLC must be 0-8."

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

    msg_el = _build_message_element(msg_guid, name, can_id, dlc, cycle_time)
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

    link_el = _build_ecu_link_element(msg_guid, direction)
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
            sig_el = _build_signal_element(msg_guid, sig_name, start_bit, size_bits)
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

    if PDT_DIR:
        try:
            errors = get_errors()
            info += f", {len(errors)} errors"
        except Exception:
            info += " (errors: reload failed — check PDT_DIR)"

    return info


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    mcp.run()
