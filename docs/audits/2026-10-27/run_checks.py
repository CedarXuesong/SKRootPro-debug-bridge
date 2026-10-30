#!/usr/bin/env python3
"""Offline audit reproductions using production C++ sources; Linux/WSL only.

No phone, privileged operation, or network listener is used. Memory fixtures
belong to the test process. Only the SKRoot kernel backend is substituted.
Exit 0 means the documented defects were reproduced, not that they are fixed.
"""

import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[3]
SRC = ROOT / "skpdb_probe" / "src"
HERE = Path(__file__).resolve().parent


def main():
    if sys.platform != "linux":
        raise SystemExit("Run this script under Linux/WSL with clang++ and AddressSanitizer.")
    compiler = shutil.which("clang++")
    if not compiler:
        raise SystemExit("clang++ is required")
    with tempfile.TemporaryDirectory(prefix="skpdb-audit-20261027-") as temp:
        build = Path(temp)
        (build / "module_err_def.h").write_text("#pragma once\nenum class KModErr { OK };\n")
        (build / "kernel_module_kit_umbrella.h").write_text(
            '#pragma once\n#include <cstring>\n#include <fcntl.h>\n#include <string>\n'
            '#include "module_err_def.h"\n'
            'namespace kernel_module { inline std::string get_kernel_version() '
            '{ return "offline-stub"; } }\n'
        )
        targets = {
            "transport": ["common/stream.cpp", "common/auth.cpp", "common/crypto.cpp",
                          "wireless/socket_stream.cpp"],
            "memory": ["memory/memread.cpp", "memory/memwrite.cpp", "memory/memscan.cpp"],
            "symbols": ["memory/symtab.cpp"],
        }
        env = dict(os.environ, ASAN_OPTIONS="detect_leaks=0:abort_on_error=0")
        for target, sources in targets.items():
            exe = build / target
            command = [compiler, "-std=c++20", "-O1", "-g", "-fno-omit-frame-pointer",
                       "-ffunction-sections", "-fdata-sections", "-Wl,--gc-sections",
                       "-fsanitize=address", "-pthread", "-include", "cstdint",
                       "-I", str(build), "-I", str(SRC),
                       str(HERE / (target + "_checks.cpp"))]
            command.extend(str(SRC / source) for source in sources)
            command.extend(["-ldl", "-o", str(exe)])
            cp = subprocess.run(command, capture_output=True, text=True, timeout=60)
            if cp.returncode:
                print(cp.stdout + cp.stderr)
                raise SystemExit("Compilation failed: " + target)
            cp = subprocess.run([str(exe), str(build / "symbols-fixture.elf")], env=env,
                                capture_output=True, text=True, timeout=10)
            if target == "symbols":
                if cp.returncode == 0 or "heap-buffer-overflow" not in cp.stderr:
                    print(cp.stdout + cp.stderr)
                    raise SystemExit("Symbol parser defect was not reproduced")
                print("REPRODUCED symbols: AddressSanitizer heap-buffer-overflow")
                print("\n".join(cp.stderr.splitlines()[:12]))
            else:
                print(cp.stdout, end="")
                if cp.returncode:
                    print(cp.stderr)
                    raise SystemExit("Audit reproduction failed: " + target)
        print("All 6 documented offline reproductions confirmed; production files unchanged.")


if __name__ == "__main__":
    main()
