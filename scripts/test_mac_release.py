import unittest
import xml.etree.ElementTree as ET
from datetime import datetime, timezone

import mac_release as r

NOW = datetime(2026, 10, 23, 12, 0, tzinfo=timezone.utc)
NS = {"sparkle": r.SPARKLE_NS}


def add(xml, version, channel_check=None):
    return r.update_appcast(xml, version, f"https://example.com/OverInk-{version}-mac.dmg", "SIG==", 1234,
                            "https://example.com/notes", now=NOW)


def versions(xml):
    root = ET.fromstring(xml)
    return [i.find("sparkle:shortVersionString", NS).text for i in root.find("channel").findall("item")]


class BuildNumberTests(unittest.TestCase):
    def test_stable_and_prerelease_numbers(self):
        self.assertEqual(r.build_number("0.3.0"), "0.3.0.10000")
        self.assertEqual(r.build_number("v0.3.0-beta.2"), "0.3.0.2")
        self.assertEqual(r.build_number("0.0.0-dev.123"), "0.0.0.123")
        self.assertEqual(r.build_number("1.0.0-beta"), "1.0.0.1")

    def test_ordering_matches_release_order(self):
        order = ["0.2.9", "0.3.0-beta.1", "0.3.0-beta.2", "0.3.0-beta.10", "0.3.0", "0.3.1-beta.1", "0.10.0"]
        keys = [r.build_key(r.build_number(v)) for v in order]
        self.assertEqual(keys, sorted(keys))
        self.assertEqual(len(set(keys)), len(keys))

    def test_rejects_bad_versions(self):
        for bad in ["1.2", "abc", "1.2.3.4", ""]:
            with self.assertRaises(ValueError):
                r.build_number(bad)


class AppcastTests(unittest.TestCase):
    def test_creates_a_new_appcast(self):
        xml = add("", "0.3.0")
        root = ET.fromstring(xml)
        item = root.find("channel/item")
        self.assertEqual(item.find("sparkle:version", NS).text, "0.3.0.10000")
        self.assertEqual(item.find("sparkle:minimumSystemVersion", NS).text, "14.0")
        self.assertIsNone(item.find("sparkle:channel", NS))
        enclosure = item.find("enclosure")
        self.assertEqual(enclosure.get("length"), "1234")
        self.assertEqual(enclosure.get("{%s}edSignature" % r.SPARKLE_NS), "SIG==")
        self.assertEqual(enclosure.get("url"), "https://example.com/OverInk-0.3.0-mac.dmg")

    def test_beta_items_carry_the_beta_channel(self):
        item = ET.fromstring(add("", "0.3.0-beta.1")).find("channel/item")
        self.assertEqual(item.find("sparkle:channel", NS).text, "beta")

    def test_items_are_newest_first_and_a_rerun_replaces_the_item(self):
        xml = add("", "0.2.0")
        xml = add(xml, "0.3.0-beta.1")
        xml = add(xml, "0.3.0")
        xml = add(xml, "0.3.0-beta.1")
        self.assertEqual(versions(xml), ["0.3.0", "0.3.0-beta.1", "0.2.0"])

    def test_keeps_the_most_recent_items_only(self):
        xml = ""
        for patch in range(r.MAX_ITEMS + 5):
            xml = add(xml, f"1.0.{patch}")
        found = versions(xml)
        self.assertEqual(len(found), r.MAX_ITEMS)
        self.assertEqual(found[0], f"1.0.{r.MAX_ITEMS + 4}")


if __name__ == "__main__":
    unittest.main()
