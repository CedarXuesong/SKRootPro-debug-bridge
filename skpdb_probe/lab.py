#!/usr/bin/env python3
"""Typed memory workbench for the SKPDB bridge.

pc_client.py answers "what are the bytes at this address". Finding a value whose
address is not known yet needs the other direction, and that is what this tool
adds, on top of the same wire protocol:

  maps     /proc/<pid>/maps with every mapping labelled and sized
  view     a typed slice of memory (f32/f64/u32/u64/ptr) with mapping and
           module-relative addresses
  chain    a pointer chain (module+offset or absolute) hop by hop, validating
           each dereference against the target's mappings
  ptrs     reverse pointer search: every qword that equals an address
  scan     typed value scan (eq/gt/lt/between/known-unknown) over a region set
  snap     a "known unknown" snapshot: every plausible value of a type
  filter   refine a snapshot by changed/unchanged/increased/decreased/eq
  watch    poll one address and print a time series

The scan family reads through the same strict kernel path as `memread`, so it
never faults a page into the target; values that are not resident are skipped
rather than pulled in. Filtering happens on this side of the wire, which is why
a snapshot stores only plausible candidates instead of every address.

numpy is required for the typed scans. The bundled interpreter has it:

  python -m pip install numpy
"""

import argparse
import getpass
import os
import re
import struct
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import pc_client  # noqa: E402

try:
    import numpy as np
except ImportError:  # pragma: no cover - the non-scan commands still work
    np = None

PAGE = 4096
# type -> (numpy dtype, struct format, size). struct has no one-byte integers,
# so the byte types spell themselves as b/B while numpy keeps <i1/<u1.
ITEM = {
    "f32": ("<f4", "f", 4),
    "f64": ("<f8", "d", 8),
    "i8": ("<i1", "b", 1),
    "i16": ("<i2", "h", 2),
    "i32": ("<i4", "i", 4),
    "i64": ("<i8", "q", 8),
    "u8": ("<u1", "B", 1),
    "u16": ("<u2", "H", 2),
    "u32": ("<u4", "I", 4),
    "u64": ("<u8", "Q", 8),
    "ptr": ("<u8", "Q", 8),
    "ptr32": ("<u4", "I", 4),
}
# Regions a value scan starts from by default: writable anonymous memory, which
# is where a heap allocator keeps a live object. Java and ART regions are left
# out because a native engine never stores its actors there, and reading them
# only costs bandwidth.
ANON_SKIP = ("dalvik", "art", "jit", "stack", "vvar", "vectors", "vsyscall", "profiler")
CHUNK = 900 * 1024


class Mapping:
    __slots__ = ("start", "end", "perms", "offset", "inode", "path")

    def __init__(self, start, end, perms, offset, inode, path):
        self.start = start
        self.end = end
        self.perms = perms
        self.offset = offset
        self.inode = inode
        self.path = path

    @property
    def size(self):
        return self.end - self.start

    def label(self):
        return self.path or "[anon]"

    def contains(self, address):
        return self.start <= address < self.end


def parse_maps(text):
    out = []
    for line in text.splitlines():
        parts = line.split(None, 5)
        if len(parts) < 5:
            continue
        try:
            start, end = (int(value, 16) for value in parts[0].split("-"))
            offset = int(parts[2], 16)
            inode = int(parts[4])
        except ValueError:
            continue
        path = parts[5].strip() if len(parts) > 5 else ""
        out.append(Mapping(start, end, parts[1], offset, inode, path))
    return out


def fetch_maps(channel, pid):
    body = pc_client.request(channel, pc_client.MEM_MAPS + struct.pack("!I", pid),
                             pc_client.MEM_MAPS)
    return parse_maps(body.decode(errors="replace"))


def modules(maps):
    """File-backed mappings folded into one entry per module image."""
    table = {}
    for entry in maps:
        if not entry.path.startswith("/"):
            continue
        base, size = table.get(entry.path, (entry.start, 0))
        table[entry.path] = (min(base, entry.start), size + entry.size)
    return sorted(((base, size, path) for path, (base, size) in table.items()),
                  key=lambda row: row[0])


def find_module(maps, name):
    best = None
    for base, size, path in modules(maps):
        if path == name or path.endswith("/" + name) or name in path:
            if best is None or base < best[0]:
                best = (base, size, path)
    return best


def describe(maps, address):
    for entry in maps:
        if entry.contains(address):
            if entry.path.startswith("/"):
                base = find_module(maps, entry.path)
                if base:
                    return f"{Path(entry.path).name}+0x{address - base[0]:x}"
                return f"{Path(entry.path).name}+0x{address - entry.start + entry.offset:x}"
            return f"{entry.label()}+0x{address - entry.start:x}"
    return "unmapped"


def dump_chunks(channel, pid, start, end, mode, chunk=CHUNK):
    """Streams every resident run in [start, end) with one D request per run."""
    cursor = start
    while cursor < end:
        payload = (pc_client.MEM_DUMP +
                   struct.pack("!IQQQIB", pid, start, end, cursor, chunk, mode))
        body = pc_client.request(channel, payload, pc_client.MEM_DUMP)
        data_start, next_cursor, size = struct.unpack_from("!QQI", body, 0)
        data = body[20:20 + size]
        if data:
            yield data_start, data
        if next_cursor <= cursor:
            return
        cursor = next_cursor


def read_exact(channel, pid, address, length, mode):
    return pc_client.request(
        channel,
        pc_client.MEM_READ + struct.pack("!IQIB", pid, address, length, mode),
        pc_client.MEM_READ)[1:]


def read_qword(channel, pid, address, mode, size=8):
    data = read_exact(channel, pid, address, size, mode)
    if len(data) < size:
        return None
    return int.from_bytes(data, "little")


# A sparse heap makes `D` the wrong tool: every hole turns into a request, and an
# Android game reserves gigabytes it has never touched. One `P` bitmap covers
# 256 MiB (32 KiB of bitmap), and the resident runs it finds are then fetched
# with `S`, 64 ranges per request. That is the difference between a few hundred
# round trips and one per hole.
RESIDENT_SPAN = 256 * 2**20
SCATTER_RANGE = 256 * 1024


def resident_runs(channel, pid, start, end, mode):
    cursor = start
    while cursor < end:
        stop = min(cursor + RESIDENT_SPAN, end)
        for run_start, run_end in pc_client.resident_runs(channel, pid, cursor,
                                                          stop - cursor, mode):
            yield run_start, run_end
        cursor = stop


def iter_resident(channel, pid, start, end, mode, chunk=CHUNK):
    """Yields (address, bytes) for resident memory only, never faulting a hole."""
    batch = []
    batch_bytes = 0

    def flush():
        nonlocal batch, batch_bytes
        if not batch:
            return
        for address, data, *_ in pc_client.scatter_read(channel, pid, batch, mode):
            if data:
                yield address, data
        batch, batch_bytes = [], 0

    for run_start, run_end in resident_runs(channel, pid, start, end, mode):
        cursor = run_start
        while cursor < run_end:
            take = min(run_end - cursor, SCATTER_RANGE)
            if batch and (len(batch) >= 64 or batch_bytes + take > chunk):
                yield from flush()
            batch.append((cursor, take))
            batch_bytes += take
            cursor += take
    yield from flush()


# --------------------------------------------------------------------------
# commands
# --------------------------------------------------------------------------

def cmd_maps(args, channel):
    maps = fetch_maps(channel, args.pid)
    wanted = re.compile(args.filter) if args.filter else None
    shown = 0
    total = 0
    for entry in maps:
        if wanted and not wanted.search(entry.path or ""):
            continue
        shown += 1
        total += entry.size
        print(f"{entry.start:012x}-{entry.end:012x} {entry.perms} "
              f"{entry.size / 1024:9.1f} KiB off=0x{entry.offset:x} {entry.label()}")
    print(f"mappings={shown}/{len(maps)} bytes={total} ({total / 2**20:.1f} MiB)",
          file=sys.stderr)
    return 0


def cmd_modules(args, channel):
    maps = fetch_maps(channel, args.pid)
    wanted = re.compile(args.filter) if args.filter else None
    rows = [row for row in modules(maps) if not wanted or wanted.search(row[2])]
    for base, size, path in rows:
        print(f"{base:012x} {size / 1024:9.1f} KiB  {path}")
    print(f"modules={len(rows)}", file=sys.stderr)
    return 0


def cmd_view(args, channel):
    maps = fetch_maps(channel, args.pid)
    _dtype, code, size = ITEM[args.type]
    count = max(1, args.length // size)
    data = read_exact(channel, args.pid, args.address, count * size, args.mode)
    count = len(data) // size
    label = describe(maps, args.address)
    print(f"# {args.address:#x} {label} type={args.type} items={count}")
    for index in range(count):
        address = args.address + index * size
        raw = data[index * size:(index + 1) * size]
        value = struct.unpack("<" + code, raw)[0]
        ascii_part = "".join(chr(b) if 32 <= b < 127 else "." for b in raw)
        extra = ""
        if args.type in ("ptr", "ptr32") and value:
            extra = "  -> " + describe(maps, value)
        print(f"{address:012x}  {raw.hex():<16} {value!r:<24} {ascii_part}{extra}")
    return 0


def cmd_chain(args, channel):
    maps = fetch_maps(channel, args.pid)
    expression = args.base
    match = re.match(r"^(?P<module>[^+]+)\+(?P<offset>0x[0-9a-fA-F]+|\d+)$", expression)
    if match:
        found = find_module(maps, match.group("module"))
        if not found:
            print(f"module {match.group('module')!r} is not mapped in pid {args.pid}",
                  file=sys.stderr)
            return 2
        address = found[0] + int(match.group("offset"), 0)
        print(f"base {found[2]} @ {found[0]:#x} + {int(match.group('offset'), 0):#x} "
              f"= {address:#x} ({describe(maps, address)})")
    else:
        address = int(expression, 0)
        print(f"base {address:#x} ({describe(maps, address)})")
    for step, offset in enumerate(args.offsets):
        pointer = read_qword(channel, args.pid, address, args.mode, args.ptr_size)
        if pointer is None:
            print(f"step {step}: read at {address:#x} failed (hole or unmapped)")
            return 1
        target = pointer + int(offset, 0)
        print(f"step {step}: [{address:#x}] -> {pointer:#x} + {int(offset, 0):#x} "
              f"= {target:#x} ({describe(maps, target)})")
        if pointer and not any(entry.contains(pointer) for entry in maps):
            print(f"  warning: {pointer:#x} is outside every mapping; the chain is stale",
                  file=sys.stderr)
        address = target
    if args.read:
        value_type = args.read_type
        _dtype, code, size = ITEM[value_type]
        data = read_exact(channel, args.pid, address, args.read, args.mode)
        values = [struct.unpack_from("<" + code, data, offset)[0]
                  for offset in range(0, len(data) - size + 1, size)]
        print(f"{address:#x} {value_type}: {values}")
    return 0


def cmd_ptrs(args, channel):
    """Every qword equal to `target`: the reverse direction of a pointer chain."""
    maps = fetch_maps(channel, args.pid)
    pattern = struct.pack("<Q", args.target)
    hits = 0
    for entry in select_regions(maps, args.region, writable_only=True):
        cursor = entry.start
        remaining = args.limit - hits
        while cursor < entry.end and remaining > 0:
            end = min(cursor + 4 * 2**20, entry.end)
            payload = (pc_client.MEM_SCAN +
                       struct.pack("!IQQIHBB", args.pid, cursor, end, remaining,
                                   len(pattern), args.mode, 0) + pattern)
            try:
                body = pc_client.request(channel, payload, pc_client.MEM_SCAN)
            except RuntimeError:
                break
            found = struct.unpack_from("!I", body, 0)[0]
            for index in range(found):
                address = struct.unpack_from("!Q", body, 20 + index * 8)[0]
                print(f"{address:012x}  {entry.label()}+0x{address - entry.start:x}")
            hits += found
            remaining -= found
            cursor = end
    print(f"hits={hits}", file=sys.stderr)
    return 0


def select_regions(maps, region, writable_only=True):
    if region == "all":
        return [entry for entry in maps if "r" in entry.perms]
    if region == "anon":
        out = []
        for entry in maps:
            if "r" not in entry.perms:
                continue
            if writable_only and "w" not in entry.perms:
                continue
            name = entry.path
            if name and not name.startswith("[anon") and name != "[heap]":
                continue
            if any(skip in name for skip in ANON_SKIP):
                continue
            out.append(entry)
        return out
    wanted = re.compile(region)
    return [entry for entry in maps if "r" in entry.perms and
            (wanted.search(entry.path or "") or (not entry.path and wanted.search("[anon]")))]


def iter_values(channel, pid, region, modes, dtype, size):
    """Yields (start, np.array) for every resident chunk of the selected regions."""
    for entry in region:
        for data_start, data in iter_resident(channel, pid, entry.start, entry.end,
                                              modes, chunk=CHUNK):
            offset = (-data_start) % size
            if len(data) - offset < size:
                continue
            count = (len(data) - offset) // size
            yield data_start + offset, np.frombuffer(data, dtype=dtype, count=count,
                                                     offset=offset)


def plausible(values, args, dtype):
    """Candidate filter for a "known unknown" pass.

    A first pass cannot store every address, so it keeps only values a
    coordinate could hold: finite, inside the configured magnitude, and (for
    floats) not so small that it is rounding noise.
    """
    if dtype.startswith("<f"):
        mask = np.isfinite(values)
        mask &= (np.abs(values) >= args.min_value) & (np.abs(values) <= args.max_value)
        mask &= values != 0
        return mask
    mask = (values >= args.min_value) & (values <= args.max_value)
    return mask if args.max_value != float("inf") else mask


def compare_initial(values, args):
    if args.cmp == "eq":
        if values.dtype.kind == "f":
            return np.isclose(values, args.value, rtol=0.0, atol=args.tolerance)
        return values == args.value
    if args.cmp == "gt":
        return values > args.value
    if args.cmp == "lt":
        return values < args.value
    if args.cmp == "between":
        return (values >= args.value) & (values <= args.value2)
    if args.cmp == "unknown":
        return plausible(values, args, values.dtype.str)
    raise SystemExit(f"unsupported compare {args.cmp} for a first pass")


def collect(args, channel):
    dtype, _code, size = ITEM[args.type]
    if np is None:
        raise SystemExit("numpy is required for scans; run this with the bundled "
                         "python interpreter (see the module docstring)")
    maps = fetch_maps(channel, args.pid)
    regions = select_regions(maps, args.region)
    addresses = []
    values = []
    scanned = 0
    started = time.time()
    for base, chunk in iter_values(channel, args.pid, regions, args.mode, dtype, size):
        scanned += chunk.nbytes
        mask = compare_initial(chunk, args)
        index = np.nonzero(mask)[0]
        if index.size:
            room = args.limit - len(addresses)
            index = index[:room]
            addresses.append(base + index.astype(np.uint64) * size)
            values.append(chunk[index])
        if len(addresses) and sum(len(part) for part in addresses) >= args.limit:
            break
    if not addresses:
        print(f"hits=0 scanned={scanned / 2**20:.1f} MiB regions={len(regions)} "
              f"{time.time() - started:.1f}s", file=sys.stderr)
        return 0
    found_addresses = np.concatenate(addresses)
    found_values = np.concatenate(values)
    print(f"hits={len(found_addresses)} scanned={scanned / 2**20:.1f} MiB "
          f"regions={len(regions)} {time.time() - started:.1f}s", file=sys.stderr)
    if args.out:
        np.savez_compressed(args.out, address=found_addresses, value=found_values)
        print(f"saved={args.out}", file=sys.stderr)
    for address, value in list(zip(found_addresses, found_values))[:args.show]:
        print(f"{int(address):012x}  {describe(maps, int(address))}  {value!r}")
    if len(found_addresses) > args.show:
        print(f"... {len(found_addresses) - args.show} more", file=sys.stderr)
    return 0


def group_runs(addresses, size, max_ranges=64, max_bytes=CHUNK):
    """Merges addresses into contiguous runs so one read covers many of them."""
    runs = []
    run_start = None
    run_count = 0
    for address in addresses:
        address = int(address)
        if run_start is not None and address == run_start + run_count * size:
            run_count += 1
            if run_count * size >= max_bytes:
                runs.append((run_start, run_count * size))
                run_start = None
                run_count = 0
            continue
        if run_start is not None:
            runs.append((run_start, run_count * size))
        run_start, run_count = address, 1
    if run_start is not None:
        runs.append((run_start, run_count * size))
    return runs


def read_runs(channel, pid, runs, mode):
    """Reads runs with S in batches that fit one frame; yields (address, bytes)."""
    batch = []
    batch_bytes = 0
    for run_start, run_length in runs:
        if batch and (len(batch) >= pc_client.MAX_SCATTER_RANGES or
                      batch_bytes + run_length > CHUNK):
            for address, data, *_ in pc_client.scatter_read(channel, pid, batch, mode):
                yield address, data
            batch, batch_bytes = [], 0
        batch.append((run_start, run_length))
        batch_bytes += run_length
    if batch:
        for address, data, *_ in pc_client.scatter_read(channel, pid, batch, mode):
            yield address, data


def cmd_filter(args, channel):
    if np is None:
        raise SystemExit("numpy is required for scans")
    saved = np.load(args.source)
    addresses = saved["address"]
    previous = saved["value"]
    maps = fetch_maps(channel, args.pid)
    runs = group_runs(addresses, previous.dtype.itemsize)
    keep_address = []
    keep_value = []
    for run_start, data in read_runs(channel, args.pid, runs, args.mode):
        if not data:
            continue
        current = np.frombuffer(data, dtype=previous.dtype)
        begin = int(np.searchsorted(addresses, run_start, side="left"))
        end = int(np.searchsorted(addresses, run_start + len(data), side="left"))
        if end <= begin:
            continue
        before = previous[begin:end]
        offsets = (addresses[begin:end] - run_start) // previous.dtype.itemsize
        offsets = offsets[offsets < len(current)]
        before = before[:len(offsets)]
        now = current[offsets.astype(np.int64)]
        mask = filter_mask(before, now, args)
        keep_address.append(addresses[begin:begin + len(mask)][mask])
        keep_value.append(now[mask])
    if not keep_address:
        print(f"survivors=0 of {len(addresses)}", file=sys.stderr)
        return 0
    result_address = np.concatenate(keep_address)
    result_value = np.concatenate(keep_value)
    print(f"survivors={len(result_address)} of {len(addresses)}", file=sys.stderr)
    if args.out:
        np.savez_compressed(args.out, address=result_address, value=result_value)
        print(f"saved={args.out}", file=sys.stderr)
    for address, value in list(zip(result_address, result_value))[:args.show]:
        print(f"{int(address):012x}  {describe(maps, int(address))}  {value!r}")
    if len(result_address) > args.show:
        print(f"... {len(result_address) - args.show} more", file=sys.stderr)
    return 0


def filter_mask(before, after, args):
    if args.cmp == "changed":
        return after != before
    if args.cmp == "unchanged":
        return after == before
    if args.cmp == "increased":
        return after > before
    if args.cmp == "decreased":
        return after < before
    if args.cmp == "eq":
        if after.dtype.kind == "f":
            return np.isclose(after, args.value, rtol=0.0, atol=args.tolerance)
        return after == args.value
    if args.cmp == "gt":
        return after > args.value
    if args.cmp == "lt":
        return after < args.value
    if args.cmp == "between":
        return (after >= args.value) & (after <= args.value2)
    raise SystemExit(f"unsupported compare {args.cmp}")


def cmd_watch(args, channel):
    _dtype, code, size = ITEM[args.type]
    maps = fetch_maps(channel, args.pid)
    print(f"# {args.address:#x} {describe(maps, args.address)} type={args.type} "
          f"count={args.count} interval={args.interval}s")
    started = time.time()
    for index in range(args.count):
        data = read_exact(channel, args.pid, args.address, size, args.mode)
        if len(data) < size:
            print(f"{index:5d} {time.time() - started:8.3f}  <hole>")
        else:
            value = struct.unpack("<" + code, data)[0]
            print(f"{index:5d} {time.time() - started:8.3f}  {value!r}")
        if index + 1 < args.count:
            time.sleep(args.interval)
    return 0


# --------------------------------------------------------------------------
# command line
# --------------------------------------------------------------------------

def build_parser():
    parser = argparse.ArgumentParser(description="SKPDB typed memory workbench")
    parser.add_argument("host", help="device address")
    parser.add_argument("--port", type=int, default=39876)
    parser.add_argument("--knock-port", type=int, default=39877)
    parser.add_argument("--no-knock", action="store_true")
    parser.add_argument("--legacy-trust", action="store_true")
    parser.add_argument("--timeout", type=float, default=120)
    parser.add_argument("--chunk", type=int, default=900 * 1024)
    parser.add_argument("--mode", choices=sorted(pc_client.MEM_MODES), default="auto")
    sub = parser.add_subparsers(dest="command", required=True)

    def add(name, help_text):
        command = sub.add_parser(name, help=help_text)
        command.add_argument("pid", type=int)
        return command

    maps_cmd = add("maps", "list mappings")
    maps_cmd.add_argument("--filter")

    modules_cmd = add("modules", "list file-backed modules")
    modules_cmd.add_argument("--filter")

    view_cmd = add("view", "typed view of one address")
    view_cmd.add_argument("address", type=lambda text: int(text, 0))
    view_cmd.add_argument("--length", type=lambda text: int(text, 0), default=0x100)
    view_cmd.add_argument("--type", choices=sorted(ITEM), default="u8")

    chain_cmd = add("chain", "walk a pointer chain")
    chain_cmd.add_argument("base", help="address, or module+0xoffset")
    chain_cmd.add_argument("offsets", nargs="*", help="one offset per hop")
    chain_cmd.add_argument("--ptr-size", type=int, choices=(4, 8), default=8)
    chain_cmd.add_argument("--read", type=lambda text: int(text, 0), default=0)
    chain_cmd.add_argument("--read-type", choices=sorted(ITEM), default="f32")

    ptrs_cmd = add("ptrs", "find qwords that equal an address")
    ptrs_cmd.add_argument("target", type=lambda text: int(text, 0))
    ptrs_cmd.add_argument("--region", default="anon")
    ptrs_cmd.add_argument("--limit", type=int, default=256)

    for name, help_text in (("scan", "typed value scan"), ("snap", "unknown-value snapshot")):
        command = add(name, help_text)
        command.add_argument("--type", choices=sorted(ITEM), default="f32")
        command.add_argument("--region", default="anon")
        command.add_argument("--cmp", default="unknown" if name == "snap" else "eq",
                             choices=("eq", "gt", "lt", "between", "unknown"))
        command.add_argument("--value", type=float, default=0.0)
        command.add_argument("--value2", type=float, default=0.0)
        command.add_argument("--tolerance", type=float, default=0.0)
        command.add_argument("--min-value", type=float, default=0.0)
        command.add_argument("--max-value", type=float, default=1e7)
        command.add_argument("--limit", type=int, default=200000)
        command.add_argument("--show", type=int, default=20)
        command.add_argument("--out")

    filter_cmd = add("filter", "refine a snapshot")
    filter_cmd.add_argument("source")
    filter_cmd.add_argument("--cmp", required=True,
                            choices=("changed", "unchanged", "increased", "decreased",
                                     "eq", "gt", "lt", "between"))
    filter_cmd.add_argument("--value", type=float, default=0.0)
    filter_cmd.add_argument("--value2", type=float, default=0.0)
    filter_cmd.add_argument("--tolerance", type=float, default=0.0)
    filter_cmd.add_argument("--show", type=int, default=20)
    filter_cmd.add_argument("--out")

    watch_cmd = add("watch", "poll one address")
    watch_cmd.add_argument("address", type=lambda text: int(text, 0))
    watch_cmd.add_argument("--type", choices=sorted(ITEM), default="f32")
    watch_cmd.add_argument("--count", type=int, default=20)
    watch_cmd.add_argument("--interval", type=float, default=0.1)
    return parser


COMMANDS = {
    "maps": cmd_maps,
    "modules": cmd_modules,
    "view": cmd_view,
    "chain": cmd_chain,
    "ptrs": cmd_ptrs,
    "scan": collect,
    "snap": collect,
    "filter": cmd_filter,
    "watch": cmd_watch,
}


def connect(args):
    key = os.environ.get("SKPDB_KEY") or getpass.getpass("SKP key: ")
    channel_args = argparse.Namespace(
        host=args.host, port=args.port, knock_port=args.knock_port,
        timeout=args.timeout, chunk=args.chunk, key=key.encode(),
        no_knock=args.no_knock, legacy_trust=args.legacy_trust)
    return pc_client.connect(channel_args)


def main(argv=None):
    args = build_parser().parse_args(argv)
    args.mode = pc_client.MEM_MODES[args.mode]
    channel = connect(args)
    try:
        return COMMANDS[args.command](args, channel)
    finally:
        pc_client.close_session(channel)


if __name__ == "__main__":
    sys.exit(main())
