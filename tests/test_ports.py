import socket
import unittest

from wapk_launcher.ports import find_free_port


class PortTests(unittest.TestCase):
    def test_skips_used_port(self) -> None:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
            sock.bind(("127.0.0.1", 0))
            used_port = sock.getsockname()[1]

            free_port = find_free_port(used_port, used_port + 1)

        self.assertEqual(free_port, used_port + 1)


if __name__ == "__main__":
    unittest.main()
