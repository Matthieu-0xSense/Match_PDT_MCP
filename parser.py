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

# PDT version auto-discovery
_PDT_BASE = r"C:\Program Files\Hydac\Project Definition Tool"


def _version_key(v):
    """Sort key for semantic version directory names like '2.12.100'."""
    try:
        return tuple(int(p) for p in v.split("."))
    except ValueError:
        return (0,)


# A version directory only counts as an install if the PDT runtime is present.
# Uninstalled versions can leave a near-empty folder behind (e.g. 2.12.100 with
# only a stray helper exe); picking that breaks every .dat tool with
# "Impossible de trouver l'assembly 'Hydac.PDT.DataLayer.Implementation'".
_PDT_RUNTIME_MARKER = "Hydac.PDT.PdtFramework.dll"


def _pdt_dir_has_runtime(path: str) -> bool:
    return bool(path) and os.path.isfile(os.path.join(path, _PDT_RUNTIME_MARKER))


def _installed_pdt_dirs() -> list[str]:
    """Installed PDT version dirs (with runtime), newest first."""
    if not os.path.isdir(_PDT_BASE):
        return []
    versions = sorted(os.listdir(_PDT_BASE), key=_version_key, reverse=True)
    return [os.path.join(_PDT_BASE, v) for v in versions
            if _pdt_dir_has_runtime(os.path.join(_PDT_BASE, v))]


# Default PDT_DIR: env var > latest installed version (fallback)
PDT_DIR = os.environ.get("PDT_DIR", "")
if not PDT_DIR:
    _installed = _installed_pdt_dirs()
    if _installed:
        PDT_DIR = _installed[0]

_pdt_dir_cache: dict[str, str] = {}


def _extract_pdt_version(hdb_path: str) -> str:
    """Read the PdtVersionString from info.xml inside the .hdb archive."""
    try:
        with zipfile.ZipFile(hdb_path, "r") as zf:
            root = _read_xml(zf, "info.xml")
            if root is not None:
                el = root.find("PdtVersionString")
                if el is not None and el.text:
                    return el.text.strip()
    except Exception:
        pass
    return ""


def _resolve_pdt_dir(hdb_path: str) -> str:
    """Resolve the best PDT installation directory for a given .hdb project.

    Reads the project's PdtVersionString (e.g. '2.12.100.25'), truncates to
    the directory format ('2.12.100'), and checks if that version is installed.
    Falls back to the default (latest) PDT_DIR if not found.
    """
    if hdb_path in _pdt_dir_cache:
        return _pdt_dir_cache[hdb_path]

    version = _extract_pdt_version(hdb_path)
    if version and os.path.isdir(_PDT_BASE):
        # "2.12.100.25" -> "2.12.100"
        parts = version.split(".")
        dir_version = ".".join(parts[:3])
        candidate = os.path.join(_PDT_BASE, dir_version)
        if _pdt_dir_has_runtime(candidate):
            _pdt_dir_cache[hdb_path] = candidate
            return candidate
        else:
            import sys
            why = "not installed" if not os.path.isdir(candidate) else "folder has no PDT runtime"
            print(f"[Match_PDT_MCP] Warning: PDT {dir_version} {why}, "
                  f"using {PDT_DIR}", file=sys.stderr)

    _pdt_dir_cache[hdb_path] = PDT_DIR
    return PDT_DIR

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
    """Return cached HDB data, loading on first access.

    Keyed on "messages_by_id" rather than emptiness: helper-backed lookups
    (errors, buses) also park results in _cache, and a bare `if not _cache`
    would then skip the XML load and KeyError on the next CAN/DB read.
    """
    if "messages_by_id" not in _cache:
        _cache.update(_load_hdb())
    return _cache


def clear_cache() -> None:
    """Clear the cache so the next access re-parses the HDB."""
    _cache.clear()
    _pdt_dir_cache.clear()


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
    if not HDB_PATH:
        raise RuntimeError("HDB_PATH environment variable not set.")

    pdt_dir = _resolve_pdt_dir(HDB_PATH)
    if not pdt_dir:
        raise RuntimeError("No PDT installation found. Set PDT_DIR or install PDT.")

    exe = _DOTNET_HELPER_EXE
    if os.path.exists(exe):
        cmd = [exe, HDB_PATH, pdt_dir] + command.split()
    else:
        cmd = ["dotnet", "run", "-c", "Release", "--", HDB_PATH, pdt_dir] + command.split()

    verb = command.split()[0]
    is_write = verb in _V1_WRITE_COMMANDS
    if is_write:
        # The v1 helper rewrites the .hdb on disk. A running v2 helper still holds
        # the pre-write project in memory and would clobber this on its next save.
        _invalidate_helper_v2(f"v1 {verb}")

    result = subprocess.run(
        cmd,
        cwd=DOTNET_HELPER,
        capture_output=True, text=True, timeout=timeout,
        input=stdin_data,
    )
    if result.returncode != 0:
        raise RuntimeError(f"dotnet helper failed: {result.stderr.strip()}")
    return json.loads(result.stdout)


_V1_WRITE_COMMANDS = {"db-add-var", "db-update-var", "db-delete-var", "err-custom-add",
                      "err-custom-update", "err-custom-delete", "err-block-delete",
                      "err-blocks-repair"}


def _invalidate_helper_v2(reason: str) -> None:
    """Stop the long-lived v2 helper so its next use reloads the .hdb from disk."""
    try:
        import helper_v2
        helper_v2.invalidate(reason)
    except Exception as exc:  # noqa: BLE001 - never let this break a write
        import sys
        print(f"[Match_PDT_MCP] helper_v2 invalidate failed: {exc}", file=sys.stderr)


def get_can_buses() -> list[dict]:
    """CAN buses in PDT's own order (project.dat Repo.CanBusses), merged with
    the per-bus message stats derived from the XML files.

    The XML-only view (CanMessages.xml BusId order) can list buses in a
    different order than PDT and can include buses that no longer exist in
    project.dat. Each entry carries `in_project` so callers can tell.
    Falls back to the XML-only list when the .NET helper is unavailable.
    """
    if "buses_merged" in _cache:
        return _cache["buses_merged"]

    data = get_cache()
    xml_buses = {b["bus_id"]: b for b in data.get("buses", [])}
    merged: list[dict] = []
    try:
        repo_buses = _run_dotnet_helper("can-list-buses", timeout=60)
    except Exception as exc:  # noqa: BLE001 - degrade to XML view
        import sys
        print(f"[Match_PDT_MCP] can-list-buses unavailable ({exc}); "
              f"bus numbering falls back to CanMessages.xml order", file=sys.stderr)
        repo_buses = []

    seen: set[str] = set()
    for rb in repo_buses:
        bid = rb.get("guid", "")
        entry = dict(xml_buses.get(bid) or {
            "bus_id": bid, "ecu_id": "", "send_buffer": "", "recv_buffer": "",
            "send_count": 0, "recv_count": 0,
        })
        entry.update({
            "name": rb.get("name", ""),
            "hw_index": rb.get("hw_index"),
            "bitrate": rb.get("bitrate"),
            "description": rb.get("description", ""),
            "in_project": True,
        })
        merged.append(entry)
        seen.add(bid)
    for bid, b in xml_buses.items():
        if bid not in seen:
            entry = dict(b)
            entry.update({"name": "", "hw_index": None, "bitrate": None,
                          "description": "", "in_project": not repo_buses})
            merged.append(entry)

    _cache["buses_merged"] = merged
    return merged


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


def _find_reference_hdb_with_err_block() -> str:
    """Find a sibling .hdb project that has an ERR-type TBlock (for cloning).

    Scans the parent directory of the current project for other .hdb files
    and checks if they contain ERR blocks by searching for the ERR blueprint GUID.
    """
    if not HDB_PATH:
        return ""
    projects_dir = os.path.dirname(os.path.dirname(HDB_PATH))  # up from project dir
    if not os.path.isdir(projects_dir):
        projects_dir = os.path.dirname(HDB_PATH)
    import uuid
    err_blueprint = uuid.UUID("65e013fd-1bb0-4c28-ac30-7b8d5e51effd").bytes_le
    # Scan immediate subdirectories of the projects folder
    try:
        for entry in os.scandir(projects_dir):
            if not entry.is_dir():
                continue
            for f in os.listdir(entry.path):
                if not f.endswith(".hdb"):
                    continue
                hdb = os.path.join(entry.path, f)
                if os.path.abspath(hdb) == os.path.abspath(HDB_PATH):
                    continue
                try:
                    with zipfile.ZipFile(hdb, "r") as zf:
                        if "project.dat" in zf.namelist():
                            raw = zf.read("project.dat")
                            if err_blueprint in raw:
                                return hdb
                except Exception:
                    continue
    except Exception:
        pass
    return ""


def add_custom_error(template: str, dm_name: str, bit: int, spn: int,
                     block_name: str = "",
                     description: str = "", severity: int = 3,
                     fmi: str = "FMI_31_CONDITION_EXISTS",
                     fmi_extended: str = "FMIEX_GLOBAL",
                     set_debounce_ms: int = 500, release_debounce_ms: int = 0,
                     set_threshold: int = 500, release_threshold: int = 1000) -> dict:
    """Add a custom error via v1 dotnet-helper (XML-direct path).

    v2 (PDT-services in-process host) was attempted but produces project.dat that
    PDT compile rejects with DATABASE_ERROR_OBJ_SET_DATA_UNICODE_DM_BITPOS_NOT_FOUND.
    Root cause: PDT's GUI add-error path relies on WPF ViewModel observers
    (IDetectionMethodLinkObserver, ISingleErrorsObserver) to maintain
    TErrorTemplate.LinkedBlockIds and customDMs reactively. Headless v2 never
    materializes those VMs, so the saved project drifts (~5910 bytes scattered)
    from what GUI produces. See MIGRATION_PLAN.md "Phase 2 closure" for the full
    investigation. Until VM materialization is solved, v1 stays the production path.
    """
    params = {
        "template": template,
        "dm_name": dm_name,
        "bit": bit,
        "spn": spn,
        "block_name": block_name,
        "description": description,
        "severity": severity,
        "fmi": fmi,
        "fmi_extended": fmi_extended,
        "set_debounce_ms": set_debounce_ms,
        "release_debounce_ms": release_debounce_ms,
        "set_threshold": set_threshold,
        "release_threshold": release_threshold,
    }
    ref_hdb = _find_reference_hdb_with_err_block()
    payload = json.dumps({**params, "ref_hdb": ref_hdb})
    result = _run_dotnet_helper("err-custom-add", timeout=60, stdin_data=payload)
    clear_cache()
    return result


def list_custom_errors() -> dict:
    """Custom errors joined with their ERR block, bit, template and FMI names,
    plus the used/free bits of every ERR block. Read-only."""
    return _run_dotnet_helper("err-list-custom", timeout=60)


def update_custom_error(spn: int, **fields) -> dict:
    """Update fields of a custom error. Only keys present in `fields` change.

    Accepted keys: description, comment, severity, fmi, fmi_extended,
    set_debounce_ms, release_debounce_ms, set_threshold, release_threshold,
    new_spn, dm_name.
    """
    payload = json.dumps({"spn": spn, **fields})
    result = _run_dotnet_helper("err-custom-update", timeout=60, stdin_data=payload)
    clear_cache()
    return result


def delete_custom_error(spn: int) -> dict:
    """Remove a custom error and free its block bit."""
    result = _run_dotnet_helper("err-custom-delete", timeout=60,
                                stdin_data=json.dumps({"spn": spn}))
    clear_cache()
    return result


def repair_error_blocks() -> dict:
    """Register ERR-block params in the repo lists and resync ERROR_COUNT for
    blocks created by earlier versions of add_custom_error. Idempotent."""
    result = _run_dotnet_helper("err-blocks-repair", timeout=60, stdin_data="{}")
    clear_cache()
    return result


def delete_error_block(block_name: str) -> dict:
    """Remove an ERR block with all its errors and, if it was the last linked
    block, its template."""
    result = _run_dotnet_helper("err-block-delete", timeout=60,
                                stdin_data=json.dumps({"block_name": block_name}))
    clear_cache()
    return result


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

    # A running v2 helper holds the pre-edit project in memory; drop it so the
    # next v2 call reloads this file instead of saving stale state over it.
    _invalidate_helper_v2(f"xml write {filename}")

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
