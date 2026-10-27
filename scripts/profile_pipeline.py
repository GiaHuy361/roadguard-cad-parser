import os
import sys
import time
import json
import subprocess
import urllib.request
import urllib.error
from urllib.parse import urlencode

DXF_FILES = [
    {
        "name": "sample_road.dxf",
        "path": os.path.join(os.path.dirname(__file__), "..", "sample_road.dxf"),
        "category": "Extra Small (18 KB)",
        "cm": 105.75,
        "k0": 0.9999
    },
    {
        "name": "binh do dieu chinh lan 1.dxf",
        "path": r"C:\Users\dell\Downloads\binh do dieu chinh lan 1.dxf",
        "category": "Small (406 KB)",
        "cm": 105.75,
        "k0": 0.9999
    },
    {
        "name": "ROAD_CENTERLINE.dxf",
        "path": os.path.join(os.path.dirname(__file__), "..", "ROAD_CENTERLINE.dxf"),
        "category": "Medium (6.8 MB)",
        "cm": 105.75,
        "k0": 0.9999
    },
    {
        "name": "3. BDTK.dxf",
        "path": os.path.join(os.path.dirname(__file__), "..", "3. BDTK.dxf"),
        "category": "Large (10.5 MB, 355 layers)",
        "cm": 105.75,
        "k0": 0.9999
    }
]

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

def post_multipart(url, file_path, fields, file_field="file"):
    body, ct = make_multipart_body(file_path, fields, file_field)
    req = urllib.request.Request(url, data=body, headers={"Content-Type": ct})
    t0 = time.perf_counter()
    with urllib.request.urlopen(req, timeout=180) as resp:
        data = resp.read()
    t1 = time.perf_counter()
    return t1 - t0, len(data), json.loads(data.decode("utf-8"))

def get_backend_memory():
    try:
        cmd = 'powershell -NoProfile -Command "(Get-Process dotnet -ErrorAction SilentlyContinue | Measure-Object WorkingSet64 -Sum).Sum / 1MB"'
        out = subprocess.check_output(cmd, shell=True).decode().strip()
        return float(out) if out else 0.0
    except Exception:
        return 0.0

print("=" * 70)
print("ROADGUARD CAD & GIS STUDIO — PERFORMANCE PROFILING BASELINE")
print("=" * 70)
print(f"Backend memory before profiling: {get_backend_memory():.2f} MB")

results = []

for item in DXF_FILES:
    name = item["name"]
    path = item["path"]
    cat = item["category"]
    cm = item["cm"]
    k0 = item["k0"]
    
    if not os.path.exists(path):
        print(f"Skipping {name}: file not found.")
        continue
    
    size_mb = os.path.getsize(path) / (1024 * 1024)
    print(f"\n--- Profiling HTTP pipeline: {name} ({cat}, {size_mb:.2f} MB) ---")
    sys.stdout.flush()
    
    # Measure HTTP /api/cad/get-layers
    t_layers, sz_layers, res_layers = post_multipart(
        "http://localhost:5198/api/cad/get-layers", 
        path, 
        {},
        file_field="file"
    )
    layer_count = len(res_layers) if isinstance(res_layers, list) else 0
    print(f"  [HTTP] get-layers: {t_layers*1000:.1f} ms (Layers: {layer_count})")
    sys.stdout.flush()
    
    # Measure HTTP /api/cad/parse-road (Cold)
    t_road_cold, sz_road_cold, res_road_cold = post_multipart(
        "http://localhost:5198/api/cad/parse-road",
        path,
        {"roadWidth": 7.0, "centralMeridian": cm, "scaleFactor": k0}
    )
    detected_layer = res_road_cold.get("centerlineLayerName", "")
    total_len = res_road_cold.get("totalLengthMeters", 0.0)
    print(f"  [HTTP] parse-road (Cold): {t_road_cold*1000:.1f} ms (Layer: {detected_layer}, Length: {total_len:.2f} m)")
    sys.stdout.flush()
    
    # Measure HTTP /api/cad/parse-road (Warm run 1)
    t_road_warm1, _, _ = post_multipart(
        "http://localhost:5198/api/cad/parse-road",
        path,
        {"roadWidth": 7.0, "centralMeridian": cm, "scaleFactor": k0}
    )
    # Measure HTTP /api/cad/parse-road (Warm run 2)
    t_road_warm2, _, _ = post_multipart(
        "http://localhost:5198/api/cad/parse-road",
        path,
        {"roadWidth": 7.0, "centralMeridian": cm, "scaleFactor": k0}
    )
    t_road_warm = (t_road_warm1 + t_road_warm2) / 2
    print(f"  [HTTP] parse-road (Warm avg): {t_road_warm*1000:.1f} ms")
    sys.stdout.flush()
    
    # Measure HTTP /api/cad/parse-dxf (Cold)
    t_dxf_cold, sz_dxf_cold, res_dxf_cold = post_multipart(
        "http://localhost:5198/api/cad/parse-dxf",
        path,
        {"targetSrid": 4326, "centralMeridian": cm, "scaleFactor": k0, "centerlineLayerName": detected_layer}
    )
    feat_count = len(res_dxf_cold.get("featureCollection", {}).get("features", []))
    print(f"  [HTTP] parse-dxf (Cold): {t_dxf_cold*1000:.1f} ms (Features: {feat_count})")
    sys.stdout.flush()
    
    # Measure HTTP /api/cad/parse-dxf (Warm)
    t_dxf_warm, _, _ = post_multipart(
        "http://localhost:5198/api/cad/parse-dxf",
        path,
        {"targetSrid": 4326, "centralMeridian": cm, "scaleFactor": k0, "centerlineLayerName": detected_layer}
    )
    print(f"  [HTTP] parse-dxf (Warm): {t_dxf_warm*1000:.1f} ms")
    sys.stdout.flush()
    
    # Measure Concurrent (/api/cad/parse-road + /api/cad/parse-dxf) as done in Sidebar.jsx
    import concurrent.futures
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as executor:
        t_conc_start = time.perf_counter()
        f1 = executor.submit(post_multipart, "http://localhost:5198/api/cad/parse-road", path, {"roadWidth": 7.0, "centralMeridian": cm, "scaleFactor": k0})
        f2 = executor.submit(post_multipart, "http://localhost:5198/api/cad/parse-dxf", path, {"targetSrid": 4326, "centralMeridian": cm, "scaleFactor": k0, "centerlineLayerName": detected_layer})
        res1 = f1.result()
        res2 = f2.result()
        t_conc_total = time.perf_counter() - t_conc_start
    print(f"  [HTTP] Concurrent (parse-road + parse-dxf): {t_conc_total*1000:.1f} ms (f1: {res1[0]*1000:.1f}ms, f2: {res2[0]*1000:.1f}ms)")
    sys.stdout.flush()
    
    # Measure Centerline Layer switch time
    t_switch, _, _ = post_multipart(
        "http://localhost:5198/api/cad/parse-road",
        path,
        {"roadWidth": 7.0, "centralMeridian": cm, "scaleFactor": k0, "centerlineLayerName": detected_layer}
    )
    print(f"  [HTTP] Layer Switch (re-submit with layer): {t_switch*1000:.1f} ms")
    sys.stdout.flush()
    
    # Measure Road Width adjust time
    t_width, _, _ = post_multipart(
        "http://localhost:5198/api/cad/parse-road",
        path,
        {"roadWidth": 8.0, "centralMeridian": cm, "scaleFactor": k0, "centerlineLayerName": detected_layer}
    )
    print(f"  [HTTP] Road Width Adjust (7m -> 8m): {t_width*1000:.1f} ms")
    sys.stdout.flush()
    
    print(f"  Backend memory: {get_backend_memory():.2f} MB")
    sys.stdout.flush()
    
    results.append({
        "name": name,
        "category": cat,
        "size_mb": round(size_mb, 2),
        "layer_count": layer_count,
        "t_layers_ms": round(t_layers * 1000, 1),
        "t_road_cold_ms": round(t_road_cold * 1000, 1),
        "t_road_warm_ms": round(t_road_warm * 1000, 1),
        "t_dxf_cold_ms": round(t_dxf_cold * 1000, 1),
        "t_dxf_warm_ms": round(t_dxf_warm * 1000, 1),
        "t_conc_ms": round(t_conc_total * 1000, 1),
        "t_switch_ms": round(t_switch * 1000, 1),
        "t_width_ms": round(t_width * 1000, 1),
        "total_length": total_len,
        "feature_count": feat_count
    })

with open("benchmark_baseline.json", "w", encoding="utf-8") as f:
    json.dump(results, f, indent=2)

print("\n" + "=" * 70)
print("BASELINE PROFILING SUMMARY SAVED TO benchmark_baseline.json")
print("=" * 70)
