import os
import sys
import time
import json
import urllib.request

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

with open("benchmark_baseline.json", "r", encoding="utf-8") as f:
    baseline_data = {item["name"]: item for item in json.load(f)}

print("=" * 80)
print("ROADGUARD CAD & GIS STUDIO — PERFORMANCE BENCHMARK: BEFORE VS AFTER")
print("=" * 80)

comparison_results = []

for item in DXF_FILES:
    name = item["name"]
    path = item["path"]
    cat = item["category"]
    cm = item["cm"]
    k0 = item["k0"]
    
    if not os.path.exists(path):
        continue
    
    base = baseline_data.get(name, {})
    
    # Run warm tests to capture cached/optimized performance
    t_layers, _, res_layers = post_multipart("http://localhost:5198/api/cad/get-layers", path, {}, file_field="file")
    
    t_road_warm, _, res_road = post_multipart("http://localhost:5198/api/cad/parse-road", path, {"roadWidth": 7.0, "centralMeridian": cm, "scaleFactor": k0})
    detected_layer = res_road.get("centerlineLayerName", "")
    
    t_dxf_warm, _, res_dxf = post_multipart("http://localhost:5198/api/cad/parse-dxf", path, {"targetSrid": 4326, "centralMeridian": cm, "scaleFactor": k0, "centerlineLayerName": detected_layer})
    
    import concurrent.futures
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as executor:
        t_conc_start = time.perf_counter()
        f1 = executor.submit(post_multipart, "http://localhost:5198/api/cad/parse-road", path, {"roadWidth": 7.0, "centralMeridian": cm, "scaleFactor": k0})
        f2 = executor.submit(post_multipart, "http://localhost:5198/api/cad/parse-dxf", path, {"targetSrid": 4326, "centralMeridian": cm, "scaleFactor": k0, "centerlineLayerName": detected_layer})
        r1 = f1.result()
        r2 = f2.result()
        t_conc_total = time.perf_counter() - t_conc_start
        
    t_switch, _, _ = post_multipart("http://localhost:5198/api/cad/parse-road", path, {"roadWidth": 7.0, "centralMeridian": cm, "scaleFactor": k0, "centerlineLayerName": detected_layer})
    
    t_width, _, _ = post_multipart("http://localhost:5198/api/cad/parse-road", path, {"roadWidth": 8.0, "centralMeridian": cm, "scaleFactor": k0, "centerlineLayerName": detected_layer})
    
    row = {
        "name": name,
        "category": cat,
        "size_mb": base.get("size_mb", round(os.path.getsize(path)/(1024*1024), 2)),
        "baseline": {
            "get_layers_ms": base.get("t_layers_ms", 0),
            "parse_road_warm_ms": base.get("t_road_warm_ms", 0),
            "parse_dxf_warm_ms": base.get("t_dxf_warm_ms", 0),
            "concurrent_ms": base.get("t_conc_ms", 0),
            "layer_switch_ms": base.get("t_switch_ms", 0),
            "width_adjust_ms": base.get("t_width_ms", 0)
        },
        "optimized": {
            "get_layers_ms": round(t_layers * 1000, 1),
            "parse_road_warm_ms": round(t_road_warm * 1000, 1),
            "parse_dxf_warm_ms": round(t_dxf_warm * 1000, 1),
            "concurrent_ms": round(t_conc_total * 1000, 1),
            "layer_switch_ms": round(t_switch * 1000, 1),
            "width_adjust_ms": round(t_width * 1000, 1)
        },
        "speedup": {
            "parse_road": round(base.get("t_road_warm_ms", 1) / max(0.1, t_road_warm * 1000), 1),
            "parse_dxf": round(base.get("t_dxf_warm_ms", 1) / max(0.1, t_dxf_warm * 1000), 1),
            "concurrent": round(base.get("t_conc_ms", 1) / max(0.1, t_conc_total * 1000), 1)
        }
    }
    comparison_results.append(row)
    print(f"\n[{name}] ({cat}):")
    print(f"  • parse-road (Warm): {row['baseline']['parse_road_warm_ms']} ms  ->  {row['optimized']['parse_road_warm_ms']} ms  ({row['speedup']['parse_road']}x faster)")
    print(f"  • parse-dxf  (Warm): {row['baseline']['parse_dxf_warm_ms']} ms  ->  {row['optimized']['parse_dxf_warm_ms']} ms  ({row['speedup']['parse_dxf']}x faster)")
    print(f"  • Concurrent Call:   {row['baseline']['concurrent_ms']} ms  ->  {row['optimized']['concurrent_ms']} ms  ({row['speedup']['concurrent']}x faster)")

with open("benchmark_comparison.json", "w", encoding="utf-8") as f:
    json.dump(comparison_results, f, indent=2)

print("\n" + "=" * 80)
print("BENCHMARK COMPARISON SAVED TO benchmark_comparison.json")
print("=" * 80)
