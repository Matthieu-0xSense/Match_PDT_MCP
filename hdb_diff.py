"""
Semantic diff for HYDAC PDT .hdb files.

Compares two .hdb archives (ZIP containing XML + binary .dat files) and
produces a human-readable markdown report of changes.

Usage:
    python hdb_diff.py <hdb_a> <hdb_b> [--output report.md]

Requires:
    - Built dotnet-helper (bin/Release/net48/HdbDatReader.exe)
    - PDT installation (auto-discovered or set PDT_DIR env var)
"""

import argparse
import hashlib
import json
import os
import subprocess
import sys
import zipfile
import xml.etree.ElementTree as ET
from pathlib import Path


SCRIPT_DIR = Path(__file__).parent
DOTNET_HELPER_EXE = SCRIPT_DIR / "dotnet-helper" / "bin" / "Release" / "net48" / "HdbDatReader.exe"


def find_pdt_dir() -> str:
    """Auto-discover PDT installation directory."""
    env = os.environ.get("PDT_DIR")
    if env and os.path.isdir(env):
        return env
    base = r"C:\Program Files\Hydac\Project Definition Tool"
    if os.path.isdir(base):
        versions = sorted(os.listdir(base), reverse=True)
        if versions:
            return os.path.join(base, versions[0])
    raise RuntimeError("Cannot find PDT installation. Set PDT_DIR environment variable.")


def get_zip_entry_hash(zf: zipfile.ZipFile, name: str) -> str:
    """SHA256 of a ZIP entry."""
    return hashlib.sha256(zf.read(name)).hexdigest()


def parse_xml_from_zip(zf: zipfile.ZipFile, name: str) -> ET.Element:
    """Parse an XML file from a ZIP, stripping null padding."""
    data = zf.read(name).rstrip(b'\x00')
    return ET.fromstring(data)


def element_identity(elem: ET.Element) -> str:
    """Get identity key for an XML element (Id > Name > tag+index)."""
    id_el = elem.find("Id")
    if id_el is not None and id_el.text:
        return id_el.text
    name_el = elem.find("Name")
    if name_el is not None and name_el.text:
        return name_el.text
    return None


def element_to_flat_dict(elem: ET.Element, prefix: str = "") -> dict:
    """Flatten an XML element to a dict of path -> text values."""
    result = {}
    for child in elem:
        key = f"{prefix}{child.tag}" if prefix else child.tag
        if len(child) > 0:
            result.update(element_to_flat_dict(child, key + "."))
        else:
            result[key] = (child.text or "").strip()
    for attr_name, attr_val in elem.attrib.items():
        key = f"{prefix}@{attr_name}" if prefix else f"@{attr_name}"
        result[key] = attr_val
    return result


def diff_xml_file(root_a: ET.Element, root_b: ET.Element) -> dict:
    """Compare two XML roots element-by-element. Returns diff dict."""
    changes = {"added": [], "removed": [], "changed": []}

    # Build lookup by identity
    def build_index(root):
        index = {}
        for i, child in enumerate(root):
            key = element_identity(child)
            if key is None:
                key = f"#{i}"
            index[key] = child
        return index

    idx_a = build_index(root_a)
    idx_b = build_index(root_b)

    keys_a = set(idx_a.keys())
    keys_b = set(idx_b.keys())

    for key in sorted(keys_b - keys_a):
        elem = idx_b[key]
        name = elem.findtext("Name") or key
        changes["added"].append(name)

    for key in sorted(keys_a - keys_b):
        elem = idx_a[key]
        name = elem.findtext("Name") or key
        changes["removed"].append(name)

    for key in sorted(keys_a & keys_b):
        flat_a = element_to_flat_dict(idx_a[key])
        flat_b = element_to_flat_dict(idx_b[key])
        all_keys = set(flat_a.keys()) | set(flat_b.keys())
        diffs = []
        for k in sorted(all_keys):
            va = flat_a.get(k, "<absent>")
            vb = flat_b.get(k, "<absent>")
            if va != vb:
                diffs.append((k, va, vb))
        if diffs:
            name = idx_a[key].findtext("Name") or key
            changes["changed"].append({"name": name, "fields": diffs})

    return changes


def diff_json(obj_a, obj_b, path: str = "", max_diffs: int = 200) -> list:
    """Deep-diff two JSON-compatible structures. Returns list of (path, old, new)."""
    diffs = []

    if type(obj_a) != type(obj_b):
        diffs.append((path or "(root)", _summarize(obj_a), _summarize(obj_b)))
        return diffs

    if isinstance(obj_a, dict):
        all_keys = set(obj_a.keys()) | set(obj_b.keys())
        for k in sorted(all_keys):
            if k.startswith("$"):  # skip metadata
                continue
            child_path = f"{path}.{k}" if path else k
            if k not in obj_a:
                diffs.append((child_path, "<absent>", _summarize(obj_b[k])))
            elif k not in obj_b:
                diffs.append((child_path, _summarize(obj_a[k]), "<absent>"))
            else:
                diffs.extend(diff_json(obj_a[k], obj_b[k], child_path, max_diffs - len(diffs)))
            if len(diffs) >= max_diffs:
                diffs.append(("...", f"(>{max_diffs} differences, truncated)", ""))
                return diffs
    elif isinstance(obj_a, list):
        for i in range(max(len(obj_a), len(obj_b))):
            child_path = f"{path}[{i}]"
            if i >= len(obj_a):
                diffs.append((child_path, "<absent>", _summarize(obj_b[i])))
            elif i >= len(obj_b):
                diffs.append((child_path, _summarize(obj_a[i]), "<absent>"))
            else:
                diffs.extend(diff_json(obj_a[i], obj_b[i], child_path, max_diffs - len(diffs)))
            if len(diffs) >= max_diffs:
                diffs.append(("...", f"(>{max_diffs} differences, truncated)", ""))
                return diffs
    else:
        if obj_a != obj_b:
            diffs.append((path or "(root)", _summarize(obj_a), _summarize(obj_b)))

    return diffs


def _summarize(obj, max_len: int = 80) -> str:
    """Short string representation of a value for diff display."""
    if obj is None:
        return "null"
    if isinstance(obj, (dict, list)):
        s = json.dumps(obj, ensure_ascii=False)
        if len(s) > max_len:
            return s[:max_len - 3] + "..."
        return s
    return str(obj)


def dump_dats(hdb_path: str, pdt_dir: str) -> dict:
    """Call HdbDatReader.exe dump-all and return parsed JSON."""
    if not DOTNET_HELPER_EXE.exists():
        raise RuntimeError(
            f"dotnet-helper not built. Run:\n"
            f"  cd {SCRIPT_DIR / 'dotnet-helper'}\n"
            f"  dotnet build -c Release"
        )
    result = subprocess.run(
        [str(DOTNET_HELPER_EXE), hdb_path, pdt_dir, "dump-all"],
        capture_output=True, text=True, timeout=120,
    )
    if result.returncode != 0:
        raise RuntimeError(f"dotnet helper failed: {result.stderr.strip()}")
    return json.loads(result.stdout)


def generate_report(hdb_a: str, hdb_b: str, pdt_dir: str) -> str:
    """Generate a markdown diff report comparing two .hdb files."""
    lines = []
    lines.append(f"# HDB Diff Report")
    lines.append(f"")
    lines.append(f"- **A**: `{os.path.basename(hdb_a)}`")
    lines.append(f"- **B**: `{os.path.basename(hdb_b)}`")
    lines.append("")

    za = zipfile.ZipFile(hdb_a)
    zb = zipfile.ZipFile(hdb_b)

    names_a = {e.filename for e in za.infolist()}
    names_b = {e.filename for e in zb.infolist()}

    # --- XML files ---
    xml_files_a = sorted(n for n in names_a if n.endswith(".xml"))
    xml_files_b = sorted(n for n in names_b if n.endswith(".xml"))
    all_xml = sorted(set(xml_files_a) | set(xml_files_b))

    xml_changed = 0
    xml_identical = 0
    xml_sections = []

    for name in all_xml:
        if name not in names_a:
            xml_changed += 1
            xml_sections.append(f"### {name}\n**Added** (only in B)\n")
            continue
        if name not in names_b:
            xml_changed += 1
            xml_sections.append(f"### {name}\n**Removed** (only in A)\n")
            continue

        # Quick binary check
        hash_a = get_zip_entry_hash(za, name)
        hash_b = get_zip_entry_hash(zb, name)
        if hash_a == hash_b:
            xml_identical += 1
            continue

        # Semantic diff
        xml_changed += 1
        try:
            root_a = parse_xml_from_zip(za, name)
            root_b = parse_xml_from_zip(zb, name)
            changes = diff_xml_file(root_a, root_b)

            section = [f"### {name}\n"]
            if changes["added"]:
                section.append("**Added:**")
                for n in changes["added"]:
                    section.append(f"- {n}")
                section.append("")
            if changes["removed"]:
                section.append("**Removed:**")
                for n in changes["removed"]:
                    section.append(f"- {n}")
                section.append("")
            if changes["changed"]:
                section.append("**Changed:**")
                for c in changes["changed"]:
                    section.append(f"- **{c['name']}**:")
                    for field, old, new in c["fields"]:
                        section.append(f"  - `{field}`: `{old}` -> `{new}`")
                section.append("")
            xml_sections.append("\n".join(section))
        except Exception as e:
            xml_sections.append(f"### {name}\n*Error parsing: {e}*\n")

    lines.append("## Summary")
    lines.append(f"- XML files: {xml_changed} changed, {xml_identical} identical")

    # --- .dat files ---
    dat_files_a = sorted(n for n in names_a if n.endswith(".dat"))
    dat_files_b = sorted(n for n in names_b if n.endswith(".dat"))

    # Quick binary check for .dat files
    dat_identical = 0
    dat_changed_names = []
    for name in sorted(set(dat_files_a) | set(dat_files_b)):
        if name not in names_a or name not in names_b:
            dat_changed_names.append(name)
            continue
        if get_zip_entry_hash(za, name) == get_zip_entry_hash(zb, name):
            dat_identical += 1
        else:
            dat_changed_names.append(name)

    dat_sections = []
    if dat_changed_names:
        try:
            dats_a = dump_dats(hdb_a, pdt_dir)
            dats_b = dump_dats(hdb_b, pdt_dir)

            for name in dat_changed_names:
                if name not in dats_a:
                    dat_sections.append(f"### {name}\n**Added** (only in B)\n")
                    continue
                if name not in dats_b:
                    dat_sections.append(f"### {name}\n**Removed** (only in A)\n")
                    continue

                diffs = diff_json(dats_a[name], dats_b[name])
                if not diffs:
                    dat_identical += 1
                    continue

                section = [f"### {name}\n"]
                for path, old, new in diffs:
                    section.append(f"- `{path}`: `{old}` -> `{new}`")
                section.append("")
                dat_sections.append("\n".join(section))
        except Exception as e:
            dat_sections.append(f"### .dat files\n*Error dumping: {e}*\n")

    lines.append(f"- .dat files: {len(dat_changed_names)} changed, {dat_identical} identical")
    lines.append("")

    if xml_sections:
        lines.append("## XML Changes")
        lines.append("")
        lines.extend(xml_sections)

    if dat_sections:
        lines.append("## .dat Changes")
        lines.append("")
        lines.extend(dat_sections)

    if not xml_sections and not dat_sections:
        lines.append("**No differences found.**")

    za.close()
    zb.close()
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description="Semantic diff for HYDAC PDT .hdb files")
    parser.add_argument("hdb_a", help="First .hdb file (base)")
    parser.add_argument("hdb_b", help="Second .hdb file (changed)")
    parser.add_argument("--output", "-o", help="Output file (default: stdout)")
    parser.add_argument("--pdt-dir", help="PDT installation directory (auto-discovered if omitted)")
    args = parser.parse_args()

    pdt_dir = args.pdt_dir or find_pdt_dir()
    report = generate_report(args.hdb_a, args.hdb_b, pdt_dir)

    if args.output:
        Path(args.output).write_text(report, encoding="utf-8")
        print(f"Report written to {args.output}")
    else:
        print(report)


if __name__ == "__main__":
    main()
