"""Real loopback HTTP faults; no paid endpoints or real account settings."""
import io
import json
import socket
import tempfile
import threading
import time
import unittest
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from unittest import mock

import provider_policy
import provider_transport as transport
import server


BODY = json.dumps({"choices": [{"message": {"content": "fixture response"}}],
                   "usage": {"prompt_tokens": 5, "completion_tokens": 3}}).encode()


class Fixture(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *_):
        pass

    def do_POST(self):
        self.rfile.read(int(self.headers.get("Content-Length", 0)))
        with self.server.lock:
            self.server.requests += 1
            count = self.server.requests
            self.server.ports.append(self.client_address[1])
            self.server.authorizations.append(self.headers.get("Authorization"))
        mode = self.server.mode
        if mode == "reset" and count == 1:
            self.connection.shutdown(socket.SHUT_RDWR)
            self.close_connection = True
            return
        if (mode == "slow_headers" or (mode == "slow_first_headers" and count == 1) or
                (mode == "two_slow_headers" and count <= 2)):
            time.sleep(0.35)
        status = (503 if mode == "busy" and count == 1 else
                  401 if mode == "auth" else 302 if mode == "redirect" else 200)
        self.send_response(status)
        self.send_header("Content-Length", str(len(BODY)))
        if status == 302:
            self.send_header("Location", "/credentials-must-not-follow")
        if mode == "close":
            self.send_header("Connection", "close")
            self.close_connection = True
        self.end_headers()
        try:
            if mode == "partial" and count == 1:
                self.wfile.write(BODY[:10])
                self.wfile.flush()
                self.connection.shutdown(socket.SHUT_RDWR)
                self.close_connection = True
            elif mode == "dribble":
                for byte in BODY:
                    self.wfile.write(bytes([byte]))
                    self.wfile.flush()
                    time.sleep(0.02)
            else:
                self.wfile.write(BODY)
        except (ConnectionError, OSError):
            self.close_connection = True


class TransportTests(unittest.TestCase):
    def setUp(self):
        transport.POOL.close_idle()
        self.profile = tempfile.TemporaryDirectory()
        self.http = ThreadingHTTPServer(("127.0.0.1", 0), Fixture)
        self.http.lock = threading.Lock()
        self.http.requests = 0
        self.http.ports = []
        self.http.authorizations = []
        self.http.mode = "ok"
        self.thread = threading.Thread(target=self.http.serve_forever, daemon=True)
        self.thread.start()
        self.endpoint = "http://127.0.0.1:%d" % self.http.server_port
        self.patches = [mock.patch.dict(server.os.environ, {"APPDATA": self.profile.name}),
                        mock.patch.object(server, "BASE_URL", self.endpoint),
                        mock.patch.object(server, "API_KEY", "fixture-only"),
                        mock.patch.object(server, "log")]
        for patch in self.patches:
            patch.start()

    def tearDown(self):
        transport.POOL.close_idle()
        self.http.shutdown()
        self.http.server_close()
        for patch in reversed(self.patches):
            patch.stop()
        self.profile.cleanup()
        self.assertEqual(0, transport.POOL.active)

    def call(self, **kwargs):
        return server.call_llm([{"role": "user", "content": "synthetic fixture"}], **kwargs)

    def test_keepalive_reuses_connection_and_records_physical_usage(self):
        first, second = {}, {}
        self.assertEqual("fixture response", self.call(timing=first))
        self.assertEqual("fixture response", self.call(timing=second))
        self.assertFalse(first["connection_reused"])
        self.assertTrue(second["connection_reused"])
        self.assertEqual(self.http.ports[0], self.http.ports[1])
        self.assertLessEqual(second["connect_ms"], second["submitted_ms"])
        self.assertLessEqual(second["submitted_ms"], second["headers_ms"])
        self.assertEqual(2, provider_policy.usage_summary()["groups"][0]["requests"])

    def test_changed_credential_cannot_reuse_old_authenticated_connection(self):
        self.call()
        timing = {}
        with mock.patch.object(server, "API_KEY", "second-fixture-only"):
            self.call(timing=timing)
        self.assertFalse(timing["connection_reused"])
        self.assertNotEqual(self.http.ports[0], self.http.ports[1])
        self.assertEqual("Bearer second-fixture-only", self.http.authorizations[1])

    def test_server_close_does_not_enter_pool(self):
        self.http.mode = "close"
        self.call()
        timing = {}
        self.call(timing=timing)
        self.assertFalse(timing["connection_reused"])

    def test_fast_reset_recovers_once_and_usage_is_unknown_for_first_attempt(self):
        self.http.mode = "reset"
        timing = {}
        self.assertEqual("fixture response", self.call(timing=timing, request_timeout=1))
        self.assertEqual(2, timing["attempts"])
        self.assertEqual(2, self.http.requests)
        usage = provider_policy.usage_summary()["groups"][0]
        self.assertEqual(2, usage["requests"])
        self.assertEqual(1, usage["unknown_usage_requests"])

    def test_partial_body_discards_socket_and_recovers(self):
        self.http.mode = "partial"
        self.assertEqual("fixture response", self.call(request_timeout=1))
        self.assertEqual(2, self.http.requests)
        self.assertNotEqual(self.http.ports[0], self.http.ports[1])

    def test_header_budget_reserves_time_for_one_recovery(self):
        self.http.mode = "slow_first_headers"
        open_provider = server.provider_urlopen

        def with_budget(request, timeout):
            if request.rd_timing["attempts"] == 1:
                request.rd_header_budget = 0.08
            return open_provider(request, timeout)

        timing = {}
        with mock.patch.object(server, "provider_urlopen", side_effect=with_budget):
            self.assertEqual("fixture response", self.call(timing=timing, request_timeout=1))
        self.assertEqual(2, timing["attempts"])
        self.assertEqual(2, self.http.requests)
        self.assertEqual(1, provider_policy.usage_summary()["groups"][0]["unknown_usage_requests"])

    def test_header_budget_does_not_shorten_body_generation(self):
        self.http.mode = "dribble"
        request = urllib.request.Request(self.endpoint + "/chat/completions", data=b'{"model":"fixture"}')
        request.rd_header_budget = 0.02
        started = time.monotonic()
        with server.provider_urlopen(request, timeout=0.15) as response:
            with self.assertRaises((TimeoutError, OSError, server.http.client.IncompleteRead)):
                response.read()
        self.assertGreater(time.monotonic() - started, 0.08)

    def test_two_header_failures_recover_without_exceeding_attempt_cap(self):
        self.http.mode = "two_slow_headers"
        open_provider = server.provider_urlopen

        def with_budget(request, timeout):
            if request.rd_timing["attempts"] < 3:
                request.rd_header_budget = 0.05
            return open_provider(request, timeout)

        timing = {}
        with mock.patch.object(server, "provider_urlopen", side_effect=with_budget):
            self.assertEqual("fixture response", self.call(timing=timing, retries=3, request_timeout=1))
        self.assertEqual(3, self.http.requests)
        self.assertEqual(3, timing["attempts"])
        self.assertEqual(2, provider_policy.usage_summary()["groups"][0]["unknown_usage_requests"])

    def test_repeated_reset_stops_at_explicit_attempt_cap(self):
        with mock.patch.object(server, "provider_urlopen", side_effect=ConnectionResetError()) as send:
            with self.assertRaises(RuntimeError):
                self.call(request_timeout=1, retries=3)
            self.assertEqual(3, send.call_count)

    def test_transient_503_retries_same_provider(self):
        self.http.mode = "busy"
        self.assertEqual("fixture response", self.call(request_timeout=1))
        self.assertEqual(2, self.http.requests)

    def test_authentication_failure_never_retries(self):
        self.http.mode = "auth"
        with self.assertRaisesRegex(RuntimeError, "401"):
            self.call()
        self.assertEqual(1, self.http.requests)

    def test_redirect_never_forwards_credential(self):
        self.http.mode = "redirect"
        with self.assertRaises(provider_policy.PolicyBlocked):
            self.call()
        self.assertEqual(1, self.http.requests)

    def test_header_timeout_does_not_create_a_fresh_deadline(self):
        self.http.mode = "slow_headers"
        timing = {}
        started = time.monotonic()
        with self.assertRaises(RuntimeError):
            self.call(request_timeout=0.12, timing=timing)
        self.assertLess(time.monotonic() - started, 0.3)
        self.assertEqual("awaiting_headers", timing["phase"])
        self.assertEqual(1, self.http.requests)

    def test_dribbling_body_cannot_extend_total_deadline(self):
        self.http.mode = "dribble"
        started = time.monotonic()
        with self.assertRaises(RuntimeError):
            self.call(request_timeout=0.12)
        self.assertLess(time.monotonic() - started, 0.3)
        self.assertEqual(1, self.http.requests)

    def test_cancelled_worker_sends_no_request_and_no_usage(self):
        cancelled = threading.Event()
        cancelled.set()
        with self.assertRaises(TimeoutError):
            self.call(_cancel=cancelled)
        self.assertEqual(0, self.http.requests)
        self.assertEqual([], provider_policy.usage_summary()["groups"])

    def test_retry_after_larger_than_remaining_budget_is_not_retried(self):
        error = urllib.error.HTTPError(self.endpoint, 503, "busy", {"Retry-After": "3"}, io.BytesIO())
        with mock.patch.object(server, "provider_urlopen", side_effect=error) as send:
            with self.assertRaisesRegex(RuntimeError, "503"):
                self.call(request_timeout=0.5)
            self.assertEqual(1, send.call_count)

    def test_permanent_and_malformed_errors_are_not_retryable(self):
        for error in (ValueError(), urllib.error.HTTPError(self.endpoint, 429, "limited", {}, None),
                      urllib.error.HTTPError(self.endpoint, 402, "balance", {}, None),
                      TimeoutError()):
            self.assertFalse(server.retryable_text_error(error, "awaiting_headers"))

    def test_pool_rejects_excess_active_connections_without_waiting(self):
        pool = transport.ConnectionPool(capacity=1)
        connection = mock.Mock()
        connection.sock = object()
        checked, reused = pool.take("a", lambda: connection)
        with self.assertRaises(transport.TransportBusy):
            pool.take("a", lambda: mock.Mock())
        self.assertFalse(reused)
        pool.give_back("a", checked, False)
        self.assertEqual(0, pool.active)

    def test_expired_idle_connection_is_discarded(self):
        pool = transport.ConnectionPool(idle_seconds=0)
        first = mock.Mock()
        checked, _ = pool.take("a", lambda: first)
        pool.give_back("a", checked, True)
        second = mock.Mock()
        checked, reused = pool.take("a", lambda: second)
        self.assertFalse(reused)
        first.close.assert_called_once()
        pool.give_back("a", checked, False)

    def test_deadline_worker_cap_prevents_unbounded_threads(self):
        gate = threading.BoundedSemaphore(1)
        release, finished = threading.Event(), threading.Event()

        def stalled(*args, **kwargs):
            release.wait(1)
            finished.set()
            return "late"

        with mock.patch.object(server, "LLM_WORKERS", gate), mock.patch.object(server, "call_llm", side_effect=stalled):
            try:
                with self.assertRaises(TimeoutError):
                    server.call_llm_with_deadline([], 0.02)
                with self.assertRaises(transport.TransportBusy):
                    server.call_llm_with_deadline([], 0.02)
            finally:
                release.set()
                self.assertTrue(finished.wait(1))

    def test_proxy_tunnel_keeps_proxy_auth_out_of_origin_headers(self):
        request = urllib.request.Request("https://origin.invalid/v1/chat/completions",
            data=b"{}", headers={"Authorization": "Bearer fixture",
                                "Proxy-Authorization": "Basic proxy-fixture"})
        request.set_proxy("proxy.invalid:8443", "http")
        request.timeout = 1
        connection = mock.Mock()
        response = mock.Mock(status=200, reason="OK", headers={})
        connection.getresponse.return_value = response
        pool = transport.ConnectionPool()
        result = transport.open_connection(request, mock.Mock(return_value=connection), pool)
        connection.set_tunnel.assert_called_once_with("origin.invalid", headers={"Proxy-Authorization": "Basic proxy-fixture"})
        sent_headers = connection.request.call_args.args[3]
        self.assertNotIn("Proxy-Authorization", sent_headers)
        self.assertEqual("Bearer fixture", sent_headers["Authorization"])
        result.close()
        self.assertEqual(0, pool.active)

    def test_cancelled_deadline_timer_cannot_shutdown_reused_socket(self):
        connection_socket = mock.Mock()
        guard = transport.SocketDeadline(connection_socket, time.monotonic() + 0.02)
        self.assertTrue(guard.stop())
        guard.expire()  # Simulate a callback that was already queued.
        connection_socket.shutdown.assert_not_called()

    def test_socket_deadline_interrupts_headers_that_never_finish(self):
        connection_socket = mock.Mock()
        guard = transport.SocketDeadline(connection_socket, time.monotonic() + 1)
        guard.expire()
        self.assertFalse(guard.stop())
        connection_socket.shutdown.assert_called_once_with(socket.SHUT_RDWR)


if __name__ == "__main__":
    unittest.main()
