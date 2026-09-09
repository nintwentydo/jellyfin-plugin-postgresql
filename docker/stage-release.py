#!/usr/bin/env python3
"""Verify and stage the published custom pair for a native Docker build."""

import argparse
import hashlib
import json
from pathlib import Path
import tarfile
import zipfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("release_directory", type=Path)
    parser.add_argument("architecture", choices=("amd64", "arm64"))
    args = parser.parse_args()
    release = args.release_directory
    docker = Path(__file__).resolve().parent
    rid = "linux-x64" if args.architecture == "amd64" else "linux-arm64"
    server = release / f"jellyfin-13-custom-{rid}.tar.gz"
    plugin = release / "postgresql_1.1.3.0.zip"
    expected = {}
    for line in (release / "SHA256SUMS").read_text().splitlines():
        digest, name = line.split(maxsplit=1)
        expected[name.removeprefix("./")] = digest
    for archive in (server, plugin):
        with archive.open("rb") as stream:
            actual = hashlib.file_digest(stream, "sha256").hexdigest()
        if actual != expected.get(archive.name):
            raise ValueError(f"Checksum mismatch: {archive.name}")

    for name in ("server", "plugin"):
        if (docker / name).exists():
            raise FileExistsError(f"Remove the previous staged docker/{name} before staging a release")

    with zipfile.ZipFile(plugin) as archive:
        names = {
            "Jellyfin.Plugin.Postgresql.dll", "Npgsql.dll",
            "Npgsql.EntityFrameworkCore.PostgreSQL.dll", "meta.json",
        }
        if set(archive.namelist()) != names or len(archive.namelist()) != len(names):
            raise ValueError("Unexpected plugin ZIP contents")
        metadata = json.loads(archive.read("meta.json"))
        if metadata["version"] != "1.1.3.0" or metadata["targetAbi"] != "13.0.0.0":
            raise ValueError("Plugin version/ABI does not match the custom server")
        archive.extractall(docker / "plugin")

    with tarfile.open(server) as archive:
        archive.extractall(docker / "server", filter="data")
    for name in ("jellyfin", "libhostpolicy.so", "libcoreclr.so"):
        if not (docker / "server" / name).is_file():
            raise ValueError(f"Self-contained server is missing {name}")
    config = json.loads((docker / "server" / "jellyfin.runtimeconfig.json").read_text())
    if not config["runtimeOptions"].get("includedFrameworks"):
        raise ValueError("Server bundle is not self-contained")
    print(f"Verified and staged plugin 1.1.3.0 and custom server for {rid}")


if __name__ == "__main__":
    main()
