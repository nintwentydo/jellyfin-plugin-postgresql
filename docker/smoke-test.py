#!/usr/bin/env python3
"""Check a local candidate image against fresh Docker-only PostgreSQL and config state."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import secrets
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid


def docker(*arguments):
    result = subprocess.run(["docker", *arguments], capture_output=True, text=True, timeout=180)
    if result.returncode:
        raise RuntimeError(f"Docker {arguments[0]} failed: {result.stderr.strip()}")
    return result.stdout.strip()


def file_hashes(directory):
    return {str(path.relative_to(directory)): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in directory.rglob("*") if path.is_file()}


def container_hashes(container, directory):
    output = docker("exec", container, "sh", "-c", "cd \"$1\" && find . -type f -exec sha256sum {} +", "sh", directory)
    return {name.removeprefix("./"): digest for digest, name in
            (line.split(maxsplit=1) for line in output.splitlines())}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image")
    parser.add_argument("architecture", choices=("amd64", "arm64"))
    args = parser.parse_args()
    machine = {"amd64": "x86_64", "arm64": "aarch64"}[args.architecture]
    assert os.uname().machine == machine, "This smoke test requires a native Linux runner"
    inspected = json.loads(docker("image", "inspect", args.image))[0]
    assert inspected["Os"] == "linux" and inspected["Architecture"] == args.architecture
    staging = Path(__file__).resolve().parent
    server_hashes = file_hashes(staging / "server")
    plugin_hashes = file_hashes(staging / "plugin")
    assert server_hashes and set(plugin_hashes) == {
        "Jellyfin.Plugin.Postgresql.dll", "Npgsql.dll", "Npgsql.EntityFrameworkCore.PostgreSQL.dll", "meta.json"}
    expected_dlls = {name: digest for name, digest in plugin_hashes.items() if name.endswith(".dll")}
    prefix = "jf-docker-smoke-" + uuid.uuid4().hex[:12]
    postgres, jellyfin = prefix + "-pg", prefix + "-server"
    token = ""
    password = secrets.token_urlsafe(24)
    auth_password = secrets.token_urlsafe(24)
    base = ""

    def api(method, path, body=None):
        authorization = 'MediaBrowser Client="Docker smoke", Device="CI", DeviceId="docker-smoke", Version="1.0"'
        if token:
            authorization += ', Token="' + token + '"'
        request = urllib.request.Request(base + path, method=method,
            data=json.dumps(body).encode() if body is not None else None,
            headers={"Authorization": authorization, "Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=10) as response:
            payload = response.read()
            return json.loads(payload) if payload else None

    with tempfile.TemporaryDirectory(prefix=prefix) as temporary:
        directory = Path(temporary)
        for child in ("config", "cache"):
            (directory / child).mkdir()
        docker("network", "create", prefix)
        try:
            docker("run", "--detach", "--name", postgres, "--network", prefix,
                   "--env", "POSTGRES_PASSWORD=" + password, "postgres:18")
            deadline = time.monotonic() + 90
            while subprocess.run(["docker", "exec", postgres, "pg_isready", "-h", "127.0.0.1", "-U", "postgres"],
                                 stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=10).returncode:
                if time.monotonic() >= deadline:
                    raise TimeoutError("PostgreSQL did not become ready")
                time.sleep(1)
            docker("exec", "--user", "postgres", postgres, "psql", "-X", "-v", "ON_ERROR_STOP=1",
                   "-c", f"CREATE ROLE jellyfin_smoke LOGIN NOSUPERUSER NOCREATEDB PASSWORD '{password}';",
                   "-c", "CREATE DATABASE jellyfin_smoke OWNER jellyfin_smoke;")
            docker("run", "--detach", "--name", jellyfin, "--network", prefix,
                   "--user", f"{os.getuid()}:{os.getgid()}", "--publish", "127.0.0.1::8096",
                   "--volume", f"{directory}/config:/config", "--volume", f"{directory}/cache:/cache",
                   "--env", "POSTGRES_HOST=" + postgres, "--env", "POSTGRES_DB=jellyfin_smoke",
                   "--env", "POSTGRES_USER=jellyfin_smoke", "--env", "POSTGRES_PASSWORD=" + password,
                   args.image)
            binding = json.loads(docker("inspect", jellyfin))[0]["NetworkSettings"]["Ports"]["8096/tcp"][0]
            assert binding["HostIp"] == "127.0.0.1"
            base = "http://127.0.0.1:" + binding["HostPort"]
            deadline = time.monotonic() + 180
            while True:
                try:
                    info = api("GET", "/System/Info/Public")
                    break
                except (urllib.error.URLError, TimeoutError):
                    if time.monotonic() >= deadline or docker("inspect", "--format", "{{.State.Running}}", jellyfin) != "true":
                        raise RuntimeError("Jellyfin did not become ready") from None
                    time.sleep(2)
            assert info["Version"] == "13.0.0" and info["StartupWizardCompleted"] is False
            api("GET", "/Startup/User")
            api("POST", "/Startup/User", {"Name": "docker-smoke", "Password": auth_password})
            login = api("POST", "/Users/AuthenticateByName", {"Username": "docker-smoke", "Pw": auth_password})
            token = login["AccessToken"]
            user_id = str(uuid.UUID(login["User"]["Id"]))
            api("POST", "/Startup/Configuration", {"ServerName": "Docker smoke", "UICulture": "en-GB",
                "MetadataCountryCode": "AU", "PreferredMetadataLanguage": "en"})
            api("POST", "/Startup/Complete")
            assert api("GET", "/System/Info/Public")["StartupWizardCompleted"] is True
            plugins = [plugin for plugin in api("GET", "/Plugins") if plugin["Name"] == "PostgreSQL"]
            assert len(plugins) == 1 and plugins[0]["Version"] == "1.1.3.0" and plugins[0]["Status"] == "Active"
            actual_user = docker("exec", "--user", "postgres", postgres, "psql", "-XAt", "-v", "ON_ERROR_STOP=1",
                "-d", "jellyfin_smoke", "-c", f'SELECT count(*) FROM "Users" WHERE "Id" = \'{user_id}\'::uuid;')
            assert actual_user == "1", "Authenticated user was not persisted in PostgreSQL"
            assert docker("exec", "--user", "postgres", postgres, "psql", "-XAt", "-v", "ON_ERROR_STOP=1",
                "-c", "SELECT rolsuper FROM pg_roles WHERE rolname='jellyfin_smoke';") == "f"
            assert container_hashes(jellyfin, "/jellyfin") == server_hashes, "Server files differ from the released payload"
            assert container_hashes(jellyfin, "/opt/jellyfin-postgres/plugin") == plugin_hashes
            installed = container_hashes(jellyfin, "/config/plugins/PostgreSQL")
            assert {name: digest for name, digest in installed.items() if name.endswith(".dll")} == expected_dlls
            assert docker("exec", jellyfin, "uname", "-m") == machine
            for tool in ("pg_dump", "psql"):
                assert docker("exec", jellyfin, tool, "--version").startswith(tool + " (PostgreSQL) 18.")
            assert docker("exec", jellyfin, "/usr/lib/jellyfin-ffmpeg/ffmpeg", "-version").startswith("ffmpeg version")
            print(json.dumps({"status": "passed", "architecture": args.architecture,
                "jellyfinVersion": info["Version"], "pluginVersion": plugins[0]["Version"],
                "pluginStatus": plugins[0]["Status"], "serverFilesVerified": len(server_hashes),
                "installedPluginDllsVerified": len(expected_dlls), "postgresMajor": 18,
                "applicationRoleSuperuser": False, "authenticatedUserStoredInPostgres": True}))
        except Exception:
            for container in (jellyfin, postgres):
                logs = subprocess.run(["docker", "logs", "--tail", "80", container], capture_output=True, text=True, timeout=15)
                output = logs.stdout + logs.stderr
                for secret in (password, auth_password, token):
                    if secret:
                        output = output.replace(secret, "[redacted]")
                print(output)
            raise
        finally:
            subprocess.run(["docker", "rm", "--force", "--volumes", jellyfin, postgres],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=30)
            docker("network", "rm", prefix)


if __name__ == "__main__":
    main()
