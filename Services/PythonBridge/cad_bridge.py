import sys
import os
import re
import json
import math
import argparse
import ezdxf
from ezdxf import path

CENTERLINE_KEYWORDS = [
    "TIM_THIET_KE",
    "TKDTIMTUYENTKT1",
    "TKDTIMTUYENTKT2",
    "TIMDUONG",
    "TIM_DUONG",
    "ROAD_CENTERLINE",
    "CENTERLINE",
    "TIM",
    "CENTER",
    "TUYEN",
    "ROAD"
]

def utm48n_to_wgs84(easting, northing):
    a = 6378137.0
    f = 1 / 298.257223563
    k0 = 0.9996
    e = math.sqrt(2 * f - f * f)
    e1 = (1 - math.sqrt(1 - e * e)) / (1 + math.sqrt(1 - e * e))
    x = easting - 500000.0
    y = northing
    m = y / k0
    mu = m / (a * (1 - e**2 / 4 - 3 * e**4 / 64 - 5 * e**6 / 256))
    phi1 = mu + (3 * e1 / 2 - 27 * e1**3 / 32) * math.sin(2 * mu) + (21 * e1**2 / 16 - 55 * e1**4 / 32) * math.sin(4 * mu) + (151 * e1**3 / 96) * math.sin(6 * mu)
    n1 = a / math.sqrt(1 - e**2 * math.sin(phi1)**2)
    t1 = math.tan(phi1)**2
    c1 = (e**2 / (1 - e**2)) * math.cos(phi1)**2
    r1 = a * (1 - e**2) / (1 - e**2 * math.sin(phi1)**2)**1.5
    d = x / (n1 * k0)
    lat = phi1 - (n1 * math.tan(phi1) / r1) * (d**2 / 2 - (5 + 3 * t1 + 10 * c1 - 4 * c1**2 - 9 * (e**2 / (1 - e**2))) * d**4 / 24 + (61 + 90 * t1 + 298 * c1 + 45 * t1**2 - 252 * (e**2 / (1 - e**2)) - 3 * c1**2) * d**6 / 720)
    lon0 = math.radians(105.0)
    lon = lon0 + (d - (1 + 2 * t1 + c1) * d**3 / 6 + (5 - 2 * c1 + 28 * t1 - 3 * c1**2 + 8 * (e**2 / (1 - e**2)) + 24 * t1**2) * d**5 / 120) / math.cos(phi1)
    return math.degrees(lon), math.degrees(lat)

def get_layers(dxf_path):
    doc = ezdxf.readfile(dxf_path)
    layers = set()
    for l in doc.layers:
        if l.dxf.name:
            layers.add(l.dxf.name)
    msp = doc.modelspace()
    for e in msp:
        if e.dxf.layer:
            layers.add(e.dxf.layer)
    return sorted(list(layers), key=lambda x: x.lower())

def extract_geometries(dxf_path, target_layer=None, srid=4326):
    doc = ezdxf.readfile(dxf_path)
    msp = doc.modelspace()

    linear_types = {"LINE", "LWPOLYLINE", "POLYLINE", "ARC", "SPLINE", "CIRCLE"}
    layer_entities = {}
    all_layers = set()

    for l in doc.layers:
        if l.dxf.name:
            all_layers.add(l.dxf.name)

    for e in msp:
        if e.dxf.layer:
            all_layers.add(e.dxf.layer)
            if e.dxftype() in linear_types:
                layer_entities.setdefault(e.dxf.layer, []).append(e)

    chosen_layer = None
    if target_layer and target_layer in layer_entities:
        chosen_layer = target_layer
    elif target_layer:
        for l in layer_entities:
            if l.lower() == target_layer.lower():
                chosen_layer = l
                break

    if not chosen_layer:
        # Heuristic: Calculate total geometric length for each layer to pick the true continuous road alignment
        layer_lengths = {}
        for l, ents in layer_entities.items():
            tot_len = 0.0
            for e in ents:
                try:
                    p = path.make_path(e)
                    pts = list(p.flattening(distance=1.0))
                    for i in range(1, len(pts)):
                        dx = pts[i].x - pts[i-1].x
                        dy = pts[i].y - pts[i-1].y
                        tot_len += math.sqrt(dx*dx + dy*dy)
                except Exception:
                    pass
            layer_lengths[l] = tot_len

        # Candidate layers matching civil engineering road keywords
        candidates = []
        for l, l_len in layer_lengths.items():
            # Exclude auxiliary annotation layers like text/tick marks
            if any(ignore in l.upper() for ignore in ['SUONTUYEN', 'DAUTIM', 'TEXT', 'GHICHU', 'COC', 'KHUNG']):
                continue
            for kw in CENTERLINE_KEYWORDS:
                if re.search(r'\b' + re.escape(kw) + r'\b', l, re.I) or kw.upper() in l.upper():
                    candidates.append((l, l_len, len(layer_entities[l])))
                    break

        if candidates:
            # Sort by total road length descending (prefer long continuous alignment over short 7m stubs)
            candidates.sort(key=lambda c: (c[1], c[2]), reverse=True)
            chosen_layer = candidates[0][0]

    if not chosen_layer and layer_entities:
        chosen_layer = max(layer_entities.keys(), key=lambda l: len(layer_entities[l]))

    if not chosen_layer:
        return {
            "success": False,
            "error": "NoLinearEntities",
            "detail": "No valid lines or polylines found in any layer of the CAD drawing.",
            "layers": sorted(list(all_layers), key=lambda x: x.lower())
        }

    entities_to_process = layer_entities.get(chosen_layer, [])
    features_data = []

    for e in entities_to_process:
        try:
            dxftype = e.dxftype()
            coords = []
            elevation = 0.0

            if hasattr(e.dxf, "elevation"):
                elevation = float(e.dxf.elevation or 0.0)

            if dxftype == "LINE":
                start = e.dxf.start
                end = e.dxf.end
                elevation = float(start.z if abs(start.z) > 0.001 else (end.z if abs(end.z) > 0.001 else elevation))
                coords = [(start.x, start.y, start.z), (end.x, end.y, end.z)]
            else:
                p = path.make_path(e)
                vertices = list(p.flattening(distance=0.5))
                if len(vertices) >= 2:
                    coords = [(v.x, v.y, v.z) for v in vertices]
                    for v in vertices:
                        if abs(v.z) > 0.001:
                            elevation = float(v.z)
                            break

            if len(coords) < 2:
                continue

            transformed_coords = []
            for x, y, z in coords:
                curr_elev = z if abs(z) > 0.001 else elevation
                if srid == 4326 and (abs(x) > 180.0 or abs(y) > 90.0):
                    lon, lat = utm48n_to_wgs84(x, y)
                    transformed_coords.append([lon, lat, curr_elev])
                else:
                    transformed_coords.append([x, y, curr_elev])

            features_data.append({
                "layer": chosen_layer,
                "elevation": elevation,
                "coordinates": transformed_coords
            })
        except Exception:
            continue

    return {
        "success": True,
        "detectedLayer": chosen_layer,
        "totalEntities": len(features_data),
        "layers": sorted(list(all_layers), key=lambda x: x.lower()),
        "features": features_data
    }

def main():
    parser = argparse.ArgumentParser(description="RoadGuard CAD ezdxf Python Bridge")
    subparsers = parser.add_subparsers(dest="command")

    layers_parser = subparsers.add_parser("get-layers")
    layers_parser.add_argument("dxf_path", help="Path to DXF file")

    parse_parser = subparsers.add_parser("parse")
    parse_parser.add_argument("dxf_path", help="Path to DXF file")
    parse_parser.add_argument("--layer", default=None, help="Target layer name")
    parse_parser.add_argument("--srid", type=int, default=4326, help="Target SRID")

    args = parser.parse_args()

    if not os.path.exists(args.dxf_path):
        print(json.dumps({"success": False, "error": "FileNotFound", "detail": f"File '{args.dxf_path}' not found."}))
        sys.exit(1)

    try:
        if args.command == "get-layers":
            layers = get_layers(args.dxf_path)
            print(json.dumps({"success": True, "layers": layers}))
        elif args.command == "parse":
            result = extract_geometries(args.dxf_path, args.layer, args.srid)
            print(json.dumps(result))
        else:
            parser.print_help()
            sys.exit(1)
    except Exception as ex:
        print(json.dumps({"success": False, "error": "BridgeError", "detail": str(ex)}))
        sys.exit(1)

if __name__ == "__main__":
    main()
