"""
Semantic diff for HYDAC PDT .hdb files.

Compares two .hdb archives and produces a human-readable markdown report
of changes in terms of named entities (CAN messages, signals, database
variables, errors, detection methods, etc.).

Usage:
    python hdb_diff.py <hdb_a> <hdb_b> [--output report.md]

Requires:
    - Built dotnet-helper (bin/Release/net48/HdbDatReader.exe)
    - PDT installation (auto-discovered or set PDT_DIR env var)
"""

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path

import parser as hdb_parser
from formatters import fmt_can_id


SCRIPT_DIR = Path(__file__).parent
DOTNET_HELPER_EXE = SCRIPT_DIR / "dotnet-helper" / "bin" / "Release" / "net48" / "HdbDatReader.exe"


# ---------------------------------------------------------------------------
# Loaders
# ---------------------------------------------------------------------------

def load_hdb_xml(hdb_path: str) -> dict:
    """Load parsed XML entities from an HDB file via the parser module."""
    original = hdb_parser.HDB_PATH
    hdb_parser.HDB_PATH = hdb_path
    hdb_parser.clear_cache()
    try:
        data = hdb_parser._load_hdb()
    finally:
        hdb_parser.HDB_PATH = original
        hdb_parser.clear_cache()
    return data


def run_helper(hdb_path: str, pdt_dir: str, command: str, timeout: int = 120) -> list | dict:
    """Call the dotnet helper for a specific HDB file and return parsed JSON."""
    if not DOTNET_HELPER_EXE.exists():
        raise RuntimeError(
            f"dotnet-helper not built. Run:\n"
            f"  cd {SCRIPT_DIR / 'dotnet-helper'}\n"
            f"  dotnet build -c Release"
        )
    result = subprocess.run(
        [str(DOTNET_HELPER_EXE), hdb_path, pdt_dir] + command.split(),
        capture_output=True, text=True, timeout=timeout,
    )
    if result.returncode != 0:
        raise RuntimeError(f"dotnet helper failed ({command}): {result.stderr.strip()}")
    return json.loads(result.stdout)


def _extract_blocks_and_pins(project_dat: dict) -> tuple[list[dict], list[dict]]:
    """Extract blocks and pin configs from a project.dat dump."""
    ecu = project_dat.get("Repo", {}).get("RevisionSoftwares", [{}])
    if not ecu:
        return [], []
    ecu = ecu[0].get("Ecu", {})

    # Blocks
    blocks = []
    for b in ecu.get("TBlocks", []):
        if isinstance(b, dict) and b.get("Name"):
            blocks.append({
                "name": b.get("Name", ""),
                "class": b.get("BlueprintClassName", ""),
                "key": b.get("Key", ""),
            })

    # Pins (flatten all pin groups)
    pins = []
    for pg in ecu.get("PinGroups", []):
        if not isinstance(pg, dict):
            continue
        group_name = pg.get("Name", "")
        for pin in pg.get("TPins", []):
            if not isinstance(pin, dict):
                continue
            tblock = pin.get("TBlock")
            block_name = ""
            if isinstance(tblock, dict):
                block_name = tblock.get("Name", "")
            pins.append({
                "name": pin.get("Name", ""),
                "group": group_name,
                "config": pin.get("ActPinCfgName", ""),
                "block": block_name,
            })

    return blocks, pins


def load_all(hdb_path: str) -> dict:
    """Load all semantic entities from an HDB file."""
    pdt_dir = hdb_parser._resolve_pdt_dir(hdb_path)

    # XML entities
    xml = load_hdb_xml(hdb_path)

    # Resolve parent message name for each signal
    msgs_by_id = xml["messages_by_id"]
    signals = []
    for sig in xml["signals_by_id"].values():
        parent = msgs_by_id.get(sig["owner_id"])
        sig_copy = dict(sig)
        sig_copy["message_name"] = parent["name"] if parent else "?"
        signals.append(sig_copy)

    # .dat entities via dotnet helper
    try:
        errors = run_helper(hdb_path, pdt_dir, "errors")
    except Exception as e:
        print(f"Warning: could not load errors from {os.path.basename(hdb_path)}: {e}", file=sys.stderr)
        errors = []

    try:
        db_variables = run_helper(hdb_path, pdt_dir, "db-list-vars")
    except Exception as e:
        print(f"Warning: could not load db variables from {os.path.basename(hdb_path)}: {e}", file=sys.stderr)
        db_variables = []

    try:
        detection_methods = run_helper(hdb_path, pdt_dir, "err-list-dms")
    except Exception as e:
        print(f"Warning: could not load detection methods from {os.path.basename(hdb_path)}: {e}", file=sys.stderr)
        detection_methods = []

    # Blocks and pins from project.dat
    blocks, pins = [], []
    try:
        project_dat = run_helper(hdb_path, pdt_dir, "dump project.dat")
        blocks, pins = _extract_blocks_and_pins(project_dat)
    except Exception as e:
        print(f"Warning: could not load blocks/pins from {os.path.basename(hdb_path)}: {e}", file=sys.stderr)

    return {
        "messages": list(msgs_by_id.values()),
        "signals": signals,
        "databases": xml["databases"],
        "db_variables": db_variables,
        "errors": errors,
        "detection_methods": detection_methods,
        "ecu_apps": xml["ecu_apps"],
        "protocols": xml["protocols"],
        "buses": xml["buses"],
        "info": xml["info"],
        "blocks": blocks,
        "pins": pins,
    }


# ---------------------------------------------------------------------------
# Generic entity differ
# ---------------------------------------------------------------------------

def diff_entities(list_a, list_b, key_fn, label_fn, compare_fields):
    """Compare two entity lists semantically.

    Args:
        list_a, list_b: Lists of entity dicts.
        key_fn: Function(entity) -> hashable identity key.
        label_fn: Function(entity) -> human-readable one-liner.
        compare_fields: List of (field_key, display_name) tuples.

    Returns:
        (added, removed, changed) where:
        - added: list of label strings
        - removed: list of label strings
        - changed: list of (label, [(display_name, old_val, new_val), ...])
    """
    idx_a = {}
    for e in list_a:
        k = key_fn(e)
        if k is not None:
            idx_a[k] = e
    idx_b = {}
    for e in list_b:
        k = key_fn(e)
        if k is not None:
            idx_b[k] = e

    keys_a = set(idx_a.keys())
    keys_b = set(idx_b.keys())

    added = [label_fn(idx_b[k]) for k in sorted(keys_b - keys_a, key=str)]
    removed = [label_fn(idx_a[k]) for k in sorted(keys_a - keys_b, key=str)]

    changed = []
    for k in sorted(keys_a & keys_b, key=str):
        ea, eb = idx_a[k], idx_b[k]
        diffs = []
        for field, display in compare_fields:
            va = ea.get(field)
            vb = eb.get(field)
            if va != vb:
                diffs.append((display, str(va), str(vb)))
        if diffs:
            changed.append((label_fn(ea), diffs))

    return added, removed, changed


# ---------------------------------------------------------------------------
# Per-entity diff definitions
# ---------------------------------------------------------------------------

def _msg_label(m):
    cid = fmt_can_id(m["can_id"], m.get("message_type", "Standard"))
    return f"{m['name']} ({cid}, {m.get('direction') or '?'}, {m['dlc']} bytes, {m['cycle_time']}ms)"

MSG_FIELDS = [
    ("can_id", "CAN ID"),
    ("direction", "Direction"),
    ("dlc", "DLC"),
    ("cycle_time", "Cycle Time (ms)"),
    ("timeout", "Timeout (ms)"),
    ("byte_order", "Byte Order"),
    ("message_type", "Message Type"),
    ("description", "Description"),
]


def _sig_label(s):
    end = s["start_bit"] + s["size_bits"] - 1
    return f"{s['name']} [{s['start_bit']}:{end}] on {s.get('message_name', '?')}"

SIG_FIELDS = [
    ("start_bit", "Start Bit"),
    ("size_bits", "Size (bits)"),
    ("offset", "Offset"),
    ("multiplier", "Multiplier"),
    ("divisor", "Divisor"),
    ("unit", "Unit"),
    ("raw_unit", "Raw Unit"),
    ("raw_min", "Raw Min"),
    ("raw_max", "Raw Max"),
    ("initial_value", "Initial Value"),
    ("default_value", "Default Value"),
    ("error_reaction", "Error Reaction"),
    ("description", "Description"),
]


def _db_label(d):
    t = "NvMem" if d["list_type"] == 2 else "RAM"
    return f"{d['name']} ({t})"

DB_FIELDS = [
    ("list_type", "Type"),
    ("start_address", "Start Address"),
    ("backup_start_address", "Backup Address"),
    ("nv_crc_protected", "NV CRC"),
    ("ram_crc_protected", "RAM CRC"),
    ("default_data_set_count", "Dataset Count"),
]


def _var_label(v):
    return f"{v.get('name', '?')} ({v.get('var_type', '?')}, default={v.get('default_value', '?')}) in {v.get('database', '?')}"

VAR_FIELDS = [
    ("var_type", "Type"),
    ("default_value", "Default"),
    ("min", "Min"),
    ("max", "Max"),
    ("unit", "Unit"),
    ("description", "Description"),
]


def _err_label(e):
    return f"SPN {e.get('spn', '?')} - {e.get('description', '?')} (severity {e.get('severity', '?')})"

ERR_FIELDS = [
    ("description", "Description"),
    ("severity", "Severity"),
    ("set_debounce_ms", "Set Debounce (ms)"),
    ("release_debounce_ms", "Release Debounce (ms)"),
    ("set_threshold", "Set Threshold"),
    ("release_threshold", "Release Threshold"),
    ("comment", "Comment"),
]


def _dm_label(d):
    return f"{d.get('detection', '?')} (bit {d.get('bit', '?')})"

DM_FIELDS = [
    ("bit", "Bit"),
    ("default_fmi", "Default FMI"),
    ("default_fmi_ex", "Default FMI Extension"),
    ("description", "Description"),
]


def _app_label(a):
    return f"{a['name']} ({a['cycle_time']}ms cycle)"

APP_FIELDS = [
    ("cycle_time", "Cycle Time (ms)"),
    ("offset_time", "Offset Time (ms)"),
    ("execution_time", "Execution Time (ms)"),
    ("watchdog_time", "Watchdog Time (ms)"),
    ("watchdog_reaction", "Watchdog Reaction"),
    ("task_priority", "Task Priority"),
    ("safety_level", "Safety Level"),
    ("source_address", "Source Address"),
]


def _proto_label(p):
    return f"{p['name']} ({p['type']})"

PROTO_FIELDS = [
    ("match_version", "MATCH Version"),
    ("protocol_version", "Protocol Version"),
    ("enabled", "Enabled"),
    ("ecu_code", "ECU Code"),
]


def _block_label(b):
    return f"{b['name']} (class={b['class']})"

BLOCK_FIELDS = [
    ("class", "Class"),
    ("key", "Key"),
]


def _pin_label(p):
    parts = [p["name"]]
    if p.get("config"):
        parts.append(f"cfg={p['config']}")
    if p.get("block"):
        parts.append(f"block={p['block']}")
    return " ".join(parts)

PIN_FIELDS = [
    ("config", "Pin Config"),
    ("block", "Connected Block"),
]


# ---------------------------------------------------------------------------
# Report generation
# ---------------------------------------------------------------------------

def _render_section(title, added, removed, changed):
    """Render a diff section as markdown lines. Returns empty list if no changes."""
    if not added and not removed and not changed:
        return []
    lines = [f"## {title}", ""]
    if added:
        lines.append("### Added")
        for label in added:
            lines.append(f"- {label}")
        lines.append("")
    if removed:
        lines.append("### Removed")
        for label in removed:
            lines.append(f"- {label}")
        lines.append("")
    if changed:
        lines.append("### Changed")
        for label, diffs in changed:
            lines.append(f"- **{label}**:")
            for field, old, new in diffs:
                lines.append(f"  - {field}: `{old}` -> `{new}`")
        lines.append("")
    return lines


def _summary_line(title, added, removed, changed):
    """One-line summary for a section, e.g. '2 added, 1 changed'. Returns None if empty."""
    parts = []
    if added:
        parts.append(f"{len(added)} added")
    if removed:
        parts.append(f"{len(removed)} removed")
    if changed:
        parts.append(f"{len(changed)} changed")
    if not parts:
        return None
    return f"- {title}: {', '.join(parts)}"


def generate_report(hdb_a: str, hdb_b: str) -> str:
    """Generate a semantic markdown diff report comparing two .hdb files."""
    print(f"Loading {os.path.basename(hdb_a)}...", file=sys.stderr)
    data_a = load_all(hdb_a)
    print(f"Loading {os.path.basename(hdb_b)}...", file=sys.stderr)
    data_b = load_all(hdb_b)

    # Run all diffs
    sections = []

    # CAN Messages
    msg_diff = diff_entities(
        data_a["messages"], data_b["messages"],
        key_fn=lambda m: m["name"].lower(),
        label_fn=_msg_label,
        compare_fields=MSG_FIELDS,
    )
    sections.append(("CAN Messages", *msg_diff))

    # CAN Signals
    sig_diff = diff_entities(
        data_a["signals"], data_b["signals"],
        key_fn=lambda s: (s.get("message_name", "").lower(), s["name"].lower()),
        label_fn=_sig_label,
        compare_fields=SIG_FIELDS,
    )
    sections.append(("CAN Signals", *sig_diff))

    # Databases
    db_diff = diff_entities(
        data_a["databases"], data_b["databases"],
        key_fn=lambda d: d["name"].lower(),
        label_fn=_db_label,
        compare_fields=DB_FIELDS,
    )
    sections.append(("Databases", *db_diff))

    # Database Variables
    var_diff = diff_entities(
        data_a["db_variables"], data_b["db_variables"],
        key_fn=lambda v: (v.get("database", "").lower(), v.get("name", "").lower()),
        label_fn=_var_label,
        compare_fields=VAR_FIELDS,
    )
    sections.append(("Database Variables", *var_diff))

    # Errors
    err_diff = diff_entities(
        data_a["errors"], data_b["errors"],
        key_fn=lambda e: e.get("spn"),
        label_fn=_err_label,
        compare_fields=ERR_FIELDS,
    )
    sections.append(("Errors", *err_diff))

    # Detection Methods
    dm_diff = diff_entities(
        data_a["detection_methods"], data_b["detection_methods"],
        key_fn=lambda d: d.get("detection"),
        label_fn=_dm_label,
        compare_fields=DM_FIELDS,
    )
    sections.append(("Detection Methods", *dm_diff))

    # ECU Applications
    app_diff = diff_entities(
        data_a["ecu_apps"], data_b["ecu_apps"],
        key_fn=lambda a: a["name"].lower(),
        label_fn=_app_label,
        compare_fields=APP_FIELDS,
    )
    sections.append(("ECU Applications", *app_diff))

    # Protocols
    proto_diff = diff_entities(
        data_a["protocols"], data_b["protocols"],
        key_fn=lambda p: p["name"].lower(),
        label_fn=_proto_label,
        compare_fields=PROTO_FIELDS,
    )
    sections.append(("Protocols", *proto_diff))

    # Blocks
    block_diff = diff_entities(
        data_a["blocks"], data_b["blocks"],
        key_fn=lambda b: b["name"].lower(),
        label_fn=_block_label,
        compare_fields=BLOCK_FIELDS,
    )
    sections.append(("Blocks", *block_diff))

    # Pin Configurations
    pin_diff = diff_entities(
        data_a["pins"], data_b["pins"],
        key_fn=lambda p: p["name"].lower(),
        label_fn=_pin_label,
        compare_fields=PIN_FIELDS,
    )
    sections.append(("Pin Configurations", *pin_diff))

    # --- Build report ---
    lines = [
        "# HDB Diff Report",
        "",
        f"- **A**: `{os.path.basename(hdb_a)}`",
        f"- **B**: `{os.path.basename(hdb_b)}`",
        "",
    ]

    # Project info
    info_a = data_a.get("info", {})
    info_b = data_b.get("info", {})
    info_changes = []
    if info_a.get("pdt_version") != info_b.get("pdt_version"):
        info_changes.append(f"- PDT Version: `{info_a.get('pdt_version', '?')}` -> `{info_b.get('pdt_version', '?')}`")
    if info_a.get("file_format") != info_b.get("file_format"):
        info_changes.append(f"- File Format: `{info_a.get('file_format', '?')}` -> `{info_b.get('file_format', '?')}`")

    # Buses
    bus_a_count = len(data_a["buses"])
    bus_b_count = len(data_b["buses"])

    # Summary
    lines.append("## Summary")
    if info_changes:
        for ic in info_changes:
            lines.append(ic)
    if bus_a_count != bus_b_count:
        lines.append(f"- CAN Buses: {bus_a_count} -> {bus_b_count}")

    for title, added, removed, changed in sections:
        sl = _summary_line(title, added, removed, changed)
        if sl:
            lines.append(sl)

    has_any = any(a or r or c for _, a, r, c in sections) or info_changes or bus_a_count != bus_b_count
    if not has_any:
        lines.append("**No differences found.**")
    lines.append("")

    # Detail sections
    for title, added, removed, changed in sections:
        lines.extend(_render_section(title, added, removed, changed))

    return "\n".join(lines)


def main():
    argp = argparse.ArgumentParser(description="Semantic diff for HYDAC PDT .hdb files")
    argp.add_argument("hdb_a", help="First .hdb file (base)")
    argp.add_argument("hdb_b", help="Second .hdb file (changed)")
    argp.add_argument("--output", "-o", help="Output file (default: stdout)")
    args = argp.parse_args()

    report = generate_report(args.hdb_a, args.hdb_b)

    if args.output:
        Path(args.output).write_text(report, encoding="utf-8")
        print(f"Report written to {args.output}", file=sys.stderr)
    else:
        print(report)


if __name__ == "__main__":
    main()
