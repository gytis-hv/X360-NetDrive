import socket
import struct
import subprocess
import sys
import tempfile
import threading
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "x360-nbd-size.py"
MAXIMUM = 2199023255040
OPTIONS_MAGIC = 0x49484156454F5054


class NbdSizeTests(unittest.TestCase):
    def run_check(self, size, malformed=False):
        with tempfile.TemporaryDirectory() as directory:
            path = str(Path(directory) / "nbd.sock")
            with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as listener:
                listener.bind(path)
                listener.listen(1)
                errors = []

                def serve():
                    try:
                        with listener.accept()[0] as connection:
                            magic = 0 if malformed else 0x4E42444D41474943
                            connection.sendall(struct.pack(">QQH", magic, OPTIONS_MAGIC, 3))
                            if not malformed:
                                request = bytearray()
                                while len(request) < 20:
                                    part = connection.recv(20 - len(request))
                                    if not part:
                                        raise RuntimeError("client closed before negotiation")
                                    request.extend(part)
                                if request != struct.pack(">IQII", 3, OPTIONS_MAGIC, 1, 0):
                                    raise RuntimeError("unexpected NBD negotiation")
                                connection.sendall(struct.pack(">QH", size, 1))
                    except Exception as error:
                        errors.append(error)

                thread = threading.Thread(target=serve)
                thread.start()
                try:
                    result = subprocess.run(
                        [sys.executable, str(SCRIPT), path, str(MAXIMUM)],
                        capture_output=True, text=True, timeout=10,
                    )
                finally:
                    thread.join(timeout=10)
                self.assertFalse(thread.is_alive())
                self.assertEqual(errors, [])
                return result

    def test_accepts_aligned_source_sizes_through_maximum(self):
        for size in (512, MAXIMUM - 512, MAXIMUM):
            with self.subTest(size=size):
                result = self.run_check(size)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(result.stdout.strip(), str(size))

    def test_rejects_empty_unaligned_and_oversized_sources(self):
        for size in (0, 513, MAXIMUM + 1, MAXIMUM + 512):
            with self.subTest(size=size):
                result = self.run_check(size)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("invalid or exceeds", result.stderr)

    def test_rejects_invalid_negotiation(self):
        result = self.run_check(512, malformed=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("fixed newstyle negotiation", result.stderr)


if __name__ == "__main__":
    unittest.main()
