"""Formatting helpers for CAN messages and signals."""


def fmt_can_id(can_id: int, msg_type: str) -> str:
    """Format CAN ID as hex, with 0x prefix."""
    if msg_type == "Extended":
        return f"0x{can_id:08X}"
    return f"0x{can_id:03X}"


def fmt_message(msg: dict, include_signals: bool = True) -> str:
    """Format a CAN message for display."""
    lines = [
        f"**{msg['name']}**",
        f"  CAN ID: {fmt_can_id(msg['can_id'], msg['message_type'])} ({msg['can_id']})",
        f"  Type: {msg['message_type']}  |  Byte Order: {msg['byte_order']}  |  DLC: {msg['dlc']}",
        f"  Direction: {msg['direction'] or 'unknown'}",
        f"  Cycle Time: {msg['cycle_time']} ms  |  Timeout: {msg['timeout']} ms",
    ]
    if msg["description"]:
        lines.append(f"  Description: {msg['description']}")
    if msg["is_muxed"]:
        lines.append("  Multiplexed: yes")

    if include_signals and msg["signals"]:
        lines.append(f"  Signals ({len(msg['signals'])}):")
        for sig in msg["signals"]:
            scaling = ""
            if sig["multiplier"] != 1 or sig["divisor"] != 1 or sig["offset"] != 0:
                scaling = f"  (raw * {sig['multiplier']:.6g} + {sig['offset']:.6g}) / {sig['divisor']:.6g}"
            lines.append(
                f"    - {sig['name']:30s}  bits [{sig['start_bit']}:{sig['start_bit']+sig['size_bits']-1}]"
                f"  {sig['unit'] or sig['raw_unit']}"
                f"  range [{sig['raw_min']}..{sig['raw_max']}]{scaling}"
            )
    return "\n".join(lines)


def fmt_signal(sig: dict, msg_name: str = "") -> str:
    """Format a CAN signal for display."""
    scaling = f"(raw * {sig['multiplier']:.6g} + {sig['offset']:.6g}) / {sig['divisor']:.6g}"
    lines = [
        f"**{sig['name']}**" + (f"  (message: {msg_name})" if msg_name else ""),
        f"  Signal ID: {sig['guid']}",
        f"  Message ID (OwnerId): {sig['owner_id']}",
        f"  Bits: [{sig['start_bit']}:{sig['start_bit']+sig['size_bits']-1}]  ({sig['size_bits']} bits)",
        f"  Raw: {sig['raw_unit']}  range [{sig['raw_min']}..{sig['raw_max']}]",
        f"  Scaled: {sig['unit']}  formula: {scaling}",
        f"  Initial: {sig['initial_value']}  |  Default: {sig['default_value']}",
        f"  Error Reaction: {sig['error_reaction']}",
    ]
    if sig["description"]:
        lines.append(f"  Description: {sig['description']}")
    lines.append(f"  XPath: .//CanSignalDataObject[OwnerId='{sig['owner_id']}'][Name='{sig['name']}']")
    return "\n".join(lines)
