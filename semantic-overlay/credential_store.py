"""Read Windows CurrentUser DPAPI secrets; never migrate live configuration."""
import base64
import ctypes
import os
from ctypes import wintypes


def unprotect(value, endpoint, slot):
    if os.name != "nt" or not isinstance(value, str) or not value.startswith("dpapi-v1:"):
        raise ValueError("protected credential unavailable")
    raw = base64.b64decode(value[9:], validate=True)
    entropy = ("RealtimeDictionary|v1|" + slot + "|" + endpoint).encode("utf-8")

    class Blob(ctypes.Structure):
        _fields_ = [("size", wintypes.DWORD), ("data", ctypes.POINTER(ctypes.c_ubyte))]

    def blob(data):
        buffer = (ctypes.c_ubyte * len(data)).from_buffer_copy(data)
        return Blob(len(data), buffer), buffer

    source, source_buffer = blob(raw)
    optional, entropy_buffer = blob(entropy)
    output = Blob()
    crypt = ctypes.WinDLL("crypt32", use_last_error=True).CryptUnprotectData
    crypt.argtypes = [ctypes.POINTER(Blob), ctypes.c_void_p, ctypes.POINTER(Blob),
                     ctypes.c_void_p, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(Blob)]
    crypt.restype = wintypes.BOOL
    free = ctypes.WinDLL("kernel32", use_last_error=True).LocalFree
    free.argtypes = [ctypes.c_void_p]
    free.restype = ctypes.c_void_p
    if not crypt(ctypes.byref(source), None, ctypes.byref(optional), None, None, 1,
                 ctypes.byref(output)):
        raise ValueError("protected credential cannot be decrypted")
    try:
        return ctypes.string_at(output.data, output.size).decode("utf-8")
    finally:
        free(output.data)
