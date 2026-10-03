"""Run offline regression without loading a real user's saved API credentials."""

import os
import sys
import tempfile
import unittest
import urllib.request
import urllib.parse
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent


def main():
    original_open = urllib.request.OpenerDirector.open
    def local_only(opener, request, *args, **kwargs):
        url = request.full_url if isinstance(request, urllib.request.Request) else request
        if urllib.parse.urlsplit(url).hostname not in ("localhost", "127.0.0.1", "::1"):
            raise AssertionError("Offline test attempted external network access")
        return original_open(opener, request, *args, **kwargs)
    urllib.request.OpenerDirector.open = local_only
    with tempfile.TemporaryDirectory(prefix="realtime-dictionary-offline-") as profile:
        settings = Path(profile) / "RealtimeDictionary"
        settings.mkdir()
        (settings / "config.json").write_text("{}", encoding="utf-8")
        os.environ["APPDATA"] = profile
        for name in (
            "SILICONFLOW_API_KEY", "DEEPSEEK_API_KEY", "OPENAI_API_KEY",
            "TYPESAFE_API_KEY", "OPENAI_BASE_URL", "OPENAI_MODEL",
            "TYPESAFE_BASE_URL", "TYPESAFE_MODEL",
            "REALTIME_DICTIONARY_ANALYSIS_MODEL",
            "REALTIME_DICTIONARY_LOOKUP_MODEL",
            "REALTIME_DICTIONARY_SELECTION_MODEL",
        ):
            os.environ.pop(name, None)
        os.chdir(ROOT)
        sys.path.insert(0, str(ROOT))
        suite = unittest.defaultTestLoader.discover(str(ROOT / "tests"))
        result = unittest.TextTestRunner(verbosity=2).run(suite)
        return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    raise SystemExit(main())
