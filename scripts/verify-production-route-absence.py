#!/usr/bin/env python3
"""
verify-production-route-absence.py
Verifies that the test-only route /api/cad/cache/evict does NOT exist in Production.
Asserts HTTP 404 Not Found under all circumstances (with or without previous secret headers).
"""

import sys
import json
import urllib.request
import urllib.error

BASE_URL = sys.argv[1] if len(sys.argv) > 1 else "https://roadguard-cad-parser.onrender.com"
print("=" * 78)
print(f"VERIFYING ROUTE ABSENCE ON: {BASE_URL}")
print("=" * 78)

total_passed = 0
total_failed = 0

def record_pass(msg):
    global total_passed
    total_passed += 1
    print(f"  [PASS] {msg}")

def record_fail(msg):
    global total_failed
    total_failed += 1
    print(f"  [FAIL] {msg}")

# 1. Health check verification
health_url = f"{BASE_URL}/api/cad/health"
try:
    with urllib.request.urlopen(health_url, timeout=15) as resp:
        data = json.loads(resp.read().decode("utf-8"))
        if data.get("status") == "healthy":
            record_pass(f"Backend is healthy at {health_url}")
        else:
            record_fail(f"Unexpected health response: {data}")
except Exception as ex:
    record_fail(f"Health check failed: {ex}")

# 2. POST /api/cad/cache/evict WITHOUT headers -> MUST return HTTP 404
evict_url = f"{BASE_URL}/api/cad/cache/evict"
try:
    req = urllib.request.Request(evict_url, data=b"", headers={"Content-Length": "0"}, method="POST")
    with urllib.request.urlopen(req, timeout=15) as resp:
        record_fail("Security violation! /api/cad/cache/evict returned 200 on production.")
except urllib.error.HTTPError as ex:
    if ex.code == 404:
        record_pass(f"POST /api/cad/cache/evict correctly returned HTTP 404 Not Found (standard absent route).")
    else:
        record_fail(f"Expected HTTP 404, got {ex.code}")
except Exception as ex:
    record_fail(f"Unexpected error: {ex}")

# 3. POST /api/cad/cache/evict WITH previous secret header -> MUST STILL return HTTP 404
try:
    headers = {
        "Content-Length": "0",
        "X-RoadGuard-Internal-Test": "rg-internal-cache-test-2026"
    }
    req = urllib.request.Request(evict_url, data=b"", headers=headers, method="POST")
    with urllib.request.urlopen(req, timeout=15) as resp:
        record_fail("Security violation! /api/cad/cache/evict accepted previous secret header on production.")
except urllib.error.HTTPError as ex:
    if ex.code == 404:
        record_pass(f"POST /api/cad/cache/evict WITH secret header strictly returned HTTP 404 Not Found (route does not exist).")
    else:
        record_fail(f"Expected HTTP 404, got {ex.code}")
except Exception as ex:
    record_fail(f"Unexpected error: {ex}")

# 4. GET /api/cad/cache/evict -> MUST return HTTP 404
try:
    req = urllib.request.Request(evict_url, method="GET")
    with urllib.request.urlopen(req, timeout=15) as resp:
        record_fail("GET /api/cad/cache/evict unexpectedly succeeded.")
except urllib.error.HTTPError as ex:
    if ex.code == 404:
        record_pass(f"GET /api/cad/cache/evict correctly returned HTTP 404 Not Found.")
    else:
        record_fail(f"Expected HTTP 404, got {ex.code}")
except Exception as ex:
    record_fail(f"Unexpected error: {ex}")

print("-" * 78)
print(f"ROUTE ABSENCE VERIFICATION RESULT: {total_passed} PASSED, {total_failed} FAILED")
print("-" * 78)

if total_failed > 0:
    sys.exit(1)
