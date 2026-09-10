#!/usr/bin/env python3
"""Check that backup preparation waits for a new, successful library scan."""

from pathlib import Path
import runpy
from unittest.mock import Mock, call, patch

scan_library = runpy.run_path(str(Path(__file__).with_name("smoke-test.py")))["scan_library"]
completed = {"Status": "Completed", "EndTimeUtc": "2026-09-10T01:00:00Z"}

for previous in (None, dict(completed, EndTimeUtc="2026-09-09T01:00:00Z")):
    initial = {"Id": "scan", "Key": "RefreshLibrary", "State": "Idle", "LastExecutionResult": previous}
    api = Mock(side_effect=[
        [initial], None, initial, dict(initial, State="Running"), initial,
        dict(initial, LastExecutionResult=completed),
    ])
    with patch("time.sleep"), patch("time.monotonic", return_value=0):
        scan_library(api)
    assert api.call_args_list == [
        call("GET", "/ScheduledTasks"), call("POST", "/ScheduledTasks/Running/scan"),
        *[call("GET", "/ScheduledTasks/scan")] * 4,
    ], "Accepted an old result or returned before the scan finished"

for status in ("Failed", "Cancelled", "Aborted"):
    api = Mock(side_effect=[[initial], None, dict(initial, LastExecutionResult=dict(completed, Status=status))])
    try:
        scan_library(api)
    except AssertionError as error:
        assert "Library scan failed" in str(error)
    else:
        raise AssertionError(f"Accepted unsuccessful scan: {status}")

api = Mock(side_effect=[[initial], None, initial])
with patch("time.monotonic", side_effect=[0, 180]):
    try:
        scan_library(api)
    except TimeoutError:
        pass
    else:
        raise AssertionError("Did not time out waiting for a new scan")

print("Smoke test scan completion checks passed")
