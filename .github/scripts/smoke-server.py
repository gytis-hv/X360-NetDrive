import json
import os
from pathlib import Path
import shutil
import socket
import struct
import subprocess
import sys
import tempfile
import time
import urllib.request


def create_iso(path):
    image = bytearray(0x12000)
    signature = b"MICROSOFT*XBOX*MEDIA"
    image[0x10000:0x10000 + len(signature)] = signature
    image[0x107EC:0x107EC + len(signature)] = signature
    struct.pack_into("<II", image, 0x10014, 1, 128)
    xex = bytearray(64)
    xex[:4] = b"XEX2"
    struct.pack_into(">III", xex, 20, 1, 0x00040006, 40)
    struct.pack_into(">I", xex, 40, 0x12345678)
    struct.pack_into(">I", xex, 52, 0x87654321)
    xex[56:60] = bytes([0x57, 0xA3, 1, 1])
    entry = 2048
    struct.pack_into("<II", image, entry + 4, 3, len(xex))
    name = b"default.xex"
    image[entry + 13] = len(name)
    image[entry + 14:entry + 14 + len(name)] = name
    image[3 * 2048:3 * 2048 + len(xex)] = xex
    path.write_bytes(image)
    return bytes(xex)


def main():
    executable = Path(sys.argv[1]).resolve(strict=True)
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        isolated = root / executable.name
        shutil.copy2(executable, isolated)
        isolated.chmod(isolated.stat().st_mode | 0o111)
        expected = create_iso(root / "smoke.iso")
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            port = listener.getsockname()[1]
        base = f"http://127.0.0.1:{port}"
        with (root / "server.log").open("w+", encoding="utf-8") as log:
            process = subprocess.Popen(
                [str(isolated), "--iso", str(root / "smoke.iso"),
                 "--iso-metadata-layout", "false", "--urls", base],
                cwd=root, stdout=log, stderr=subprocess.STDOUT,
                env={**os.environ, "DOTNET_NOLOGO": "1"},
            )
            try:
                deadline = time.monotonic() + 60
                while True:
                    if process.poll() is not None:
                        raise RuntimeError(f"Server exited before readiness: {process.returncode}")
                    try:
                        with urllib.request.urlopen(base + "/health", timeout=2) as response:
                            health = json.load(response)
                        break
                    except (OSError, TimeoutError):
                        if time.monotonic() >= deadline:
                            raise TimeoutError("Server did not become ready")
                        time.sleep(0.5)
                if health["status"] != "ready" or health["games"] != 1:
                    raise AssertionError(f"Unexpected health response: {health}")
                with urllib.request.urlopen(base + "/webdav/Games/smoke/default.xex", timeout=10) as response:
                    if response.read() != expected:
                        raise AssertionError("Published executable returned incorrect ISO data")
                with urllib.request.urlopen(base + "/", timeout=10) as response:
                    manifest = json.load(response)
                request = urllib.request.Request(base + manifest["disk"], headers={"Range": "bytes=0-511"})
                with urllib.request.urlopen(request, timeout=10) as response:
                    disk = response.read()
                    if response.status != 206 or len(disk) != 512 or disk[510:512] != b"\x55\xaa":
                        raise AssertionError("Published executable returned an invalid virtual disk range")
                print(f"PASS: isolated {executable.name}: health, ISO file and FAT32 disk range")
            except Exception:
                log.flush()
                log.seek(0)
                print(log.read(), file=sys.stderr)
                raise
            finally:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()


if __name__ == "__main__":
    main()
