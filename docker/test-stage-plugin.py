#!/usr/bin/env python3
"""Exercise the staging gate without Docker or a real plugin build."""

import json
from pathlib import Path
import runpy
import tempfile
import zipfile

stage = runpy.run_path(str(Path(__file__).with_name("stage-plugin.py")))["stage"]
manifest = 'version: "1.2.3.4"\ntargetAbi: "12.0.0.0"\n- "Plugin.dll"\n'
metadata = {"version": "1.2.3.4", "targetAbi": "12.0.0.0"}

with tempfile.TemporaryDirectory() as temporary:
    root = Path(temporary)
    for case, entries in enumerate((
        [("Plugin.dll", b"dll"), ("meta.json", json.dumps(metadata))],
        [("Plugin.dll", b"dll")],
        [("Plugin.dll", b"dll"), ("meta.json", json.dumps(metadata)), ("../extra.dll", b"bad")],
        [("Plugin.dll", b"dll"), ("meta.json", json.dumps(dict(metadata, version="0.0.0.0")))],
        [("Plugin.dll", b"dll"), ("meta.json", json.dumps(dict(metadata, targetAbi="13.0.0.0")))],
    )):
        archive = root / f"{case}.zip"
        output = root / str(case)
        with zipfile.ZipFile(archive, "w") as zip_file:
            for name, content in entries:
                zip_file.writestr(name, content)
        if case == 0:
            stage(archive, output, manifest)
            assert (output / "Plugin.dll").read_bytes() == b"dll"
            try:
                stage(archive, output, manifest)
            except FileExistsError:
                pass
            else:
                raise AssertionError("Stale staging was accepted")
        else:
            try:
                stage(archive, output, manifest)
            except ValueError:
                assert not output.exists()
            else:
                raise AssertionError(f"Invalid archive accepted: {case}")
print("Plugin staging checks passed")
