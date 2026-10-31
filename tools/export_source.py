"""Export reviewed source without Git history, binaries, user data or SDKs."""
import hashlib
import json
import subprocess
from datetime import datetime
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED
from check_repository import ROOT, check
from source_files import source_files

def main():
    report = check()
    if not report["passed"]:
        print(json.dumps(report, ensure_ascii=False, indent=2))
        raise SystemExit("Publication check failed; source archive was not created")
    out = ROOT / "dist"
    out.mkdir(exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    archive = out / f"SKPDB-source-{stamp}.zip"
    commit = subprocess.check_output(["git", "-C", str(ROOT), "rev-parse", "HEAD"], text=True).strip()
    manifest = []
    with ZipFile(archive, "x", compression=ZIP_DEFLATED) as zipfile:
        for path in source_files(ROOT):
            name = path.relative_to(ROOT).as_posix()
            data = path.read_bytes()
            zipfile.writestr("SKPDB-source/" + name, data)
            manifest.append({"path": name, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest()})
        zipfile.writestr("SKPDB-source/SOURCE-MANIFEST.json", json.dumps(manifest, indent=2))
        zipfile.writestr("SKPDB-source/SOURCE-COMMIT.txt", commit + "\n")
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    archive.with_suffix(".zip.sha256").write_text(f"{digest}  {archive.name}\n", encoding="ascii")
    print(f"Exported {len(manifest)} source files: {archive}\nSHA256: {digest}")

if __name__ == "__main__":
    main()
