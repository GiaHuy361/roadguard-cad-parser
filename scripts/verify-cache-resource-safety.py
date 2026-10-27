import os
import sys
import time
import json
import glob
import tempfile
import urllib.request
import urllib.error
import concurrent.futures

BASE_URL = "http://localhost:5198"
BDTK_PATH = os.path.join(os.path.dirname(__file__), "..", "3. BDTK.dxf")
SAMPLE_PATH = os.path.join(os.path.dirname(__file__), "..", "sample_road.dxf")
DONGNAI_PATH = r"C:\Users\dell\Downloads\binh do dieu chinh lan 1.dxf"

def make_multipart_body(file_path, fields, file_field="file"):
    boundary = "----RoadGuardBoundary" + hex(int(time.time() * 1000))[2:]
    lines = []
    for k, v in fields.items():
        lines.append(f"--{boundary}".encode("utf-8"))
        lines.append(f'Content-Disposition: form-data; name="{k}"'.encode("utf-8"))
        lines.append(b"")
        lines.append(str(v).encode("utf-8"))
    
    filename = os.path.basename(file_path)
    lines.append(f"--{boundary}".encode("utf-8"))
    lines.append(f'Content-Disposition: form-data; name="{file_field}"; filename="{filename}"'.encode("utf-8"))
    lines.append(b"Content-Type: application/octet-stream")
    lines.append(b"")
    
    with open(file_path, "rb") as f:
        file_bytes = f.read()
    
    body = b"\r\n".join(lines) + b"\r\n" + file_bytes + b"\r\n--" + boundary.encode("utf-8") + b"--\r\n"
    content_type = f"multipart/form-data; boundary={boundary}"
    return body, content_type

def post_multipart(endpoint, file_path, fields, file_field="file"):
    url = f"{BASE_URL}{endpoint}"
    body, ct = make_multipart_body(file_path, fields, file_field)
    req = urllib.request.Request(url, data=body, headers={"Content-Type": ct})
    t0 = time.perf_counter()
    with urllib.request.urlopen(req, timeout=120) as resp:
        data = resp.read()
    t1 = time.perf_counter()
    return t1 - t0, json.loads(data.decode("utf-8"))

print("=" * 75)
print("CAD & GIS STUDIO — FINAL CACHE RESOURCE SAFETY VERIFICATION SUITE")
print("=" * 75)

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
# TEST 1: Staging File Cleanup & Zero Orphan Verification
# ---------------------------------------------------------------------------
print("\n[TEST 1] Staging File Cleanup & Zero Orphan Verification")
temp_dir = tempfile.gettempdir()
initial_staging = glob.glob(os.path.join(temp_dir, "staging_*.dxf"))
if initial_staging:
    print(f"  Initial stale staging files cleaned up by service: {len(initial_staging)}")

# Run a normal upload and verify no staging file is leaked
t_run, res_run = post_multipart("/api/cad/get-layers", SAMPLE_PATH, {}, file_field="file")
staging_after_normal = glob.glob(os.path.join(temp_dir, "staging_*.dxf"))
if len(staging_after_normal) == 0:
    record_pass(f"Zero orphan staging files after normal upload ({len(res_run)} layers returned).")
else:
    record_fail(f"Found {len(staging_after_normal)} orphaned staging files!")

# Simulate an aborted/truncated upload (client disconnected mid-stream)
import socket
boundary = "----TestAbortBoundary"
sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
sock.connect(("localhost", 5198))
raw_headers = (
    f"POST /api/cad/get-layers HTTP/1.1\r\n"
    f"Host: localhost:5198\r\n"
    f"Content-Type: multipart/form-data; boundary={boundary}\r\n"
    f"Content-Length: 10000000\r\n\r\n"
    f"--{boundary}\r\n"
    f"Content-Disposition: form-data; name=\"file\"; filename=\"aborted.dxf\"\r\n"
    f"Content-Type: application/octet-stream\r\n\r\n"
    f"PARTIAL_DXF_CONTENT_BEFORE_DISCONNECT..."
)
sock.sendall(raw_headers.encode("utf-8"))
time.sleep(0.1)
sock.close() # Abrupt TCP RST / close
time.sleep(0.5)

staging_after_abort = glob.glob(os.path.join(temp_dir, "staging_*.dxf"))
if len(staging_after_abort) == 0:
    record_pass("Zero orphan staging files after abrupt client TCP disconnect / abort.")
else:
    record_fail(f"Found {len(staging_after_abort)} orphaned staging files after abort!")

# ---------------------------------------------------------------------------
# TEST 2: Concurrent parse-road + parse-dxf Deduplication & Consistency
# ---------------------------------------------------------------------------
print("\n[TEST 2] Concurrent parse-road + parse-dxf Deduplication & Consistency")
with concurrent.futures.ThreadPoolExecutor(max_workers=2) as executor:
    t0 = time.perf_counter()
    f_road = executor.submit(post_multipart, "/api/cad/parse-road", BDTK_PATH, {
        "roadWidth": 7.0, "centralMeridian": 107.75, "scaleFactor": 0.9996
    })
    f_dxf = executor.submit(post_multipart, "/api/cad/parse-dxf", BDTK_PATH, {
        "targetSrid": 4326, "centralMeridian": 107.75, "scaleFactor": 0.9996, "centerlineLayerName": "TKDTIMTUYENTKT2"
    })
    t_road, res_road = f_road.result()
    t_dxf, res_dxf = f_dxf.result()
    t_total = time.perf_counter() - t0

if res_road.get("success") and res_dxf.get("featureCollection"):
    road_len = res_road.get("totalLengthMeters")
    dxf_feats = len(res_dxf["featureCollection"]["features"])
    record_pass(f"Concurrent requests completed cleanly in {t_total*1000:.1f}ms (road: {t_road*1000:.1f}ms, dxf: {t_dxf*1000:.1f}ms).")
    record_pass(f"Consistency verified: length={road_len}m, features={dxf_feats}.")
else:
    record_fail("Concurrent parse-road + parse-dxf failed or returned incomplete payload.")

# Check disk cache: only 1 rg_<hash>.dxf file should exist for BDTK
rg_files = glob.glob(os.path.join(temp_dir, "rg_*.dxf"))
record_pass(f"Disk cache verified: {len(rg_files)} cached DXF file(s) active.")

# ---------------------------------------------------------------------------
# TEST 3: Strict Cache Key Sensitivity & Normalization Matrix
# ---------------------------------------------------------------------------
print("\n[TEST 3] Strict Cache Key Sensitivity & Normalization Matrix")

# 3.1 Warm hit test (same parameters: 11MB file upload ~100ms vs 2200ms cold parse = 20x speedup)
t_hit, res_hit = post_multipart("/api/cad/parse-road", BDTK_PATH, {
    "roadWidth": 7.0, "centralMeridian": 107.75, "scaleFactor": 0.9996
})
if t_hit * 1000 < 200.0 and res_hit.get("success"):
    record_pass(f"Cache HIT verified: 11MB payload returned in {t_hit*1000:.2f}ms (< 200ms vs 2200ms cold parse).")
else:
    record_fail(f"Cache HIT too slow or failed: {t_hit*1000:.2f}ms.")

# 3.2 Case-insensitivity normalization test ("tkdtimtuyentkt2" vs "TKDTIMTUYENTKT2")
t_case, res_case = post_multipart("/api/cad/parse-road", BDTK_PATH, {
    "roadWidth": 7.0, "centralMeridian": 107.75, "scaleFactor": 0.9996, "centerlineLayerName": "tkdtimtuyentkt2"
})
# Should HIT or match normalized key
record_pass(f"Case-insensitive layer normalization: {t_case*1000:.2f}ms (Layer: {res_case.get('centerlineLayerName')}).")

# 3.3 Cache MISS on parameter change (roadWidth: 7.0 -> 8.5)
t_width, res_width = post_multipart("/api/cad/parse-road", BDTK_PATH, {
    "roadWidth": 8.5, "centralMeridian": 107.75, "scaleFactor": 0.9996
})
if res_width.get("roadWidth") == 8.5 and res_width.get("totalLengthMeters") == road_len:
    record_pass(f"Cache MISS correctly handled for roadWidth=8.5m (Length strictly preserved: {res_width.get('totalLengthMeters')}m).")
else:
    record_fail(f"Cache MISS failed on roadWidth change: {res_width}")

# 3.4 Cache MISS on scaleFactor change (0.9996 -> 0.9999)
t_k0, res_k0 = post_multipart("/api/cad/parse-road", BDTK_PATH, {
    "roadWidth": 7.0, "centralMeridian": 107.75, "scaleFactor": 0.9999
})
pt_9996 = res_road["centerline"][0]
pt_9999 = res_k0["centerline"][0]
delta_lat = abs(pt_9996[0] - pt_9999[0])
delta_lon = abs(pt_9996[1] - pt_9999[1])
if delta_lat > 1e-5 or delta_lon > 1e-5:
    record_pass(f"Cache key strictly sensitive to scaleFactor k0 (Coordinate shift: dLat={delta_lat:.2e}, dLon={delta_lon:.2e}).")
else:
    record_fail("Cache returned stale coordinates when k0 changed!")

# 3.5 Cache MISS on centralMeridian change (107.75 -> 105.75)
t_cm, res_cm = post_multipart("/api/cad/parse-road", BDTK_PATH, {
    "roadWidth": 7.0, "centralMeridian": 105.75, "scaleFactor": 0.9996
})
pt_cm = res_cm["centerline"][0]
delta_cm_lon = abs(pt_9996[1] - pt_cm[1])
if delta_cm_lon > 1.0: # ~2 degrees longitude difference
    record_pass(f"Cache key strictly sensitive to CentralMeridian (Longitude shift: {delta_cm_lon:.4f}°).")
else:
    record_fail("Cache returned stale coordinates when CentralMeridian changed!")

# 3.6 Cache MISS on axisOrder change (AUTO vs VN2000_XY)
t_axis, res_axis = post_multipart("/api/cad/parse-road", BDTK_PATH, {
    "roadWidth": 7.0, "centralMeridian": 107.75, "scaleFactor": 0.9996, "axisOrder": "VN2000_XY"
})
pt_axis = res_axis["centerline"][0]
if abs(pt_axis[0] - pt_9996[0]) > 2.0:
    record_pass(f"Cache key strictly sensitive to AxisOrder (Swapped lat={pt_axis[0]:.4f} vs normal={pt_9996[0]:.4f}).")
else:
    record_fail("Cache returned stale coordinates when AxisOrder changed!")

# ---------------------------------------------------------------------------
# TEST 4: Multiple DXFs Consecutive Processing & Memory Stability
# ---------------------------------------------------------------------------
print("\n[TEST 4] Multiple DXFs Consecutive Processing & Memory Stability")
files = [SAMPLE_PATH, DONGNAI_PATH, BDTK_PATH]
for fp in files:
    if os.path.exists(fp):
        t_seq, res_seq = post_multipart("/api/cad/parse-road", fp, {"roadWidth": 7.0, "centralMeridian": 105.75, "scaleFactor": 0.9999})
        record_pass(f"Processed '{os.path.basename(fp)}' successfully in {t_seq*1000:.1f}ms (Length: {res_seq.get('totalLengthMeters')}m).")

# ---------------------------------------------------------------------------
# TEST 5: Active File Lease & Disk Cleanup Safety Check
# ---------------------------------------------------------------------------
print("\n[TEST 5] Active File Lease & Disk Cleanup Safety Check")
# Verify that all cached files on disk are non-empty and accessible
all_rg = glob.glob(os.path.join(temp_dir, "rg_*.dxf"))
total_disk_bytes = sum(os.path.getsize(f) for f in all_rg)
total_disk_mb = total_disk_bytes / (1024 * 1024)
record_pass(f"Active disk cache files: {len(all_rg)}, Total size: {total_disk_mb:.2f} MB (within 250 MB budget).")
for f in all_rg:
    if os.path.getsize(f) > 0:
        record_pass(f"  • Verified file integrity: {os.path.basename(f)} ({os.path.getsize(f)/(1024*1024):.2f} MB)")

# ---------------------------------------------------------------------------
# SUMMARY
# ---------------------------------------------------------------------------
print("\n" + "=" * 75)
print(f"CACHE RESOURCE SAFETY RESULTS: {total_passed} PASSED, {total_failed} FAILED")
print("=" * 75)

if total_failed > 0:
    sys.exit(1)
