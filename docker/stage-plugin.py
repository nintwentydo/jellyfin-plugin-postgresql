#!/usr/bin/env python3
"""Verify the release ZIP against build.yaml and stage only its declared payload."""

import argparse
import json
from pathlib import Path
import re
import zipfile


def stage(plugin_zip, output, manifest):
    if output.exists():
        raise FileExistsError(f"Remove the previous staged {output} before staging a release")
    expected = set(re.findall(r'^- "([^"]+\.dll)"$', manifest, re.MULTILINE)) | {"meta.json"}
    with zipfile.ZipFile(plugin_zip) as archive:
        if set(archive.namelist()) != expected or len(archive.namelist()) != len(expected):
            raise ValueError("Unexpected plugin ZIP contents")
        metadata = json.loads(archive.read("meta.json"))
        for key in ("version", "targetAbi"):
            value = re.search(rf'^{key}: "([0-9]+(?:\.[0-9]+){{3}})"$', manifest, re.MULTILINE)
            if value is None or metadata.get(key) != value[1]:
                raise ValueError(f"Plugin {key} does not match build.yaml")
        # Read every entry before writing anything; this also verifies ZIP CRCs.
        payload = {name: archive.read(name) for name in expected}
    output.mkdir(parents=True)
    for name, content in payload.items():
        (output / name).write_bytes(content)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("plugin_zip", type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    stage(args.plugin_zip, root / "docker/plugin", (root / "build.yaml").read_text())
    print("Verified and staged the plugin release payload")
