"""Offline checks for release identity and SKRoot/desktop update contracts."""
import unittest
from publish_release import make_feed, versions


class FeedTests(unittest.TestCase):
    def test_channel_contracts(self):
        commit = "0123456789abcdef0123456789abcdef01234567"
        for channel in ("Canary", "Release"):
            with self.subTest(channel=channel):
                desktop, module = versions(channel, commit)
                suffix = "-canary" if channel == "Canary" else ""
                self.assertRegex(desktop, rf"^\d+\.\d+\.\d+{suffix}\+{commit[:12]}$")
                self.assertRegex(module, rf"^\d+\.\d+\.\d+{suffix}\+{commit[:12]}$")
                archive = f"SKPDB-module-{module.replace('+', '-')}.zip"
                feed = make_feed(channel, commit, desktop, module, archive, "owner/repo")
                self.assertEqual(feed["channel"], channel)
                self.assertEqual(feed["version"], module)
                self.assertEqual(feed["desktopVersion"], desktop)
                self.assertEqual(feed["commit"], commit)
                self.assertTrue(feed["zipUrl"].endswith(f"/{channel.lower()}-{commit[:12]}/{archive}"))
                self.assertTrue(feed["releaseUrl"].endswith(f"/{channel.lower()}-{commit[:12]}"))
                self.assertTrue(feed["changelog"].endswith(f"/updates/{channel.lower()}-notes.txt"))


if __name__ == "__main__":
    unittest.main()
