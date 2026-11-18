#!/usr/bin/env python3
import json
import re
import sys


def allow() -> None:
    print(json.dumps({"decision": "allow"}))


def force_ask(reason: str) -> None:
    print(json.dumps({"decision": "force_ask", "reason": reason}, ensure_ascii=False))


def main() -> None:
    payload = json.load(sys.stdin) if not sys.stdin.isatty() else {}
    tool = payload.get("toolCall") or {}
    tool_name = tool.get("name") or ""
    args = tool.get("args") or {}

    path = (args.get("Path") or "").replace("\\", "/")
    if not path.endswith("CavernSamples/CavernizeAndroid/Properties/AndroidManifest.xml"):
        allow()
        return

    if tool_name == "edit_file":
        target = args.get("Target") or ""
        replacement = args.get("Replacement") or ""
        # Flag only if targetSdkVersion is changed away from 28 or removed
        if "targetSdkVersion" in target or "targetSdkVersion" in replacement:
            if not re.search(r'android:targetSdkVersion\s*=\s*"28"', replacement):
                force_ask(
                    'PROJECT SAFETY GUARD: AndroidManifest.xml must keep android:targetSdkVersion="28" '
                    '(SELinux untrusted_app_27 domain) so static binaries (ffmpeg/truehdd) can execute inside FilesDir. '
                    'Confirm manually only if you intend to change targetSdkVersion.'
                )
                return
    elif tool_name == "write_file":
        content = args.get("Content") or ""
        if not re.search(r'android:targetSdkVersion\s*=\s*"28"', content):
            force_ask(
                'PROJECT SAFETY GUARD: AndroidManifest.xml rewritten without android:targetSdkVersion="28" '
                '(required for executing static binaries from FilesDir under SELinux). '
                'Confirm manually only if intentional.'
            )
            return

    allow()


if __name__ == "__main__":
    try:
        main()
    except Exception:
        print('{"decision": "allow"}')
