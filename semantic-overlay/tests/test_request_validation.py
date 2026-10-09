import http.client
import io
import json
import os
from email.message import Message
from pathlib import Path
import subprocess
import shutil
import tempfile
import threading
import unittest
from unittest import mock

import server
from request_validation import MAX_BODY_BYTES, RequestInputError, read_json_object


class RequestValidationTests(unittest.TestCase):
    def test_server_log_rotates_and_retains_new_events(self):
        with tempfile.TemporaryDirectory() as directory:
            logfile = Path(directory) / '_server.log'
            logfile.write_bytes(b'x' * (2 * 1024 * 1024))
            with mock.patch.object(server, 'HERE', directory), mock.patch('builtins.print'):
                server.log('synthetic latest event')
            self.assertIn('synthetic latest event', logfile.read_text(encoding='utf-8'))
            self.assertEqual(2 * 1024 * 1024, Path(str(logfile) + '.1').stat().st_size)

    def test_analyze_and_lookup_reject_invalid_fields_without_dispatch(self):
        http_server = server.ThreadingHTTPServer(('127.0.0.1', 0), server.Handler)
        thread = threading.Thread(target=http_server.serve_forever, daemon=True)
        thread.start()
        try:
            with mock.patch.object(server, 'PORT', http_server.server_address[1]), \
                    mock.patch.object(server, 'log'), \
                    mock.patch.object(server, 'analyze') as analyze, \
                    mock.patch.object(server, 'lookup') as lookup:
                for path, field, limit in (('/analyze', 'text', 20000), ('/lookup', 'term', 200)):
                    for value in (None, 123, True, [], {}, 'x' * (limit + 1)):
                        with self.subTest(path=path, value_type=type(value).__name__):
                            connection = http.client.HTTPConnection(*http_server.server_address, timeout=3)
                            try:
                                connection.request('POST', path, json.dumps({field: value}),
                                                   {'X-RealtimeDictionary-Token': server.TOKEN})
                                response = connection.getresponse()
                                self.assertEqual(400, response.status)
                                self.assertIn('error', json.loads(response.read()))
                            finally:
                                connection.close()
                analyze.assert_not_called()
                lookup.assert_not_called()
        finally:
            http_server.shutdown()
            http_server.server_close()
            thread.join(2)

    def test_retired_validation_endpoint_and_health_cannot_enable_typesafe(self):
        http_server = server.ThreadingHTTPServer(('127.0.0.1', 0), server.Handler)
        thread = threading.Thread(target=http_server.serve_forever, daemon=True)
        thread.start()
        try:
            with mock.patch.object(server, 'PORT', http_server.server_address[1]), \
                    mock.patch.object(server, 'API_KEY', ''), \
                    mock.patch.object(server, 'provider_urlopen') as provider:
                connection = http.client.HTTPConnection(*http_server.server_address, timeout=3)
                try:
                    connection.request('POST', '/validate-typesafe-key', '{}',
                                       {'X-Session-Token': server.TOKEN})
                    response = connection.getresponse()
                    self.assertEqual(404, response.status)
                    response.read()
                    connection.request('GET', '/health')
                    response = connection.getresponse()
                    health = json.loads(response.read())
                    self.assertEqual('local', health['analysis_provider'])
                    self.assertEqual('local', health['analysis_mode'])
                    self.assertFalse(any('typesafe' in key for key in health))
                    provider.assert_not_called()
                finally:
                    connection.close()
        finally:
            http_server.shutdown()
            http_server.server_close()
            thread.join(2)

    def headers(self, length):
        result = Message()
        if length is not None:
            result['Content-Length'] = length
        return result

    def test_bad_lengths_and_oversize_never_read_the_stream(self):
        for length in (None, 'abc', '-1', '0', '1e3', str(MAX_BODY_BYTES + 1), '999999999999'):
            stream = mock.Mock()
            with self.subTest(length=length), self.assertRaises(RequestInputError):
                read_json_object(self.headers(length), stream)
            stream.read.assert_not_called()

    def test_duplicate_length_and_transfer_encoding_are_rejected(self):
        headers = self.headers('2')
        headers['Content-Length'] = '2'
        with self.assertRaises(RequestInputError):
            read_json_object(headers, io.BytesIO(b'{}'))
        headers = self.headers('2')
        headers['Transfer-Encoding'] = 'chunked'
        with self.assertRaises(RequestInputError):
            read_json_object(headers, io.BytesIO(b'{}'))

    def test_only_complete_utf8_json_objects_are_accepted(self):
        for raw in (b'[]', b'null', b'"text"', b'12', b'{', b'\xff\xff'):
            with self.subTest(raw=raw), self.assertRaises(RequestInputError):
                read_json_object(self.headers(str(len(raw))), io.BytesIO(raw))
        with self.assertRaises(RequestInputError):
            read_json_object(self.headers('3'), io.BytesIO(b'{}'))
        value = {'text': '原句完整保留'}
        raw = json.dumps(value, ensure_ascii=False).encode('utf-8')
        self.assertEqual(value, read_json_object(self.headers(str(len(raw))), io.BytesIO(raw)))

    def test_body_timeout_is_a_controlled_error(self):
        with self.assertRaises(RequestInputError) as found:
            read_json_object(self.headers('2'), mock.Mock(read=mock.Mock(side_effect=TimeoutError)))
        self.assertEqual(408, found.exception.status)

    def test_real_http_returns_errors_and_remains_usable_without_provider_calls(self):
        http_server = server.ThreadingHTTPServer(('127.0.0.1', 0), server.Handler)
        thread = threading.Thread(target=http_server.serve_forever, daemon=True)
        thread.start()
        try:
            with mock.patch.object(server, 'PORT', http_server.server_address[1]), \
                    mock.patch.object(server, 'provider_urlopen') as provider:
                for raw, length, expected in ((b'{}', 'abc', 400), (b'[]', '2', 400),
                                              (b'', str(MAX_BODY_BYTES+1), 413),
                                              (b'{}', '2', 404)):
                    connection = http.client.HTTPConnection(*http_server.server_address, timeout=3)
                    try:
                        connection.request('POST', '/unknown-fixture', raw, {'Content-Length': length})
                        response = connection.getresponse()
                        self.assertEqual(expected, response.status)
                        self.assertIn('error', json.loads(response.read()))
                    finally:
                        connection.close()
                provider.assert_not_called()
        finally:
            http_server.shutdown()
            http_server.server_close()
            thread.join(2)


@unittest.skipUnless(os.name == 'nt', 'Windows launcher test')
class LauncherOwnershipTests(unittest.TestCase):
    def test_bundled_runtime_imports_without_script_directory_on_initial_path(self):
        root = Path(__file__).resolve().parents[1]
        runtime = root / 'runtime' / 'python.exe'
        if not runtime.is_file():
            self.skipTest('Bundled Python is absent in this checkout')
        with tempfile.TemporaryDirectory(prefix='realtime-dictionary-bundle-import-') as temporary:
            fixture = Path(temporary)
            for name in ('server.py', 'request_validation.py', 'provider_policy.py',
                         'provider_transport.py', 'credential_store.py', 'calendar_export.py',
                         'outlook_calendar.py', 'version.txt'):
                shutil.copyfile(root / name, fixture / name)
            profile = fixture / 'profile' / 'RealtimeDictionary'
            profile.mkdir(parents=True)
            (profile / 'config.json').write_text('{}', encoding='utf-8')
            # Exclude real account configuration and prohibit network before importing.
            code = """import runpy, socket, sys
def no_network(*args, **kwargs): raise AssertionError('Import attempted a network connection')
socket.socket.connect = no_network
loaded = runpy.run_path(sys.argv[1], run_name='isolated_bundle_import')
assert not loaded['API_KEY'] and 'TYPESAFE_API_KEY' not in loaded
print('embedded-path-bootstrap-no-keys-no-network-ok')
"""
            env = {'SystemRoot': os.environ.get('SystemRoot', ''),
                   'APPDATA': str(fixture / 'profile')}
            result = subprocess.run([str(runtime), '-I', '-c', code, str(fixture / 'server.py')],
                                    cwd=fixture, env=env, capture_output=True, text=True, timeout=15)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn('embedded-path-bootstrap-no-keys-no-network-ok', result.stdout)

    def test_exact_script_argument_not_backup_test_or_substring(self):
        script = Path(__file__).resolve().parents[1] / 'start.ps1'
        quoted_script = str(script).replace("'", "''")
        command = f"""
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile('{quoted_script}', [ref]$tokens, [ref]$errors)
$fn = $ast.Find({{param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-ProjectBackendCommandLine'}}, $true)
Invoke-Expression $fn.Extent.Text
$target = 'D:\\fixture with space\\semantic-overlay\\server.py'
$cases = @(
 @('python.exe "D:\\fixture with space\\semantic-overlay\\server.py"', $true),
 @('python.exe "d:\\FIXTURE WITH SPACE\\SEMANTIC-OVERLAY\\SERVER.PY" --flag', $true),
 @('python.exe "D:\\fixture with space\\semantic-overlay-backup\\server.py"', $false),
 @('python.exe "D:\\fixture with space\\semantic-overlay\\tests\\test_server.py"', $false),
 @('python.exe "D:\\fixture with space\\semantic-overlay\\server.py.bak"', $false),
 @('python.exe unrelated.py --fixture "D:\\fixture with space\\semantic-overlay\\server.py"', $false),
 @('python.exe -u -B "D:\\fixture with space\\semantic-overlay\\server.py"', $true)
)
foreach ($case in $cases) {{ if ((Test-ProjectBackendCommandLine $case[0] $target) -ne $case[1]) {{ throw 'ownership matcher regression' }} }}
'exact-launcher-ownership-ok'
"""
        result = subprocess.run(['pwsh', '-NoProfile', '-Command', command],
                                capture_output=True, text=True, timeout=15)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('exact-launcher-ownership-ok', result.stdout)
