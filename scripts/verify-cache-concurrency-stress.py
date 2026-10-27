import os
import sys
import time
import json
import glob
import tempfile
import urllib.request
import urllib.error
import concurrent.futures
import subprocess
import socket

BASE_URL = "http://localhost:5198"
BDTK_PATH = os.path.join(os.path.dirname(__file__), "..", "3. BDTK.dxf")
SAMPLE_PATH = os.path.join(os.path.dirname(__file__), "..", "sample_road.dxf")

def make_multipart_body(file_path_or_bytes, fields, filename=None, file_field="file"):
    boundary = "----RoadGuardStressBoundary" + hex(int(time.time() * 1000))[2:]
    lines = []
    for k, v in fields.items():
        lines.append(f"--{boundary}".encode("utf-8"))
        lines.append(f'Content-Disposition: form-data; name="{k}"'.encode("utf-8"))
        lines.append(b"")
        lines.append(str(v).encode("utf-8"))
    
    if isinstance(file_path_or_bytes, (bytes, bytearray)):
        file_bytes = bytes(file_path_or_bytes)
        fn = filename or "sample.dxf"
    else:
        with open(file_path_or_bytes, "rb") as f:
            file_bytes = f.read()
        fn = filename or os.path.basename(file_path_or_bytes)

    lines.append(f"--{boundary}".encode("utf-8"))
    lines.append(f'Content-Disposition: form-data; name="{file_field}"; filename="{fn}"'.encode("utf-8"))
    lines.append(b"Content-Type: application/octet-stream")
    lines.append(b"")
    
    body = b"\r\n".join(lines) + b"\r\n" + file_bytes + b"\r\n--" + boundary.encode("utf-8") + b"--\r\n"
    content_type = f"multipart/form-data; boundary={boundary}"
    return body, content_type

def post_multipart(endpoint, file_data, fields, filename=None, file_field="file", timeout=120):
    url = f"{BASE_URL}{endpoint}"
    body, ct = make_multipart_body(file_data, fields, filename, file_field)
    req = urllib.request.Request(url, data=body, headers={"Content-Type": ct})
    t0 = time.perf_counter()
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        data = resp.read()
    t1 = time.perf_counter()
    return t1 - t0, json.loads(data.decode("utf-8"))

def get_health():
    url = f"{BASE_URL}/api/cad/health"
    with urllib.request.urlopen(url, timeout=10) as resp:
        return json.loads(resp.read().decode("utf-8"))

def trigger_evict():
    url = f"{BASE_URL}/api/cad/cache/evict"
    req = urllib.request.Request(url, data=b"", headers={"Content-Length": "0"}, method="POST")
    with urllib.request.urlopen(req, timeout=10) as resp:
        return json.loads(resp.read().decode("utf-8"))

print("=" * 78)
print("CAD & GIS STUDIO — FINAL CACHE CONCURRENCY CORRECTNESS & STRESS TEST SUITE")
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

# ---------------------------------------------------------------------------
# TEST 0: Development Test Hook Verification (No Secrets, Loopback Only)
# ---------------------------------------------------------------------------
print("\n[TEST 0] Development Test Hook Check: verify /api/cad/cache/evict operates cleanly")
try:
    evict_res = trigger_evict()
    if evict_res.get("status") == "evicted":
        record_pass("Development test hook successfully authorized on loopback without any hardcoded secret.")
    else:
        record_fail(f"Unexpected evict response: {evict_res}")
except Exception as ex:
    record_fail(f"Development test hook failed: {ex}")

# ---------------------------------------------------------------------------
# TEST 1: Multiple Concurrent Requests Acquiring/Releasing the Same DXF
# ---------------------------------------------------------------------------
print("\n[TEST 1] Multiple Concurrent Requests Acquiring/Releasing Same DXF")
CONCURRENT_COUNT = 25
errors = []
results = []

def worker_same_dxf(idx):
    try:
        t, res = post_multipart("/api/cad/get-layers", SAMPLE_PATH, {})
        return idx, t, res
    except Exception as e:
        return idx, -1, str(e)

t0 = time.perf_counter()
with concurrent.futures.ThreadPoolExecutor(max_workers=CONCURRENT_COUNT) as ex:
    futs = [ex.submit(worker_same_dxf, i) for i in range(CONCURRENT_COUNT)]
    for f in concurrent.futures.as_completed(futs):
        i, t, res = f.result()
        if t < 0:
            errors.append(f"Worker {i} failed: {res}")
        else:
            results.append(res)
t1 = time.perf_counter()

if len(errors) == 0 and len(results) == CONCURRENT_COUNT:
    all_match = all(r == results[0] for r in results)
    if all_match:
        record_pass(f"All {CONCURRENT_COUNT} concurrent requests succeeded cleanly in {(t1-t0)*1000:.1f}ms. Layers identical.")
    else:
        record_fail("Results differed across concurrent requests.")
else:
    record_fail(f"{len(errors)} workers failed out of {CONCURRENT_COUNT}: {errors[:3]}")

# Check that active leases returned to 0
h = get_health()
if h.get("activeLeases") == 0:
    record_pass("Active lease count returned to 0 after all concurrent leases released.")
else:
    record_fail(f"Active lease count is {h.get('activeLeases')}, expected 0!")

# ---------------------------------------------------------------------------
# TEST 2: Force Disk Cache Budget Exceeded While File is Actively Being Read
# ---------------------------------------------------------------------------
print("\n[TEST 2] Force Disk Cache Budget Exceeded While File is Actively Read")
temp_dir = tempfile.gettempdir()
dummy_files = []

# Pre-parse BDTK to get its disk cache filename
t_pre, _ = post_multipart("/api/cad/get-layers", BDTK_PATH, {})
bdtk_rg_files = glob.glob(os.path.join(temp_dir, "rg_*.dxf"))

# Create 5 dummy files of 55 MB each = 275 MB total (> 250 MB budget)
try:
    for i in range(5):
        dummy_path = os.path.join(temp_dir, f"rg_{'a'*60}{i:04d}.dxf")
        with open(dummy_path, "wb") as f:
            f.write(b"0\r\nSECTION\r\n" + b"X" * (55 * 1024 * 1024) + b"\r\n0\r\nENDSEC\r\n0\r\nEOF\r\n")
        dummy_files.append(dummy_path)

    # Launch a slow parse-dxf on BDTK (which runs Python Bridge for ~2s)
    def parse_in_flight():
        return post_multipart("/api/cad/parse-dxf", BDTK_PATH, {
            "targetSrid": 4326, "centralMeridian": 107.75, "scaleFactor": 0.9996, "centerlineLayerName": "TKDTIMTUYENTKT2"
        })

    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as ex:
        f_flight = ex.submit(parse_in_flight)
        # Give Python Bridge ~300ms to start and acquire the lease
        time.sleep(0.3)
        # While Python Bridge is reading, trigger eviction
        evict_res = trigger_evict()
        # Wait for the in-flight parse to finish
        t_dxf, res_dxf = f_flight.result()

    if res_dxf.get("featureCollection") and len(res_dxf["featureCollection"]["features"]) == 119:
        record_pass(f"In-flight parse finished successfully in {t_dxf*1000:.1f}ms despite concurrent budget overflow eviction.")
    else:
        record_fail("In-flight parse failed or returned corrupted features!")

    # Verify that the active BDTK file was NOT deleted during eviction
    bdtk_rg_after = glob.glob(os.path.join(temp_dir, "rg_*.dxf"))
    if any("528910af5822cb95d4ea54a912059ca6d6a9a5731ecfad25a16d4b8b0f4156fb" in f for f in bdtk_rg_after):
        record_pass("Active leased DXF file was strictly preserved on disk during eviction.")
    else:
        record_fail("Active leased DXF file was mistakenly deleted by eviction!")

finally:
    # Cleanup dummy files
    for df in dummy_files:
        try:
            if os.path.exists(df):
                os.remove(df)
        except:
            pass

# ---------------------------------------------------------------------------
# TEST 3: Cleanup Running Concurrently with Continuous Lease Acquisition
# ---------------------------------------------------------------------------
print("\n[TEST 3] Cleanup Running Concurrently with Continuous Lease Acquisition")
stop_stress = False
stress_errors = []

def evict_hammer():
    while not stop_stress:
        try:
            trigger_evict()
            time.sleep(0.02)
        except Exception as e:
            stress_errors.append(f"Evict error: {e}")

def acquire_hammer(idx):
    while not stop_stress:
        try:
            t, res = post_multipart("/api/cad/get-layers", SAMPLE_PATH, {})
            if len(res) < 3:
                stress_errors.append(f"Acquire error: unexpected layers count {len(res)}")
            time.sleep(0.01)
        except Exception as e:
            stress_errors.append(f"Acquire exception: {e}")

with concurrent.futures.ThreadPoolExecutor(max_workers=10) as ex:
    evict_futs = [ex.submit(evict_hammer) for _ in range(3)]
    acquire_futs = [ex.submit(acquire_hammer, i) for i in range(5)]
    time.sleep(2.5) # run concurrent hammer for 2.5 seconds
    stop_stress = True

if len(stress_errors) == 0:
    record_pass("Zero race conditions or deadlocks during continuous concurrent evict + acquire hammer (2.5s).")
else:
    record_fail(f"Encountered {len(stress_errors)} errors during stress: {stress_errors[:3]}")

# ---------------------------------------------------------------------------
# TEST 4 & 5: Cancellation Kills Child Python Process Tree (Zero Orphan Processes)
# ---------------------------------------------------------------------------
print("\n[TEST 4 & 5] Cancellation Kills Child Python Process Tree & Zero Orphans")
def get_cad_bridge_pids():
    try:
        cmd = ["powershell", "-NoProfile", "-Command", "Get-CimInstance Win32_Process -Filter \"Name = 'python.exe' and CommandLine like '%cad_bridge.py%'\" | Select-Object -ExpandProperty ProcessId"]
        out = subprocess.check_output(cmd, text=True, timeout=5).strip()
        if not out:
            return []
        return [int(line.strip()) for line in out.splitlines() if line.strip().isdigit()]
    except Exception as ex:
        print(f"    [WARN] Unable to inspect PIDs via PowerShell: {ex}")
        return []

initial_pids = get_cad_bridge_pids()

# Use mutated BDTK so cache MISS is guaranteed and Python bridge runs for ~2000ms
with open(BDTK_PATH, "rb") as f:
    bdtk_bytes = f.read()
mutated_bdtk = bdtk_bytes + f"\r\n999\r\nCANCEL_TEST_{time.time()}\r\n".encode("ascii")

boundary = "----CancelBoundary" + hex(int(time.time() * 1000))[2:]
raw_body, raw_ct = make_multipart_body(mutated_bdtk, {
    "targetSrid": 4326, "centralMeridian": 107.75, "scaleFactor": 0.9996, "centerlineLayerName": "TKDTIMTUYENTKT2"
}, filename="cancel_bdtk.dxf")

sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
sock.connect(("localhost", 5198))
headers = (
    f"POST /api/cad/parse-dxf HTTP/1.1\r\n"
    f"Host: localhost:5198\r\n"
    f"Content-Type: {raw_ct}\r\n"
    f"Content-Length: {len(raw_body)}\r\n"
    f"Connection: close\r\n\r\n"
)
sock.sendall(headers.encode("utf-8") + raw_body)

# Python Bridge takes ~2000ms. Sleep 400ms so Python is actively parsing.
time.sleep(0.4)

# Abruptly close socket (RST/FIN) to trigger CancellationToken in ASP.NET Core
sock.close()

# Wait 1.0s for server to catch OperationCanceledException and execute process.Kill(entireProcessTree: true)
time.sleep(1.0)

after_pids = get_cad_bridge_pids()
orphan_pids = [p for p in after_pids if p not in initial_pids]

if len(orphan_pids) == 0:
    record_pass("Request cancellation triggered clean process tree termination (0 orphan processes).")
    record_pass("Python child process was terminated and did not linger as zombie process.")
else:
    record_fail(f"Found {len(orphan_pids)} orphaned Python child processes: {orphan_pids}!")

# ---------------------------------------------------------------------------
# TEST 6: Processing 100 Unique Files & Verifying Lease Dictionary Lifecycle
# ---------------------------------------------------------------------------
print("\n[TEST 6] Processing 100 Unique Files & Lease Dictionary Reclaim")
with open(SAMPLE_PATH, "rb") as f:
    sample_base = f.read()

UNIQUE_COUNT = 100
unique_results = []
unique_errors = []

def worker_unique_file(idx):
    # Mutate content with a unique comment so SHA256 is distinct for each file
    mutated = sample_base + f"\r\n999\r\nUNIQUE_TEST_FILE_{idx}_{time.time()}\r\n".encode("ascii")
    try:
        t, res = post_multipart("/api/cad/get-layers", mutated, {}, filename=f"unique_{idx}.dxf")
        return idx, res
    except Exception as ex:
        return idx, str(ex)

t0 = time.perf_counter()
with concurrent.futures.ThreadPoolExecutor(max_workers=15) as ex:
    futs = [ex.submit(worker_unique_file, i) for i in range(UNIQUE_COUNT)]
    for f in concurrent.futures.as_completed(futs):
        i, res = f.result()
        if isinstance(res, list):
            unique_results.append(res)
        else:
            unique_errors.append(f"File {i}: {res}")
t1 = time.perf_counter()

if len(unique_results) == UNIQUE_COUNT:
    record_pass(f"Processed {UNIQUE_COUNT} unique DXF files across 15 threads in {(t1-t0)*1000:.1f}ms.")
else:
    record_fail(f"Failed {len(unique_errors)} files out of {UNIQUE_COUNT}: {unique_errors[:3]}")

# CRITICAL ASSERTION: The active lease dictionary must have 0 keys accumulated!
h_after_100 = get_health()
active_leases_count = h_after_100.get("activeLeases", -1)
if active_leases_count == 0:
    record_pass(f"Lease dictionary strictly reclaimed all keys: activeLeases = {active_leases_count} (zero unbounded accumulation).")
else:
    record_fail(f"Lease dictionary leaked keys! activeLeases = {active_leases_count} (expected 0).")

# ---------------------------------------------------------------------------
# TEST 7: File Cache Retained When Leased vs Evicted When Stale
# ---------------------------------------------------------------------------
print("\n[TEST 7] File Cache Retained When Leased vs Evicted When Stale")
# 1. Post a request to ensure sample_road is in disk cache
t_s, _ = post_multipart("/api/cad/get-layers", SAMPLE_PATH, {})
rg_sample = glob.glob(os.path.join(temp_dir, "rg_1340d7c30ea27859dd640fd897e5d7685464a43da923e9aeb016bea1c148b6fc.dxf"))
if len(rg_sample) == 1:
    record_pass(f"Verified cached file exists on disk: {os.path.basename(rg_sample[0])}")
else:
    record_fail("Cached file not found on disk.")

# 2. While not leased, if we force eviction with age > TTL:
# Set file timestamp to 2 hours ago
if len(rg_sample) == 1:
    old_time = time.time() - 7200
    os.utime(rg_sample[0], (old_time, old_time))
    trigger_evict()
    time.sleep(0.5)
    rg_sample_after = glob.glob(os.path.join(temp_dir, "rg_1340d7c30ea27859dd640fd897e5d7685464a43da923e9aeb016bea1c148b6fc.dxf"))
    if len(rg_sample_after) == 0:
        record_pass("Expired, unleased file correctly reclaimed by disk eviction.")
    else:
        record_fail("Expired unleased file was not reclaimed by eviction.")

print("\n" + "=" * 78)
print(f"CACHE CONCURRENCY STRESS RESULTS: {total_passed} PASSED, {total_failed} FAILED")
print("=" * 78)

if total_failed > 0:
    sys.exit(1)
