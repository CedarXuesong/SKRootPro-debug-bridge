"""The intentionally small publication boundary. Never export a whole workspace."""
from pathlib import Path
import os

ROOT_FILES = {"README.md", "LICENSE", "CONTRIBUTING.md", "SECURITY.md", "CHANGELOG.md",
              "THIRD_PARTY_NOTICES.md", ".gitignore", ".gitattributes", ".editorconfig", ".clang-format"}
ROOTS = {"desktop", "host", "skpdb_probe", "docs", "tools", ".github"}
SUFFIXES = {".cs", ".csproj", ".xaml", ".manifest", ".sln", ".props", ".targets",
            ".cpp", ".h", ".java", ".py", ".ps1", ".js", ".cjs", ".html", ".css",
            ".mk", ".ld", ".config", ".xml", ".md", ".txt", ".json", ".yml", ".yaml"}
EXCLUDED_PARTS = {"bin", "obj", "build", "libs", "libs-experimental", "data", "__pycache__", "screenshots",
                  "node_modules", ".vs", ".idea"}
GENERATED = {"cursor_dex.h", "gyro_payload_bytes.h", "touch_guard_bytes.h", "webui_assets.h"}

def source_files(root: Path):
    candidates = [root / name for name in ROOT_FILES if (root / name).is_file()]
    for name in sorted(ROOTS):
        if not (root / name).is_dir():
            continue
        for directory, directories, filenames in os.walk(root / name, followlinks=False):
            directories[:] = sorted(d for d in directories if d not in EXCLUDED_PARTS and not d.endswith(("_obj", "_libs")))
            candidates.extend(Path(directory) / file for file in filenames)
    for path in sorted(candidates):
        rel = path.relative_to(root)
        # Pruning logically before reading anything avoids touching local secrets.
        if rel.parts[0] not in ROOTS and rel.as_posix() not in ROOT_FILES:
            continue
        if any(p in EXCLUDED_PARTS or p.endswith(("_obj", "_libs")) for p in rel.parts):
            continue
        if path.is_dir():
            continue
        if path.name.startswith("REVIEW-") or path.name in GENERATED:
            continue
        if path.name not in ROOT_FILES and path.suffix.lower() not in SUFFIXES:
            continue
        if path.is_symlink() or not path.resolve().is_relative_to(root.resolve()):
            raise ValueError(f"external/symlink source path: {rel}")
        yield path
