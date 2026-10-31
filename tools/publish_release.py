"""Publish commit-named artifacts and independent desktop/SKRoot update feeds."""
import json
import os
import re
import subprocess
import tempfile
from pathlib import Path
from xml.etree import ElementTree


ROOT = Path(__file__).resolve().parents[1]


def gh(*args, check=True):
    result = subprocess.run(["gh", *args], capture_output=True, text=True, encoding="utf-8")
    if check and result.returncode:
        raise RuntimeError(f"GitHub command failed: {result.stderr.strip()}")
    return result


def versions(channel, commit):
    suffix = "-canary" if channel == "Canary" else ""
    desktop = ElementTree.parse(ROOT / "desktop/SkpdbDesktop/SkpdbDesktop.csproj").findtext("PropertyGroup/Version")
    module = re.search(r'#define SKPDB_MODULE_BASE_VERSION "(\d+\.\d+\.\d+)"',
                       (ROOT / "skpdb_probe/src/wireless/module_info.h").read_text(encoding="utf-8")).group(1)
    return tuple(f"{base}{suffix}+{commit[:12]}" for base in (desktop, module))


def make_feed(channel, commit, desktop_version, module_version, module_archive, repository):
    tag = f"{channel.lower()}-{commit[:12]}"
    base = f"https://github.com/{repository}/releases"
    return {
        # First three fields are the SKRoot SDK's native update JSON contract.
        "version": module_version,
        "zipUrl": f"{base}/download/{tag}/{module_archive}",
        "changelog": f"{base}/download/updates/{channel.lower()}-notes.txt",
        "channel": channel,
        "desktopVersion": desktop_version,
        "commit": commit,
        "releaseUrl": f"{base}/tag/{tag}",
    }


def main():
    channel = os.environ["BUILD_CHANNEL"]
    commit = os.environ["BUILD_SHA"]
    repository = os.environ["GH_REPO"]
    if channel not in ("Canary", "Release") or not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise SystemExit("Invalid build identity")
    desktop_version, module_version = versions(channel, commit)
    tag = f"{channel.lower()}-{commit[:12]}"
    artifacts = sorted((ROOT / "artifacts").glob("*"))
    module_archive = f"SKPDB-module-{module_version.replace('+', '-')}.zip"
    desktop_archive = f"SKPDB-{desktop_version.replace('+', '-')}-portable.zip"
    for required in (module_archive, desktop_archive, module_archive + ".sha256",
                     desktop_archive + ".sha256", "SKPDB-module-notices.zip"):
        if not any(path.name == required for path in artifacts):
            raise SystemExit(f"Missing release artifact: {required}")
    notes = (
        f"Build channel: {channel}\nCommit: {commit}\n"
        f"Desktop: {desktop_version}\nModule: {module_version}\n"
        f"Workflow: {os.environ['GITHUB_SERVER_URL']}/{repository}/actions/runs/{os.environ['BUILD_ID']}\n\n"
        "Canary includes the debug overlay; Release disables diagnostic layers at compile time.\n"
        "Desktop requires .NET 8 Desktop Runtime and Windows App Runtime 1.7.\n"
        "Install the module ZIP as-is; reboot to activate it. Dependency licenses: SKPDB-module-notices.zip.\n"
        "Updates stay on the installed channel. Module updates use the SKRoot manager's native checker.\n"
    )
    with tempfile.TemporaryDirectory() as temporary:
        directory = Path(temporary)
        notes_file = directory / f"{channel.lower()}-notes.txt"
        notes_file.write_text(notes, encoding="utf-8")
        if gh("release", "view", tag, check=False).returncode == 0:
            gh("release", "upload", tag, *(str(path) for path in artifacts), "--clobber")
        else:
            flags = ["--prerelease", "--latest=false"] if channel == "Canary" else ["--latest=true"]
            gh("release", "create", tag, *(str(path) for path in artifacts),
               "--target", commit, "--title", f"{channel} {commit[:12]}",
               "--notes-file", str(notes_file), *flags)
        if gh("release", "view", "updates", check=False).returncode != 0:
            created = gh("release", "create", "updates", "--target", commit, "--title", "Update feeds",
                         "--notes", "Channel-specific update metadata. Download builds from their commit-named releases.",
                         "--prerelease", "--latest=false", check=False)
            if created.returncode != 0:
                gh("release", "view", "updates")
        name = f"{channel.lower()}.json"
        previous = gh("release", "download", "updates", "--pattern", name, "--dir", str(directory), check=False)
        if previous.returncode == 0:
            old_commit = json.loads((directory / name).read_text(encoding="utf-8"))["commit"]
            if old_commit != commit:
                comparison = json.loads(gh("api", f"repos/{repository}/compare/{old_commit}...{commit}").stdout)
                if comparison["status"] != "ahead":
                    raise SystemExit("Release published, but refusing to move update feed backwards or onto another history")
        feed = make_feed(channel, commit, desktop_version, module_version, module_archive, repository)
        feed_file = directory / name
        feed_file.write_text(json.dumps(feed, indent=2) + "\n", encoding="utf-8")
        gh("release", "upload", "updates", str(feed_file), str(notes_file), "--clobber")
    print(f"Published {channel} {commit[:12]} and its independent update feed")


if __name__ == "__main__":
    main()
