#!/usr/bin/env python3
"""Writes string plugin settings into a (stopped) pins profile file.

The native guider keeps its settings in the profile's PluginSettings (via
PluginOptionsAccessor). ninaAPI can only change them through
/equipment/guider/set-setting while the guider is connected, and the guider
cannot connect before GuideCameraDriver points at a working camera, so the
harness seeds the first values directly into the profile XML while pins is
stopped.

Usage: seed_plugin_settings.py PROFILE_FILE PLUGIN_GUID KEY=VALUE [KEY=VALUE ...]
"""
import re
import sys
from xml.sax.saxutils import escape

ARR = "http://schemas.microsoft.com/2003/10/Serialization/Arrays"
OUTER = "a:KeyValueOfguidArrayOfKeyValueOfstringanyTypeox8ieOcg"


def kv(key, value):
    return (
        "<a:KeyValueOfstringanyType>"
        f"<a:Key>{escape(key)}</a:Key>"
        f'<a:Value i:type="b:string" xmlns:b="http://www.w3.org/2001/XMLSchema">{escape(value)}</a:Value>'
        "</a:KeyValueOfstringanyType>"
    )


def main():
    path, guid, pairs = sys.argv[1], sys.argv[2].lower(), sys.argv[3:]
    settings = dict(p.split("=", 1) for p in pairs)
    xml = open(path, encoding="utf-8").read()

    empty = f'<pluginStorage xmlns:a="{ARR}"/>'
    if empty in xml:
        xml = xml.replace(empty, f'<pluginStorage xmlns:a="{ARR}"></pluginStorage>')
    if "</pluginStorage>" not in xml:
        sys.exit("pluginStorage element not found in profile")

    entry_re = re.compile(
        rf"<{OUTER}><a:Key>{re.escape(guid)}</a:Key><a:Value>(.*?)</a:Value></{OUTER}>", re.S | re.I
    )
    m = entry_re.search(xml)
    if m:
        body = m.group(1)
        for key in settings:
            body = re.sub(
                rf"<a:KeyValueOfstringanyType><a:Key>{re.escape(key)}</a:Key>.*?</a:KeyValueOfstringanyType>",
                "",
                body,
                flags=re.S,
            )
        body += "".join(kv(k, v) for k, v in settings.items())
        xml = xml[: m.start()] + f"<{OUTER}><a:Key>{guid}</a:Key><a:Value>{body}</a:Value></{OUTER}>" + xml[m.end():]
    else:
        body = "".join(kv(k, v) for k, v in settings.items())
        xml = xml.replace(
            "</pluginStorage>", f"<{OUTER}><a:Key>{guid}</a:Key><a:Value>{body}</a:Value></{OUTER}></pluginStorage>", 1
        )

    with open(path, "w", encoding="utf-8") as f:
        f.write(xml)
    print(f"seeded {len(settings)} setting(s) for plugin {guid} into {path}")


if __name__ == "__main__":
    main()
