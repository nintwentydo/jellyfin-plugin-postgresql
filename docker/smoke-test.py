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
import urllib.request
import urllib.parse
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
    engine = docker("info", "--format", "{{.OSType}} {{.Architecture}}")
    assert engine == "linux " + machine, "This smoke test requires a native Linux Docker engine"
    inspected = json.loads(docker("image", "inspect", args.image))[0]
    assert inspected["Os"] == "linux" and inspected["Architecture"] == args.architecture
    staging = Path(__file__).resolve().parent
    plugin_hashes = file_hashes(staging / "plugin")
    assert set(plugin_hashes) == {
        "Jellyfin.Plugin.Postgresql.dll", "Npgsql.dll", "Npgsql.EntityFrameworkCore.PostgreSQL.dll", "meta.json"}
    metadata = json.loads((staging / "plugin/meta.json").read_text())
    base_image = next(line.split("=", 1)[1] for line in (staging / "Dockerfile").read_text().splitlines()
                      if line.startswith("ARG JELLYFIN_BASE="))
    assert inspected["Config"]["Labels"]["org.opencontainers.image.base.name"] == base_image
    assert inspected["Config"]["Labels"]["org.opencontainers.image.version"] == metadata["version"]
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

    def base_url():
        binding = json.loads(docker("inspect", jellyfin))[0]["NetworkSettings"]["Ports"]["8096/tcp"][0]
        assert binding["HostIp"] == "127.0.0.1"
        return "http://127.0.0.1:" + binding["HostPort"]

    def wait_for_api(method, path, body=None):
        deadline = time.monotonic() + 180
        while True:
            try:
                return api(method, path, body)
            except OSError:
                if time.monotonic() >= deadline or docker("inspect", "--format", "{{.State.Running}}", jellyfin) != "true":
                    raise RuntimeError("Jellyfin did not become ready") from None
                time.sleep(2)

    def sql(statement):
        return docker("exec", "--user", "postgres", postgres, "psql", "-XAt", "-v", "ON_ERROR_STOP=1",
                      "-d", "jellyfin_smoke", "-c", statement)

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
            base = base_url()
            # Public info also exists on the temporary setup server; wait for the real API.
            wait_for_api("GET", "/Startup/User")
            info = api("GET", "/System/Info/Public")
            assert info["Version"] == "12.0.0" and info["StartupWizardCompleted"] is False
            api("POST", "/Startup/User", {"Name": "docker-smoke", "Password": auth_password})
            credentials = {"Username": "docker-smoke", "Pw": auth_password}
            login = api("POST", "/Users/AuthenticateByName", credentials)
            token = login["AccessToken"]
            user_id = str(uuid.UUID(login["User"]["Id"]))
            api("POST", "/Startup/Configuration", {"ServerName": "Docker smoke", "UICulture": "en-GB",
                "MetadataCountryCode": "AU", "PreferredMetadataLanguage": "en"})
            api("POST", "/Startup/Complete")
            assert api("GET", "/System/Info/Public")["StartupWizardCompleted"] is True
            plugins = [plugin for plugin in api("GET", "/Plugins") if plugin["Name"] == "PostgreSQL"]
            assert len(plugins) == 1 and plugins[0]["Version"] == metadata["version"] and plugins[0]["Status"] == "Active"
            assert sql(f'SELECT count(*) FROM "Users" WHERE "Id" = \'{user_id}\'::uuid;') == "1"
            assert sql("SELECT rolsuper FROM pg_roles WHERE rolname='jellyfin_smoke';") == "f"
            assert container_hashes(jellyfin, "/opt/jellyfin-postgres/plugin") == plugin_hashes
            installed = container_hashes(jellyfin, "/config/plugins/PostgreSQL")
            assert {name: digest for name, digest in installed.items() if name.endswith(".dll")} == expected_dlls
            assert docker("exec", jellyfin, "uname", "-m") == machine
            for tool in ("pg_dump", "psql"):
                assert docker("exec", jellyfin, tool, "--version").startswith(tool + " (PostgreSQL) 18.")
            assert docker("exec", jellyfin, "/usr/lib/jellyfin-ffmpeg/ffmpeg", "-version").startswith("ffmpeg version")
            with urllib.request.urlopen(base + "/web/index.html", timeout=10) as response:
                assert response.status == 200 and b"<html" in response.read().lower()
            docker("restart", jellyfin)
            base = base_url()
            token = ""
            login = wait_for_api("POST", "/Users/AuthenticateByName", credentials)
            token = login["AccessToken"]
            assert str(uuid.UUID(login["User"]["Id"])) == user_id
            assert api("GET", "/System/Info/Public")["StartupWizardCompleted"] is True

            # Create local media with the image's own ffmpeg; disable remote metadata fetchers.
            ffmpeg = "/usr/lib/jellyfin-ffmpeg/ffmpeg"
            docker("exec", jellyfin, "mkdir", "/cache/smoke")
            docker("exec", jellyfin, ffmpeg, "-hide_banner", "-loglevel", "error",
                   "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24",
                   "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "6",
                   "-c:v", "libx264", "-threads", "2", "-c:a", "aac", "/cache/smoke/Smoke.mp4")
            api("POST", "/Library/VirtualFolders?name=Smoke&collectionType=movies&paths=/cache/smoke&refreshLibrary=true",
                {"LibraryOptions": {"EnableRealtimeMonitor": False,
                 "TypeOptions": [{"Type": "Movie", "MetadataFetchers": [], "ImageFetchers": []}]}})
            deadline = time.monotonic() + 180
            while True:
                items = api("GET", "/Items?recursive=true&includeItemTypes=Movie&fields=MediaSources")["Items"]
                if len(items) == 1 and items[0].get("MediaSources"):
                    item = items[0]
                    break
                if time.monotonic() >= deadline:
                    raise TimeoutError("Fixture media was not scanned")
                time.sleep(2)

            # A real ZIP import must restore a deleted row and then generate an ID above its maximum.
            api("POST", "/Auth/Keys?app=restore-smoke")
            sql('UPDATE "ApiKeys" SET "Id" = 100 WHERE "Name" = \'restore-smoke\';')
            backup = api("POST", "/Backup/Create", {"Database": True, "Metadata": False,
                                                    "Subtitles": False, "Trickplay": False})
            assert backup["Options"]["Database"] is True
            archive = Path(backup["Path"]).name
            assert archive.startswith("jellyfin-backup-") and archive.endswith(".zip")
            sql('DELETE FROM "ApiKeys"; ALTER SEQUENCE "ApiKeys_Id_seq" RESTART WITH 1;')
            assert sql('SELECT count(*) FROM "ApiKeys";') == "0"
            api("POST", "/Backup/Restore", {"ArchiveFileName": archive})
            deadline = time.monotonic() + 180
            while sql('SELECT count(*) FROM "ApiKeys" WHERE "Id" = 100;') != "1":
                if time.monotonic() >= deadline:
                    raise TimeoutError("ZIP restore did not restore the sentinel row")
                time.sleep(2)
            token = ""
            login = wait_for_api("POST", "/Users/AuthenticateByName", credentials)
            token = login["AccessToken"]
            assert str(uuid.UUID(login["User"]["Id"])) == user_id
            api("POST", "/Auth/Keys?app=after-restore")
            assert int(sql('SELECT "Id" FROM "ApiKeys" WHERE "Name" = \'after-restore\';')) > 100

            # Decode actual direct and forced HLS streams after the restore.
            for mode in ("direct", "hls"):
                session = uuid.uuid4().hex
                query = {"mediaSourceId": item["MediaSources"][0]["Id"], "playSessionId": session,
                         "deviceId": "docker-smoke"}
                route = "stream" if mode == "direct" else "master.m3u8"
                if mode == "direct":
                    query["static"] = "true"
                else:
                    query.update(videoCodec="h264", audioCodec="aac", videoBitRate=800000,
                                 audioBitRate=96000, allowVideoStreamCopy="false", allowAudioStreamCopy="false",
                                 enableAutoStreamCopy="false", segmentContainer="ts", segmentLength=3,
                                 minSegments=1, cpuCoreLimit=2)
                url = "http://127.0.0.1:8096/Videos/" + item["Id"] + "/" + route + "?" + urllib.parse.urlencode(query)
                header = 'Authorization: MediaBrowser Client="Docker smoke", Device="CI", DeviceId="docker-smoke", Version="1.0", Token="' + token + '"\r\n'
                try:
                    progress = docker("exec", jellyfin, "timeout", "110", ffmpeg, "-hide_banner", "-loglevel", "error",
                                      "-headers", header, "-i", url, "-t", "3", "-map", "0:v:0", "-map", "0:a:0",
                                      "-threads", "2", "-progress", "pipe:1", "-f", "null", "-")
                    values = dict(line.split("=", 1) for line in progress.splitlines() if "=" in line)
                    assert int(values.get("frame", 0)) > 0 and int(values.get("out_time_us", 0)) >= 2_000_000
                finally:
                    if mode == "hls":
                        api("DELETE", "/Videos/ActiveEncodings?" + urllib.parse.urlencode(
                            {"deviceId": "docker-smoke", "playSessionId": session}))
            print(json.dumps({"status": "passed", "architecture": args.architecture,
                "jellyfinVersion": info["Version"], "pluginVersion": plugins[0]["Version"],
                "pluginStatus": plugins[0]["Status"], "installedPluginDllsVerified": len(expected_dlls),
                "postgresMajor": 18, "applicationRoleSuperuser": False,
                "authenticatedUserPersistedAfterRestart": True, "zipRestoreAndGeneratedId": True,
                "directAndHlsDecoded": True}))
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
