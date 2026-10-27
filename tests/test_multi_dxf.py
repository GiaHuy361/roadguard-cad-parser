import os
import sys

# Portable relative path to Services/PythonBridge
script_dir = os.path.dirname(os.path.abspath(__file__))
repo_root = os.path.dirname(script_dir)
bridge_dir = os.path.join(repo_root, "Services", "PythonBridge")
if bridge_dir not in sys.path:
    sys.path.insert(0, bridge_dir)

from cad_bridge import parse_road

# Priority list of test DXFs: Repo-relative first, then local fallback if available
downloads_dir = os.path.join(os.path.expanduser("~"), "Downloads")
bdtk_repo_path = os.path.join(repo_root, "3. BDTK.dxf")

candidate_files = [
    (bdtk_repo_path, 105.75, "3. BDTK.dxf", True),
    (os.path.join(downloads_dir, "3. BINH DO CHIEU SANG.dxf"), 105.75, "3. BINH DO CHIEU SANG.dxf", False),
    (os.path.join(downloads_dir, "binh do dieu chinh lan 1.dxf"), 107.75, "binh do dieu chinh lan 1.dxf", False),
    (os.path.join(downloads_dir, "cadmapper-bao-loc-lam-ong-vn.dxf"), 108.25, "cadmapper-bao-loc-lam-ong-vn.dxf", False),
    (os.path.join(downloads_dir, "ROAD_CENTERLINE.dxf"), 105.75, "ROAD_CENTERLINE.dxf", False),
]

executed_count = 0
for path, cm, name, is_required in candidate_files:
    if not os.path.exists(path):
        if is_required:
            print(f"ERROR: Required fixture missing: {path}")
            sys.exit(1)
        else:
            print(f"SKIP (optional fixture not present): {name}")
            continue

    executed_count += 1
    res = parse_road(path, central_meridian=cm)
    print(f"=== {name} ===")
    print(f"  success: {res.get('success')}")
    print(f"  crsState: {res.get('crsState')}")
    print(f"  isGeoreferenced: {res.get('isGeoreferenced')}")
    print(f"  totalLengthMeters: {res.get('totalLengthMeters')}")
    print(f"  centerline GPS count: {len(res.get('centerline', []))}")
    print(f"  centerlineCad count: {len(res.get('centerlineCad', []))}")
    print(f"  candidateLayers: {len(res.get('candidateLayers', []))}")
    if res.get("candidateLayers"):
        top3 = [c["name"] + f" ({c['totalLengthMeters']}m, score {c['score']})" for c in res["candidateLayers"][:3]]
        print(f"    Top candidates: {top3}")
    print(f"  bbox: {res.get('bbox')}")
    if res.get("centerline") and len(res.get("centerline")) > 0:
        print(f"  sample GPS: {res.get('centerline')[0]}")
    if res.get("notice"):
        print(f"  notice: {res.get('notice')}")

print(f"\nCompleted multi-DXF matrix test for {executed_count} file(s).")
