import os
from pathlib import Path
import subprocess
import unittest


@unittest.skipUnless(os.name == "nt", "Windows UI Automation reader")
class MessageReadingSafetyTests(unittest.TestCase):
    def test_menu_preserves_preferences_and_experimental_gating(self):
        binary = Path(__file__).resolve().parents[1] / "native-host/bin/TrayMenuTest.exe"
        if not binary.exists():
            self.skipTest("Native tray menu diagnostic is not built")
        result = subprocess.run([str(binary)], capture_output=True, timeout=5)
        self.assertEqual(0, result.returncode, result.stdout.decode(errors="replace"))
        self.assertIn(b"tray-navigation-preferences-and-experiment-gating-ok", result.stdout)

    def test_native_reader_timeout_does_not_queue_more_hung_workers(self):
        binary = Path(__file__).resolve().parents[1] / "native-host/bin/MessageReaderSafetyTest.exe"
        if not binary.exists():
            self.skipTest("Native message reader diagnostic is not built")
        result = subprocess.run([str(binary)], capture_output=True, timeout=5)
        self.assertEqual(0, result.returncode, result.stdout.decode(errors="replace"))
        self.assertIn(b"single-worker-and-recovery-ok", result.stdout)
