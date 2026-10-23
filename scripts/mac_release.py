#!/usr/bin/env python3
"""Release helpers for the macOS app: Sparkle build numbers and appcast.xml updates.

  mac_release.py build-number 0.3.0-beta.2
  mac_release.py appcast --output appcast.xml --previous old.xml --version 0.3.0 \
      --url https://.../OverInk-0.3.0-mac.dmg --signature BASE64 --length 123456 \
      --release-notes-url https://github.com/.../releases/tag/v0.3.0
"""
import argparse
import re
import sys
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from email.utils import format_datetime
from pathlib import Path

SPARKLE_NS = "http://www.andymatuschak.org/xml-namespaces/sparkle"
ET.register_namespace("sparkle", SPARKLE_NS)

STABLE_STAGE = 10000
MAX_ITEMS = 40
_VERSION = re.compile(r"^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?$")


def sparkle(tag):
    return f"{{{SPARKLE_NS}}}{tag}"


def parse_version(version):
    match = _VERSION.match(version.strip())
    if not match:
        raise ValueError(f"'{version}' is not a version like 1.2.3 or 1.2.3-beta.1")
    major, minor, patch, pre = match.groups()
    return int(major), int(minor), int(patch), pre


def is_prerelease(version):
    return parse_version(version)[3] is not None


def build_number(version):
    """CFBundleVersion / sparkle:version. Sparkle compares dotted numbers, so a pre-release gets its own
    number as a fourth part (1-9999) and a stable release gets 10000: 0.3.0-beta.2 < 0.3.0-beta.10 < 0.3.0."""
    major, minor, patch, pre = parse_version(version)
    if pre is None:
        stage = STABLE_STAGE
    else:
        numbers = re.findall(r"\d+", pre)
        stage = min(max(int(numbers[-1]) if numbers else 1, 1), STABLE_STAGE - 1)
    return f"{major}.{minor}.{patch}.{stage}"


def build_key(number):
    return tuple(int(part) for part in number.split("."))


def new_item(version, url, signature, length, release_notes_url, minimum_system_version, now):
    item = ET.Element("item")
    ET.SubElement(item, "title").text = version.lstrip("v")
    ET.SubElement(item, "pubDate").text = format_datetime(now)
    ET.SubElement(item, sparkle("version")).text = build_number(version)
    ET.SubElement(item, sparkle("shortVersionString")).text = version.lstrip("v")
    ET.SubElement(item, sparkle("minimumSystemVersion")).text = minimum_system_version
    if is_prerelease(version):
        ET.SubElement(item, sparkle("channel")).text = "beta"
    if release_notes_url:
        ET.SubElement(item, sparkle("releaseNotesLink")).text = release_notes_url
    enclosure = ET.SubElement(item, "enclosure")
    enclosure.set("url", url)
    enclosure.set("length", str(length))
    enclosure.set("type", "application/octet-stream")
    enclosure.set(sparkle("edSignature"), signature)
    return item


def item_build(item):
    node = item.find(sparkle("version"))
    return build_key(node.text.strip()) if node is not None and node.text else ()


def update_appcast(previous_xml, version, url, signature, length, release_notes_url="",
                   minimum_system_version="14.0", now=None):
    """Returns appcast XML text with this release added (replacing any item with the same build number)."""
    now = now or datetime.now(timezone.utc)
    if previous_xml and previous_xml.strip():
        root = ET.fromstring(previous_xml)
        channel = root.find("channel")
        if channel is None:
            raise ValueError("The existing appcast has no <channel>")
    else:
        root = ET.Element("rss", {"version": "2.0"})
        channel = ET.SubElement(root, "channel")
        ET.SubElement(channel, "title").text = "OverInk"
        ET.SubElement(channel, "link").text = "https://github.com/hickmanz/overink"
        ET.SubElement(channel, "description").text = "OverInk for macOS updates"
        ET.SubElement(channel, "language").text = "en"

    item = new_item(version, url, signature, length, release_notes_url, minimum_system_version, now)
    number = build_key(build_number(version))
    items = [i for i in channel.findall("item") if item_build(i) != number]
    items.append(item)
    items.sort(key=item_build, reverse=True)
    for old in channel.findall("item"):
        channel.remove(old)
    for entry in items[:MAX_ITEMS]:
        channel.append(entry)

    ET.indent(root, space="  ")
    return '<?xml version="1.0" encoding="utf-8"?>\n' + ET.tostring(root, encoding="unicode") + "\n"


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    number = sub.add_parser("build-number")
    number.add_argument("version")

    appcast = sub.add_parser("appcast")
    appcast.add_argument("--output", required=True)
    appcast.add_argument("--previous")
    appcast.add_argument("--version", required=True)
    appcast.add_argument("--url", required=True)
    appcast.add_argument("--signature", required=True)
    appcast.add_argument("--length", required=True, type=int)
    appcast.add_argument("--release-notes-url", default="")
    appcast.add_argument("--minimum-system-version", default="14.0")

    args = parser.parse_args(argv)
    if args.command == "build-number":
        print(build_number(args.version))
        return 0

    previous = ""
    if args.previous and Path(args.previous).is_file():
        previous = Path(args.previous).read_text(encoding="utf-8")
    text = update_appcast(previous, args.version, args.url, args.signature, args.length,
                          args.release_notes_url, args.minimum_system_version)
    Path(args.output).write_text(text, encoding="utf-8")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main(sys.argv[1:]))
    except ValueError as error:
        print(f"error: {error}", file=sys.stderr)
        sys.exit(1)
