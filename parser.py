"""HDB file parser and in-memory cache.

Parses a HYDAC PDT .hdb file (ZIP archive of null-padded XML files)
into indexed lookup dicts. Also handles dotnet helper calls for error
definitions from .dat files.
"""

import glob
import json
import math
import os
import shutil
import subprocess
import tempfile
import zipfile
import xml.etree.ElementTree as ET
from typing import Optional


# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------

# HDB_PATH: env var > first *.hdb in CWD
HDB_PATH = os.environ.get("HDB_PATH", "")
if not HDB_PATH:
    _hdb_files = glob.glob("*.hdb")
    if _hdb_files:
        HDB_PATH = os.path.abspath(_hdb_files[0])

# PDT_DIR: env var > auto-detect from standard install path
PDT_DIR = os.environ.get("PDT_DIR", "")
if not PDT_DIR:
    _pdt_base = r"C:\Program Files\Hydac\Project Definition Tool"
    if os.path.isdir(_pdt_base):
        # Sort by semantic version (split on dots, compare as integers)
        def _version_key(v):
            try:
                return tuple(int(p) for p in v.split("."))
            except ValueError:
                return (0,)
        _versions = sorted(os.listdir(_pdt_base), key=_version_key, reverse=True)
        if _versions:
            PDT_DIR = os.path.join(_pdt_base, _versions[0])

DOTNET_HELPER = os.path.join(os.path.dirname(os.path.abspath(__file__)), "dotnet-helper")


# ---------------------------------------------------------------------------
# XML helpers
# ---------------------------------------------------------------------------

def _read_xml(zf: zipfile.ZipFile, name: str) -> Optional[ET.Element]:
    """Read an XML file from the ZIP, stripping null padding."""
    try:
        raw = zf.read(name)
    except KeyError:
        return None
    text = raw.rstrip(b"\x00").decode("utf-8")
    return ET.fromstring(text)


def _text(el: Optional[ET.Element], tag: str, default: str = "") -> str:
    child = el.find(tag) if el is not None else None
    return child.text if child is not None and child.text else default


# ---------------------------------------------------------------------------
# HDB parsing
# ---------------------------------------------------------------------------

def _load_hdb() -> dict:
    """Parse the HDB and build indexed lookup dicts."""
    if not HDB_PATH or not os.path.isfile(HDB_PATH):
        raise FileNotFoundError(f"HDB file not found: {HDB_PATH!r}")

    zf = zipfile.ZipFile(HDB_PATH, "r")

    # --- CAN Messages ---
    messages_by_id: dict[str, dict] = {}
    messages_by_name: dict[str, dict] = {}
    messages_by_canid: dict[int, dict] = {}

    root = _read_xml(zf, "CanMessages.xml")
    if root is not None:
        for msg_el in root:
            msg = {
                "guid": _text(msg_el, "Id"),
                "bus_id": _text(msg_el, "BusId"),
                "name": _text(msg_el, "Name"),
                "can_id": int(_text(msg_el, "CanId", "0")),
                "byte_order": _text(msg_el, "ByteOrder"),
                "message_type": _text(msg_el, "MessageType"),
                "dlc": int(_text(msg_el, "Dlc", "8")),
                "default_byte": _text(msg_el, "DefaultByte"),
                "cycle_time": int(_text(msg_el, "CycleTime", "0")),
                "timeout": int(_text(msg_el, "TimeOut", "0")),
                "is_muxed": _text(msg_el, "IsMuxed") == "true",
                "description": _text(msg_el, "Description"),
                "direction": "",
                "signals": [],
            }
            messages_by_id[msg["guid"]] = msg
            messages_by_name[msg["name"].lower()] = msg
            messages_by_canid[msg["can_id"]] = msg

    # --- CAN Message ECU Links (direction + bus discovery) ---
    buses: dict[str, dict] = {}  # bus_id -> bus info

    # Pre-populate buses from messages
    for msg in messages_by_id.values():
        bid = msg["bus_id"]
        if bid and bid not in buses:
            buses[bid] = {
                "bus_id": bid,
                "ecu_id": "",
                "send_buffer": "",
                "recv_buffer": "",
                "send_count": 0,
                "recv_count": 0,
            }

    root = _read_xml(zf, "CanMessageEcuLinks.xml")
    if root is not None:
        for link_el in root:
            msg_guid = _text(link_el, "CanMessageId")
            usage = _text(link_el, "Usage")
            ecu_id = _text(link_el, "VirtualEcuId")
            buffer_id = _text(link_el, "BufferBlockObjectId")

            if msg_guid in messages_by_id:
                messages_by_id[msg_guid]["direction"] = usage

                # Discover per-bus ECU and buffer IDs
                bid = messages_by_id[msg_guid]["bus_id"]
                if bid and bid in buses:
                    if ecu_id:
                        buses[bid]["ecu_id"] = ecu_id
                    if usage == "Receive":
                        buses[bid]["recv_count"] += 1
                        if buffer_id:
                            buses[bid]["recv_buffer"] = buffer_id
                    else:
                        buses[bid]["send_count"] += 1
                        if buffer_id:
                            buses[bid]["send_buffer"] = buffer_id

    # Build ordered bus list (deterministic order by first message appearance)
    seen_bus_ids: list[str] = []
    for msg in messages_by_id.values():
        bid = msg["bus_id"]
        if bid and bid not in seen_bus_ids:
            seen_bus_ids.append(bid)
    buses_list: list[dict] = [buses[bid] for bid in seen_bus_ids if bid in buses]

    # --- CAN Signals ---
    signals_by_id: dict[str, dict] = {}
    signals_by_name: dict[str, list[dict]] = {}

    root = _read_xml(zf, "CanSignals.xml")
    if root is not None:
        for sig_el in root:
            ecu_layer = sig_el.find("EcuApplicationLayer")
            sig_layer = sig_el.find("SignalDefinitionLayer")

            sig = {
                "guid": _text(sig_el, "Id"),
                "owner_id": _text(sig_el, "OwnerId"),
                "name": _text(sig_el, "Name"),
                "start_bit": int(_text(sig_el, "StartBit", "0")),
                "size_bits": int(_text(sig_el, "SizeBits", "0")),
                "raw_unit": _text(sig_layer, "Unit"),
                "raw_min": _text(sig_layer, "MinValue"),
                "raw_max": _text(sig_layer, "MaxValue"),
                "initial_value": _text(sig_layer, "InitialValue"),
                "default_value": _text(sig_layer, "DefaultValue"),
                "error_reaction": _text(sig_layer, "ErrorRecordReaction"),
                "description": _text(sig_layer, "Description"),
                "unit": _text(ecu_layer, "ScalingUnit"),
                "offset": float(_text(ecu_layer, "ScalingOffset", "0")),
                "multiplier": float(_text(ecu_layer, "ScalingMultiplier", "1")),
                "divisor": float(_text(ecu_layer, "ScalingDivisor", "1")),
            }
            signals_by_id[sig["guid"]] = sig
            signals_by_name.setdefault(sig["name"].lower(), []).append(sig)

            if sig["owner_id"] in messages_by_id:
                messages_by_id[sig["owner_id"]]["signals"].append(sig)

    for msg in messages_by_id.values():
        msg["signals"].sort(key=lambda s: s["start_bit"])

    # --- Discover data_type_id from first signal ---
    data_type_id = ""
    ecu_data_type_id = ""
    root_sig = _read_xml(zf, "CanSignals.xml")
    if root_sig is not None:
        for sig_el in root_sig:
            sdl = sig_el.find("SignalDefinitionLayer")
            if sdl is not None:
                dt = _text(sdl, "DataTypeId")
                if dt:
                    data_type_id = dt
                    break
        # Discover EcuApplicationLayer DataTypeId (may differ from wire type)
        for sig_el in root_sig:
            eal = sig_el.find("EcuApplicationLayer")
            if eal is not None:
                edt = _text(eal, "DataTypeId")
                if edt and edt != data_type_id:
                    ecu_data_type_id = edt
                    break
        if not ecu_data_type_id:
            ecu_data_type_id = data_type_id

    # --- Databases ---
    databases: list[dict] = []
    root = _read_xml(zf, "DatabaseLists.xml")
    if root is not None:
        for db_el in root:
            databases.append({
                "guid": _text(db_el, "Id"),
                "name": _text(db_el, "Name"),
                "list_type": int(_text(db_el, "ListType", "0")),
                "start_address": int(_text(db_el, "StartAddress", "0")),
                "backup_start_address": int(_text(db_el, "BackupStartAddress", "0")),
                "list_mode": int(_text(db_el, "ListMode", "0")),
                "default_data_set_count": int(_text(db_el, "DefaultDataSetCount", "0")),
                "nv_crc_protected": _text(db_el, "IsNvCrcProtectionActive") == "true",
                "ram_crc_protected": _text(db_el, "IsRamCrcProtectionActive") == "true",
                "parameter_index": int(_text(db_el, "ParameterIndex", "0")),
            })

    # --- ECU Applications ---
    ecu_apps: list[dict] = []
    root = _read_xml(zf, "EcuApplications.xml")
    if root is not None:
        for app_el in root:
            ecu_apps.append({
                "guid": _text(app_el, "Id"),
                "name": _text(app_el, "Name"),
                "cycle_time": int(_text(app_el, "CycleTime", "0")),
                "offset_time": int(_text(app_el, "OffsetTime", "0")),
                "execution_time": int(_text(app_el, "ExecutionTime", "0")),
                "watchdog_time": int(_text(app_el, "WatchDogTime", "0")),
                "watchdog_reaction": int(_text(app_el, "WatchDogReaction", "0")),
                "task_priority": int(_text(app_el, "TaskPriority", "0")),
                "safety_level": int(_text(app_el, "SafetyLevel", "0")),
                "is_supervisor": _text(app_el, "IsSupervisor") == "true",
                "is_diagnosis": _text(app_el, "IsDiagnosis") == "true",
                "source_address": int(_text(app_el, "SourceAddress", "0")),
                "flash_start": int(_text(app_el, "FlashMemoryStartAddress", "0")),
                "flash_size": int(_text(app_el, "FlashMemorySize", "0")),
            })

    # --- Protocols ---
    protocols: list[dict] = []
    root = _read_xml(zf, "Protocols.xml")
    if root is not None:
        for ptc_el in root:
            protocols.append({
                "guid": _text(ptc_el, "ObjectId"),
                "name": _text(ptc_el, "Name"),
                "type": _text(ptc_el, "Type"),
                "match_version": _text(ptc_el, "MatchVersion"),
                "protocol_version": _text(ptc_el, "ProtocolVersion"),
                "ecu_code": _text(ptc_el, "EcuManufacturerCode"),
                "enabled": _text(ptc_el, "IsEnabled") == "true",
            })

    # --- Protocol Parameters ---
    protocol_params: dict[str, list[dict]] = {}
    root = _read_xml(zf, "ProtocolParameters.xml")
    if root is not None:
        for pp_el in root:
            ptc_id = _text(pp_el, "ProtocolObjectId")
            protocol_params.setdefault(ptc_id, []).append({
                "key": _text(pp_el, "Key"),
                "value": _text(pp_el, "Value"),
            })

    # --- Pin ECU Application Links ---
    pin_links: list[dict] = []
    root = _read_xml(zf, "PinEcuApplicationLinks.xml")
    if root is not None:
        for pin_el in root:
            pin_links.append({
                "pin_id": _text(pin_el, "PinId"),
                "ecu_app_id": _text(pin_el, "EcuApplicationId"),
                "is_main": _text(pin_el, "IsMain") == "true",
                "sw_module_id": _text(pin_el, "SoftwareModuleId"),
            })

    # --- Project info ---
    info = {}
    root = _read_xml(zf, "info.xml")
    if root is not None:
        info = {
            "pdt_version": _text(root, "PdtVersionString"),
            "file_format": _text(root, "FileFormatVersion"),
        }

    file_list = [i.filename for i in zf.infolist() if not i.is_dir()]
    zf.close()

    return {
        "messages_by_id": messages_by_id,
        "messages_by_name": messages_by_name,
        "messages_by_canid": messages_by_canid,
        "signals_by_id": signals_by_id,
        "signals_by_name": signals_by_name,
        "buses": buses_list,
        "data_type_id": data_type_id,
        "ecu_data_type_id": ecu_data_type_id,
        "databases": databases,
        "ecu_apps": ecu_apps,
        "protocols": protocols,
        "protocol_params": protocol_params,
        "pin_links": pin_links,
        "info": info,
        "file_list": file_list,
    }


# ---------------------------------------------------------------------------
# Cache
# ---------------------------------------------------------------------------

_cache: dict = {}


def get_cache() -> dict:
    """Return cached HDB data, loading on first access."""
    if not _cache:
        _cache.update(_load_hdb())
    return _cache


def clear_cache() -> None:
    """Clear the cache so the next access re-parses the HDB."""
    _cache.clear()


# ---------------------------------------------------------------------------
# Dotnet helper (for .dat file parsing)
# ---------------------------------------------------------------------------

_DOTNET_HELPER_EXE = os.path.join(DOTNET_HELPER, "bin", "Release", "net48", "HdbDatReader.exe")


def _run_dotnet_helper(command: str, timeout: int = 30, stdin_data: str = None) -> list | dict:
    """Run the dotnet helper and return parsed JSON.

    Args:
        command: Space-separated command and arguments.
        timeout: Timeout in seconds.
        stdin_data: Optional JSON string to pass via stdin (for write commands).
    """
    if not PDT_DIR:
        raise RuntimeError("PDT_DIR environment variable not set. Point it to the PDT installation directory.")
    if not HDB_PATH:
        raise RuntimeError("HDB_PATH environment variable not set.")

    exe = _DOTNET_HELPER_EXE
    if os.path.exists(exe):
        cmd = [exe, HDB_PATH, PDT_DIR] + command.split()
    else:
        cmd = ["dotnet", "run", "-c", "Release", "--", HDB_PATH, PDT_DIR] + command.split()

    result = subprocess.run(
        cmd,
        cwd=DOTNET_HELPER,
        capture_output=True, text=True, timeout=timeout,
        input=stdin_data,
    )
    if result.returncode != 0:
        raise RuntimeError(f"dotnet helper failed: {result.stderr.strip()}")
    return json.loads(result.stdout)


def get_errors() -> list[dict]:
    """Return cached error list, loading on first access."""
    if "errors" not in _cache:
        _cache["errors"] = _run_dotnet_helper("errors")
    return _cache["errors"]


def dump_dat(filename: str) -> dict:
    """Dump a single .dat file to JSON via the dotnet helper."""
    return _run_dotnet_helper(f"dump {filename}", timeout=120)


def dump_all_dats() -> dict:
    """Dump all .dat files from the HDB to JSON."""
    return _run_dotnet_helper("dump-all", timeout=120)


def list_dat_files() -> list[dict]:
    """List all .dat files in the HDB archive with sizes."""
    return _run_dotnet_helper("list-dat")


# ---------------------------------------------------------------------------
# Error GUID resolution queries
# ---------------------------------------------------------------------------

def get_detection_methods(filter: str = "") -> list[dict]:
    """List detection methods from project.dat. Optional name filter."""
    cmd = f"err-list-dms {filter}" if filter else "err-list-dms"
    return _run_dotnet_helper(cmd, timeout=60)


def get_fmi_definitions() -> dict:
    """List FMI and FMI extension definitions from project.dat."""
    return _run_dotnet_helper("err-list-fmis", timeout=60)


def get_error_templates() -> list[dict]:
    """List error templates from project.dat."""
    return _run_dotnet_helper("err-list-templates", timeout=60)


# ---------------------------------------------------------------------------
# DB variable queries & mutations
# ---------------------------------------------------------------------------

def list_db_variables(database: str = "") -> list[dict]:
    """List all variables in a database (or all databases)."""
    cmd = f"db-list-vars {database}" if database else "db-list-vars"
    return _run_dotnet_helper(cmd, timeout=30)


def get_db_variable(database: str, variable: str) -> dict:
    """Get detailed info for one database variable."""
    return _run_dotnet_helper(f"db-get-var {database} {variable}", timeout=30)


def add_db_variable(database: str, name: str, var_type: str, default: str,
                    min_val: str = "", max_val: str = "", unit: str = "[-]",
                    description: str = "") -> dict:
    """Add a new variable to a database. Returns the created variable info."""
    payload = json.dumps({
        "database": database,
        "name": name,
        "type": var_type,
        "default": default,
        "min": min_val,
        "max": max_val,
        "unit": unit,
        "description": description,
    })
    result = _run_dotnet_helper("db-add-var", timeout=60, stdin_data=payload)
    clear_cache()
    return result


def update_db_variable(database: str, variable: str, **kwargs) -> dict:
    """Update properties of an existing database variable.

    Keyword args: default, min, max, unit, description.
    Only provided kwargs are changed.
    """
    payload = {"database": database, "variable": variable}
    for key in ("default", "min", "max", "unit", "description"):
        if key in kwargs and kwargs[key] is not None:
            payload[key] = kwargs[key]
    result = _run_dotnet_helper("db-update-var", timeout=60, stdin_data=json.dumps(payload))
    clear_cache()
    return result


def delete_db_variable(database: str, variable: str) -> dict:
    """Delete a variable from a database."""
    payload = json.dumps({"database": database, "variable": variable})
    result = _run_dotnet_helper("db-delete-var", timeout=60, stdin_data=payload)
    clear_cache()
    return result


# ---------------------------------------------------------------------------
# HDB write support
# ---------------------------------------------------------------------------

def _next_power_of_2(n: int) -> int:
    """Return the smallest power of 2 >= n."""
    if n <= 0:
        return 256
    return 1 << math.ceil(math.log2(n))


def _null_pad(data: bytes, target_size: int) -> bytes:
    """Null-pad data to target_size bytes."""
    if len(data) >= target_size:
        return data
    return data + b"\x00" * (target_size - len(data))


def read_xml_from_hdb(filename: str) -> ET.Element:
    """Read and parse an XML file from the HDB archive.

    Returns the root Element for modification.
    """
    if not HDB_PATH or not os.path.isfile(HDB_PATH):
        raise FileNotFoundError(f"HDB file not found: {HDB_PATH!r}")

    zf = zipfile.ZipFile(HDB_PATH, "r")
    root = _read_xml(zf, filename)
    zf.close()

    if root is None:
        raise FileNotFoundError(f"XML file '{filename}' not found in HDB archive.")
    return root


def write_xml_to_hdb(filename: str, root: ET.Element) -> None:
    """Write a modified XML element tree back into the HDB archive.

    Creates a .bak backup before modifying. Null-pads the XML to match
    the original block size (or next power-of-2 if content grew).
    """
    if not HDB_PATH or not os.path.isfile(HDB_PATH):
        raise FileNotFoundError(f"HDB file not found: {HDB_PATH!r}")

    # Serialize XML
    xml_bytes = ET.tostring(root, encoding="utf-8", xml_declaration=True)

    # Read original archive
    zf = zipfile.ZipFile(HDB_PATH, "r")
    entries: dict[str, tuple[zipfile.ZipInfo, bytes]] = {}
    original_size = 0

    for info in zf.infolist():
        data = zf.read(info.filename)
        entries[info.filename] = (info, data)
        if info.filename == filename:
            original_size = info.file_size

    zf.close()

    if filename not in entries:
        raise FileNotFoundError(f"XML file '{filename}' not found in HDB archive.")

    # Null-pad to match original block size or next power-of-2
    target_size = original_size if len(xml_bytes) <= original_size else _next_power_of_2(len(xml_bytes))
    padded = _null_pad(xml_bytes, target_size)

    # Create backup
    backup_path = HDB_PATH + ".bak"
    if not os.path.isfile(backup_path):
        shutil.copy2(HDB_PATH, backup_path)

    # Write new archive to temp file, then replace original
    dir_name = os.path.dirname(HDB_PATH)
    fd, tmp_path = tempfile.mkstemp(suffix=".hdb", dir=dir_name)
    os.close(fd)

    try:
        with zipfile.ZipFile(tmp_path, "w", compression=zipfile.ZIP_DEFLATED) as zf_out:
            for fname, (info, data) in entries.items():
                if fname == filename:
                    zf_out.writestr(info, padded)
                else:
                    zf_out.writestr(info, data)

        # Replace original
        os.replace(tmp_path, HDB_PATH)
    except Exception:
        # Cleanup temp file on failure
        if os.path.isfile(tmp_path):
            os.remove(tmp_path)
        raise

    # Clear cache so next access re-parses
    clear_cache()
