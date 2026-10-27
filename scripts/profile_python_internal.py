import os
import sys
import time
import json
import tracemalloc

# Add PythonBridge to sys.path
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "Services", "PythonBridge"))

DXF_FILES = [
    ("sample_road.dxf", os.path.join(os.path.dirname(__file__), "..", "sample_road.dxf"), "Extra Small (18 KB)"),
    ("binh do dieu chinh lan 1.dxf", r"C:\Users\dell\Downloads\binh do dieu chinh lan 1.dxf", "Small (406 KB)"),
    ("ROAD_CENTERLINE.dxf", os.path.join(os.path.dirname(__file__), "..", "ROAD_CENTERLINE.dxf"), "Medium (6.8 MB)"),
    ("3. BDTK.dxf", os.path.join(os.path.dirname(__file__), "..", "3. BDTK.dxf"), "Large (10.5 MB, 355 layers)"),
    ("BINH DO THIET KE PK.dxf", r"C:\Users\dell\Downloads\BINH DO THIET KE PK.dxf", "Extra Large (19.8 MB)")
]

print("=" * 75)
print("PYTHON BRIDGE INTERNAL DETAILED PROFILING")
print("=" * 75)

# 1. Measure import overhead
t0 = time.perf_counter()
import ezdxf
t_ezdxf = time.perf_counter() - t0

t0 = time.perf_counter()
import pyproj
t_pyproj = time.perf_counter() - t0

t0 = time.perf_counter()
import shapely
import shapely.geometry
import shapely.ops
t_shapely = time.perf_counter() - t0

print(f"Library Import Overhead:")
print(f"  - ezdxf:   {t_ezdxf*1000:.1f} ms")
print(f"  - pyproj:  {t_pyproj*1000:.1f} ms")
print(f"  - shapely: {t_shapely*1000:.1f} ms")
print(f"  Total Import Overhead per Process: {(t_ezdxf + t_pyproj + t_shapely)*1000:.1f} ms")
print("-" * 75)

import cad_bridge

breakdown_results = []

for name, path, cat in DXF_FILES:
    if not os.path.exists(path):
        continue
    
    size_mb = os.path.getsize(path) / (1024 * 1024)
    print(f"\n--- Profiling internals: {name} ({cat}, {size_mb:.2f} MB) ---")
    
    tracemalloc.start()
    
    # 1. ezdxf.readfile
    t0 = time.perf_counter()
    doc = ezdxf.readfile(path)
    t_readfile = time.perf_counter() - t0
    
    # 2. Iterate modelspace entities
    t0 = time.perf_counter()
    msp = doc.modelspace()
    all_ents = list(msp)
    t_iter_msp = time.perf_counter() - t0
    ent_count = len(all_ents)
    
    # 3. Layer extraction
    t0 = time.perf_counter()
    layers = cad_bridge.get_layers(path)
    t_layers = time.perf_counter() - t0
    
    # 4. Candidate layer detection & scoring
    t0 = time.perf_counter()
    best_layer, layer_raw, candidate_layers = cad_bridge._detect_centerline_layer(msp, doc)
    t_detect = time.perf_counter() - t0
    
    # 5. Full parse_road execution
    t0 = time.perf_counter()
    road_res = cad_bridge.parse_road(path, road_width=7.0, central_meridian=105.75, scale_factor=0.9999)
    t_parse_road = time.perf_counter() - t0
    
    # 6. Full extract_geometries execution
    t0 = time.perf_counter()
    dxf_res = cad_bridge.extract_geometries(path, target_layer=best_layer, central_meridian=105.75, scale_factor=0.9999)
    t_extract_geom = time.perf_counter() - t0
    
    # 7. JSON serialization
    t0 = time.perf_counter()
    json_bytes = json.dumps(road_res).encode("utf-8")
    t_json = time.perf_counter() - t0
    
    current_mem, peak_mem = tracemalloc.get_traced_memory()
    tracemalloc.stop()
    
    peak_mb = peak_mem / (1024 * 1024)
    
    print(f"  Entities count:             {ent_count}")
    print(f"  ezdxf.readfile():          {t_readfile*1000:.1f} ms")
    print(f"  Modelspace entity listing: {t_iter_msp*1000:.1f} ms")
    print(f"  Layer scanning:            {t_layers*1000:.1f} ms")
    print(f"  Layer detection & scoring: {t_detect*1000:.1f} ms (Detected: {best_layer})")
    print(f"  Full parse_road():         {t_parse_road*1000:.1f} ms")
    print(f"  Full extract_geometries(): {t_extract_geom*1000:.1f} ms")
    print(f"  JSON serialization:        {t_json*1000:.1f} ms (Payload: {len(json_bytes)/1024:.1f} KB)")
    print(f"  Peak memory during parse:  {peak_mb:.1f} MB")
    
    breakdown_results.append({
        "name": name,
        "size_mb": round(size_mb, 2),
        "entities": ent_count,
        "t_readfile_ms": round(t_readfile * 1000, 1),
        "t_detect_ms": round(t_detect * 1000, 1),
        "t_parse_road_ms": round(t_parse_road * 1000, 1),
        "t_extract_geom_ms": round(t_extract_geom * 1000, 1),
        "peak_mem_mb": round(peak_mb, 1)
    })

with open("python_internal_breakdown.json", "w", encoding="utf-8") as f:
    json.dump({
        "imports": {
            "ezdxf_ms": round(t_ezdxf * 1000, 1),
            "pyproj_ms": round(t_pyproj * 1000, 1),
            "shapely_ms": round(t_shapely * 1000, 1),
            "total_import_ms": round((t_ezdxf + t_pyproj + t_shapely) * 1000, 1)
        },
        "files": breakdown_results
    }, f, indent=2)

print("\nSaved breakdown to python_internal_breakdown.json")
