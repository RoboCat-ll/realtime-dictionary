"""Bounded HTTP/1.1 reuse underneath urllib's existing proxy/error handlers.

No credentials, messages or response bodies are logged or retained in the pool.
The urllib entry point remains intact so offline tests can deny external traffic.
"""
import atexit
import hashlib
import http.client
import socket
import threading
import time
import urllib.error
import urllib.parse
import urllib.request


class TransportBusy(RuntimeError):
    pass


class HeaderBudgetTimeout(TimeoutError):
    pass


def remaining(request):
    cancelled = getattr(request, "rd_cancel", None)
    if cancelled is not None and cancelled.is_set():
        raise TimeoutError("Model request cancelled")
    left = request.rd_deadline - time.monotonic()
    if left <= 0:
        raise TimeoutError("Model request deadline exhausted")
    return left


def mark(request, phase, field=None):
    timing = getattr(request, "rd_timing", None)
    if timing is not None:
        timing["phase"] = phase
        if field:
            timing[field] = round((time.monotonic() - request.rd_started) * 1000)


class ConnectionPool:
    def __init__(self, capacity=8, idle_seconds=20):
        self.capacity, self.idle_seconds = capacity, idle_seconds
        self.lock = threading.Lock()
        self.idle = []
        self.active = 0

    def take(self, key, factory):
        now = time.monotonic()
        with self.lock:
            retained = []
            selected = None
            for entry_key, connection, returned in self.idle:
                if now - returned >= self.idle_seconds or connection.sock is None:
                    connection.close()
                elif entry_key == key and selected is None:
                    selected = connection
                else:
                    retained.append((entry_key, connection, returned))
            self.idle = retained
            if self.active >= self.capacity:
                if selected is not None:
                    self.idle.append((key, selected, now))
                raise TransportBusy("Provider transport is busy")
            self.active += 1
        try:
            return (selected, True) if selected is not None else (factory(), False)
        except BaseException:
            with self.lock:
                self.active -= 1
            raise

    def give_back(self, key, connection, reusable):
        with self.lock:
            self.active -= 1
            if reusable and connection.sock is not None and len(self.idle) < self.capacity:
                self.idle.append((key, connection, time.monotonic()))
            else:
                connection.close()

    def close_idle(self):
        with self.lock:
            for _, connection, _ in self.idle:
                connection.close()
            self.idle.clear()


POOL = ConnectionPool()
atexit.register(POOL.close_idle)


class SocketDeadline:
    """Interrupt trickling headers/body at the absolute deadline, not inactivity.

    Synchronize cancellation before pool return so an old timer cannot shut down
    a connection already leased to a later request.
    """
    def __init__(self, connection_socket, deadline):
        self.socket = connection_socket
        self.lock = threading.Lock()
        self.finished = self.expired = False
        self.timer = threading.Timer(max(0, deadline - time.monotonic()), self.expire)
        self.timer.daemon = True
        self.timer.start()

    def expire(self):
        with self.lock:
            if self.finished:
                return
            self.expired = True
            try:
                self.socket.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass

    def stop(self):
        with self.lock:
            self.finished = True
            self.timer.cancel()
            return not self.expired


class PooledResponse:
    def __init__(self, response, connection, request, pool, key, guard):
        self.response, self.connection, self.request = response, connection, request
        self.pool, self.key = pool, key
        self.guard = guard
        self.complete = False
        self.closed = False
        self.status = self.code = response.status
        self.msg = self.reason = response.reason
        self.headers = response.headers
        self.url = request.full_url

    def info(self):
        return self.headers

    def geturl(self):
        return self.url

    def getcode(self):
        return self.code

    def read(self, size=None):
        if self.closed:
            return b""
        mark(self.request, "reading_body")
        chunks = []
        total = 0
        try:
            while size is None or total < size:
                left = remaining(self.request)
                # getresponse() transfers ownership when the server closes; keep
                # the original socket reference to bound that response too.
                self.request.rd_socket.settimeout(left)
                count = 65536 if size is None else min(65536, size - total)
                chunk = self.response.read1(count)
                if not chunk:
                    if self.response.length not in (None, 0):
                        raise http.client.IncompleteRead(b"", self.response.length)
                    self.complete = True
                    break
                chunks.append(chunk)
                total += len(chunk)
                if total > 4 * 1024 * 1024:
                    raise ValueError("Provider response too large")
                if self.response.isclosed():
                    if self.response.length not in (None, 0):
                        raise http.client.IncompleteRead(b"", self.response.length)
                    self.complete = True
                    break
            remaining(self.request)
            mark(self.request, "body_complete", "read_ms")
            return b"".join(chunks)
        except BaseException:
            self.close()
            raise

    def close(self):
        if self.closed:
            return
        self.closed = True
        within_deadline = self.guard.stop()
        self.response.close()
        reusable = (within_deadline and self.complete and not self.response.will_close and
                    time.monotonic() < self.request.rd_deadline and
                    not (getattr(self.request, "rd_cancel", None) and self.request.rd_cancel.is_set()))
        self.pool.give_back(self.key, self.connection, reusable)

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()


def open_connection(request, connection_class, pool=POOL):
    if not hasattr(request, "rd_deadline"):
        request.rd_started = time.monotonic()
        request.rd_deadline = request.rd_started + float(request.timeout)
    headers = dict(request.unredirected_hdrs)
    headers.update({k: v for k, v in request.headers.items() if k not in headers})
    headers = {name.title(): value for name, value in headers.items()}
    headers["Connection"] = "keep-alive"
    tunnel = request._tunnel_host
    proxy_auth = headers.get("Proxy-Authorization", "")
    if tunnel:
        headers.pop("Proxy-Authorization", None)
    origin = urllib.parse.urlsplit(request.full_url).netloc.lower()
    # This in-memory identity is never returned in health, logs or usage files.
    identity = hashlib.sha256((headers.get("Authorization", "") + "\0" + proxy_auth).encode()).digest()
    key = (request.type, request.host.lower(), tunnel, origin, identity)

    def factory():
        connection = connection_class(request.host, timeout=min(3.0, remaining(request)))
        if tunnel:
            connection.set_tunnel(tunnel, headers={"Proxy-Authorization": proxy_auth} if proxy_auth else {})
        return connection

    connection, reused = pool.take(key, factory)
    guard = None
    header_deadline = None
    timing = getattr(request, "rd_timing", None)
    if timing is not None:
        timing["connection_reused"] = reused
    try:
        mark(request, "connecting")
        if connection.sock is None:
            connection.connect()
        remaining(request)  # A stalled DNS resolution must not send after expiry.
        mark(request, "submitting", "connect_ms")
        request.rd_socket = connection.sock
        guard = SocketDeadline(connection.sock, request.rd_deadline)
        connection.sock.settimeout(remaining(request))
        connection.request(request.get_method(), request.selector, request.data, headers,
                           encode_chunked=request.has_header("Transfer-encoding"))
        mark(request, "awaiting_headers", "submitted_ms")
        connection.sock.settimeout(remaining(request))
        header_budget = getattr(request, "rd_header_budget", None)
        if header_budget is not None:
            guard.stop()
            header_deadline = min(request.rd_deadline, time.monotonic() + header_budget)
            guard = SocketDeadline(connection.sock, header_deadline)
            connection.sock.settimeout(min(remaining(request), header_budget))
        response = connection.getresponse()
        remaining(request)
        if header_deadline is not None:
            if not guard.stop():
                response.close()
                raise HeaderBudgetTimeout("Initial response header budget exhausted")
            guard = SocketDeadline(request.rd_socket, request.rd_deadline)
        mark(request, "reading_body", "headers_ms")
        return PooledResponse(response, connection, request, pool, key, guard)
    except BaseException as error:
        if guard is not None:
            guard.stop()
        pool.give_back(key, connection, False)
        if (header_deadline is not None and time.monotonic() >= header_deadline and
                time.monotonic() < request.rd_deadline and isinstance(error, Exception)):
            raise HeaderBudgetTimeout("Initial response header budget exhausted") from None
        raise


class HTTPHandler(urllib.request.HTTPHandler):
    def http_open(self, request):
        return open_connection(request, http.client.HTTPConnection)


class HTTPSHandler(urllib.request.HTTPSHandler):
    def https_open(self, request):
        return open_connection(request, http.client.HTTPSConnection)
