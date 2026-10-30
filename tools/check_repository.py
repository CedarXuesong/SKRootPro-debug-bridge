"""Publication check. Reports locations, never matched secret values."""
import argparse
import json
import re
from pathlib import Path
from source_files import source_files

ROOT = Path(__file__).resolve().parents[1]
RULES = {
    "private endpoint": r"(?<![\d.])(?:192\.168\.\d{1,3}\.\d{1,3}|10\.(?:79|126)\.\d{1,3}\.\d{1,3})(?![\d.])",
    "personal Windows profile": r"(?i)C:[/\\]Users[/\\](?!<)[^/\\\s]+",
    "private key material": r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----",
    "credential literal": r'''(?i)(?:root_key|password|secret|\bkey)\s*[:=]\s*[bBrR]?["'][A-Za-z0-9+/_=-]{24,}["']''',
}
# Deliberately public, fixed test fixture; never an actual device credential.
FIXTURES = {"skpdb_probe/tests/test_deploy_tcp.py", "skpdb_probe/tests/test_lab.py"}
FIXTURE_VALUE = "00112233445566778899aabbccddeeff"

def check(root=ROOT):
    issues = []
    files = list(source_files(root))
    for path in files:
        relative = path.relative_to(root).as_posix()
        text = path.read_text(encoding="utf-8-sig")
        if relative in FIXTURES:
            text = text.replace(FIXTURE_VALUE, "public-test-fixture")
        # Scanner patterns describe secrets, but do not contain actual ones.
        if relative == "tools/check_repository.py":
            continue
        for name, pattern in RULES.items():
            for match in re.finditer(pattern, text):
                issues.append({"path": relative, "line": text.count("\n", 0, match.start()) + 1, "rule": name})
    for name in ["README.md", "LICENSE", "THIRD_PARTY_NOTICES.md", "docs/BUILDING.md"]:
        if not (root / name).is_file():
            issues.append({"path": name, "rule": "missing required publication document"})
    return {"passed": not issues, "sourceFiles": len(files), "issues": issues}

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    result = check()
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False, indent=2))
    raise SystemExit(0 if result["passed"] else 1)
