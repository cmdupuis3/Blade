"""PM54 native child. Standalone diagnostic JSON sidecar; never a timing/checksum arm."""
import argparse
import ctypes
import json
import os
from pathlib import Path

import pm54


class Win32Memory:
    def __init__(self):
        if os.name != "nt":
            raise OSError("PM54 native child requires Windows")
        from ctypes import wintypes as w

        class SystemInfo(ctypes.Structure):
            _fields_ = [("architecture_union", w.DWORD), ("page_size", w.DWORD),
                        ("minimum_address", ctypes.c_void_p), ("maximum_address", ctypes.c_void_p),
                        ("processor_mask", ctypes.c_size_t), ("processor_count", w.DWORD),
                        ("processor_type", w.DWORD), ("allocation_granularity", w.DWORD),
                        ("processor_level", w.WORD), ("processor_revision", w.WORD)]

        class MemoryCounters(ctypes.Structure):
            _fields_ = [("cb", w.DWORD), ("faults", w.DWORD),
                        ("peak_ws", ctypes.c_size_t), ("ws", ctypes.c_size_t),
                        ("peak_paged_pool", ctypes.c_size_t), ("paged_pool", ctypes.c_size_t),
                        ("peak_nonpaged_pool", ctypes.c_size_t), ("nonpaged_pool", ctypes.c_size_t),
                        ("commit", ctypes.c_size_t), ("peak_commit", ctypes.c_size_t)]

        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.psapi = ctypes.WinDLL("psapi", use_last_error=True)
        self.kernel.GetSystemInfo.argtypes = [ctypes.POINTER(SystemInfo)]
        self.kernel.GetSystemInfo.restype = None
        self.kernel.GetCurrentProcess.restype = w.HANDLE
        self.kernel.VirtualAlloc.argtypes = [ctypes.c_void_p, ctypes.c_size_t, w.DWORD, w.DWORD]
        self.kernel.VirtualAlloc.restype = ctypes.c_void_p
        self.kernel.VirtualFree.argtypes = [ctypes.c_void_p, ctypes.c_size_t, w.DWORD]
        self.kernel.VirtualFree.restype = w.BOOL
        self.psapi.GetProcessMemoryInfo.argtypes = [w.HANDLE, ctypes.POINTER(MemoryCounters), w.DWORD]
        self.psapi.GetProcessMemoryInfo.restype = w.BOOL
        system = SystemInfo()
        self.kernel.GetSystemInfo(ctypes.byref(system))
        self.page_size = system.page_size
        self.allocation_granularity = system.allocation_granularity
        self.size_limit = (1 << (8 * ctypes.sizeof(ctypes.c_size_t))) - 1
        self.counter_type = MemoryCounters

    def reserve(self, size):
        address = self.kernel.VirtualAlloc(None, size, 0x2000, 0x04)  # MEM_RESERVE, PAGE_READWRITE
        if not address:
            raise ctypes.WinError(ctypes.get_last_error())
        return address

    def commit(self, address, size):
        committed = self.kernel.VirtualAlloc(address, size, 0x1000, 0x04)  # MEM_COMMIT
        if committed != address:
            raise ctypes.WinError(ctypes.get_last_error())

    def dense_store_and_sum(self, address, size):
        ctypes.memset(address, 1, size)
        # A zero-copy byte view observes every assigned byte before the region is freed.
        view = memoryview((ctypes.c_ubyte * size).from_address(address)).cast("B")
        try:
            return sum(view)
        finally:
            view.release()

    def sparse_store_and_sum(self, address, size, stride):
        for offset in range(0, size, stride):
            ctypes.c_ubyte.from_address(address + offset).value = 1
        return sum(ctypes.c_ubyte.from_address(address + offset).value for offset in range(0, size, stride))

    def release(self, address):
        # Original reservation base + zero size are required for MEM_RELEASE.
        return bool(self.kernel.VirtualFree(address, 0, 0x8000))

    def snapshot(self):
        counters = self.counter_type()
        counters.cb = ctypes.sizeof(counters)
        if not self.psapi.GetProcessMemoryInfo(self.kernel.GetCurrentProcess(), ctypes.byref(counters), counters.cb):
            return dict.fromkeys(pm54.COUNTERS)
        return {"current_resident_bytes": counters.ws, "current_commit_bytes": counters.commit,
                "peak_resident_bytes": counters.peak_ws, "peak_commit_bytes": counters.peak_commit,
                "page_fault_count": counters.faults}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--arm", choices=pm54.ARMS, required=True)
    parser.add_argument("--bytes", type=int, required=True, dest="requested")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        record = pm54.exercise(Win32Memory(), args.arm, args.requested)
    except Exception as error:
        record = {"ok": False, "error": f"{type(error).__name__}: {error}"}
    temporary = args.output.with_suffix(".tmp")
    temporary.write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
    temporary.replace(args.output)
    return 0 if record["ok"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
