#!/usr/bin/env python3
"""
RoadGuard CAD Parser - Python ezdxf Bridge v2.3
================================================
Commands:
  get-layers <dxf>                              List all DXF layers
  parse      <dxf> [--layer L] [--srid N]      Extract centerline RFC 7946 GeoJSON
  render     <dxf> [--size N] [--width W]      Render transparent concrete road PNG + WGS-84 bounds
"""

import os, sys, math, json, argparse, base64, io
if hasattr(sys.stdout, "reconfigure"):
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
import ezdxf
from ezdxf import path as dxf_path
from ezdxf.addons.drawing.properties import Filling

try:
    from pyproj import CRS, Transformer
    HAS_PYPROJ = True
except ImportError:
    HAS_PYPROJ = False

try:
    from shapely.geometry import LineString, MultiLineString, Polygon, MultiPolygon
    from shapely.ops import unary_union, linemerge
    HAS_SHAPELY = True
except ImportError:
    HAS_SHAPELY = False

# =============================================================================
# Configuration
# =============================================================================

CENTERLINE_KEYWORDS = [
    "TKDTIMTUYEN", "TIM_THIET_KE", "TIMTHIETKE", "TIMTUYEN", "TIM_TUYEN",
    "TIMDUONG", "TIM_DUONG", "CENTERLINE", "CENTER_LINE", "ROAD_AXIS", "ROAD_CENTER",
    "DUONG_TIM", "TIM", "AXIS", "ALIG", "ALIGNMENT", "ENTPLINETUYEN"
]

def score_centerline_layer(name: str) -> int:
    u = name.upper()
    if any(ex in u for ex in EXCLUDE_KEYWORDS):
        return -100
    if "TKDTIMTUYEN" in u:
        return 1000
    if "TIM_THIET_KE" in u or "TIMTHIETKE" in u:
        return 900
    if "TIMTUYEN" in u or "TIM_TUYEN" in u:
        return 800
    if "TIMDUONG" in u or "TIM_DUONG" in u:
        return 700
    if "CENTERLINE" in u or "ROAD_AXIS" in u or "ROAD_CENTER" in u:
        return 600
    if "TIM" in u and "DAUTIM" not in u and "SUON" not in u:
        return 500
    if "ENTPLINETUYEN" in u:
        return 100
    for kw in CENTERLINE_KEYWORDS:
        if kw.upper() in u:
            return 50
    return 0
EXCLUDE_KEYWORDS = [
    "TEXT", "DIM", "KICHTHUOC", "KHUNG", "TEN", "BANG",
    "GHI_CHU", "NOTE", "HATCH", "DOT", "BOU", "RANH", "GIOI"
]
LINEAR_TYPES = {"LINE", "LWPOLYLINE", "POLYLINE", "ARC", "SPLINE", "CIRCLE"}
MIN_CENTERLINE_LENGTH = 10.0
ARC_TESSELLATION_STEPS = 64

_VN2000_X_MIN =  100_000.0
_VN2000_X_MAX =  950_000.0
_VN2000_Y_MIN =  800_000.0
_VN2000_Y_MAX = 2_600_000.0

def _is_vn2000(x, y) -> bool:
    is_std = (_VN2000_X_MIN <= x <= _VN2000_X_MAX and
              _VN2000_Y_MIN <= y <= _VN2000_Y_MAX)
    is_swp = (_VN2000_Y_MIN <= x <= _VN2000_Y_MAX and
              _VN2000_X_MIN <= y <= _VN2000_X_MAX)
    return is_std or is_swp

def _is_vn2000_swapped(x, y) -> bool:
    return (_VN2000_Y_MIN <= x <= _VN2000_Y_MAX and
            _VN2000_X_MIN <= y <= _VN2000_X_MAX)

# =============================================================================
# Projection
# =============================================================================

def detect_central_meridian(x: float) -> float:
    if x < 300000:   return 102.0
    elif x < 600000: return 105.0
    elif x < 900000: return 108.0
    else:            return 111.0


def make_vn2000_transformer(cm: float):
    proj = (
        f"+proj=tmerc +lat_0=0 +lon_0={cm} +k=0.9999 "
        f"+x_0=500000 +y_0=0 +ellps=WGS84 "
        f"+towgs84=-191.90441429,-39.30318279,-111.45032835,"
        f"-0.00928836,0.01975479,-0.00427372,0.252906278 "
        f"+units=m +no_defs"
    )
    return Transformer.from_crs(CRS.from_proj4(proj), CRS.from_epsg(4326), always_xy=True)


def project_points(points_xy: list, target_srid: int = 4326) -> list:
    if not points_xy:
        return []
    x0, y0 = points_xy[0]
    if abs(x0) <= 180.0 and abs(y0) <= 90.0 and target_srid == 4326:
        return [[float(x), float(y)] for x, y in points_xy]
    if not HAS_PYPROJ:
        return [_manual_utm(x, y) for x, y in points_xy]

    cm = detect_central_meridian(x0)
    proj_in = (
        f"+proj=tmerc +lat_0=0 +lon_0={cm} +k=0.9999 "
        f"+x_0=500000 +y_0=0 +ellps=WGS84 "
        f"+towgs84=-191.90441429,-39.30318279,-111.45032835,"
        f"-0.00928836,0.01975479,-0.00427372,0.252906278 "
        f"+units=m +no_defs"
    )
    crs_in = CRS.from_proj4(proj_in)
    crs_out = CRS.from_epsg(target_srid)
    tf = Transformer.from_crs(crs_in, crs_out, always_xy=True)
    lons, lats = tf.transform([p[0] for p in points_xy], [p[1] for p in points_xy])
    return [[float(lon), float(lat)] for lon, lat in zip(lons, lats)]


def project_to_wgs84(points_xy: list) -> list:
    return project_points(points_xy, 4326)


def _manual_utm(easting, northing):
    a = 6378137.0; f = 1/298.257223563; k0 = 0.9996
    e2 = 2*f - f*f; e = math.sqrt(e2)
    e1 = (1 - math.sqrt(1 - e2)) / (1 + math.sqrt(1 - e2))
    x = easting - 500000.0; y = northing; m = y / k0
    mu = m / (a * (1 - e2/4 - 3*e2**2/64 - 5*e2**3/256))
    phi1 = (mu + (3*e1/2 - 27*e1**3/32)*math.sin(2*mu)
          + (21*e1**2/16 - 55*e1**4/32)*math.sin(4*mu)
          + (151*e1**3/96)*math.sin(6*mu))
    sp = math.sin(phi1); cp = math.cos(phi1); tp = math.tan(phi1)
    n1 = a / math.sqrt(1 - e2*sp**2); t1 = tp**2; c1 = (e2/(1 - e2))*cp**2
    r1 = a*(1 - e2) / (1 - e2*sp**2)**1.5; ep2 = e2 / (1 - e2); d = x / (n1*k0)
    lat = phi1 - (n1*tp/r1)*(d**2/2 - (5 + 3*t1 + 10*c1 - 4*c1**2 - 9*ep2)*d**4/24
        + (61 + 90*t1 + 298*c1 + 45*t1**2 - 252*ep2 - 3*c1**2)*d**6/720)
    lon0 = math.radians(105.0)
    lon = lon0 + (d - (1 + 2*t1 + c1)*d**3/6
        + (5 - 2*c1 + 28*t1 - 3*c1**2 + 8*ep2 + 24*t1**2)*d**5/120) / cp
    return [math.degrees(lon), math.degrees(lat)]

# =============================================================================
# Geometry Tessellation Helpers (Bulge, Arc, Circle)
# =============================================================================

def _tessellate_bulge(x0: float, y0: float, x1: float, y1: float, bulge: float) -> list:
    """
    Interpolates an AutoCAD bulge arc into discrete coordinates.
    bulge = tan(included_angle / 4)
    > 0: CCW arc (center on left of chord vector)
    < 0: CW arc (center on right of chord vector)
    Returns: list of (x, y) tuples from (x0, y0) to (x1, y1) inclusive.
    """
    chord = math.hypot(x1 - x0, y1 - y0)
    if chord < 1e-9:
        return [(x0, y0)]

    abs_b = abs(bulge)
    R = (chord / 2.0) * (1.0 + abs_b**2) / (2.0 * abs_b)
    h = (chord / 2.0) * (1.0 - abs_b**2) / (2.0 * abs_b)

    dx = x1 - x0
    dy = y1 - y0
    perp_x = -dy / chord
    perp_y = dx / chord
    if bulge < 0:
        perp_x = -perp_x
        perp_y = -perp_y

    cx = (x0 + x1) / 2.0 + perp_x * h
    cy = (y0 + y1) / 2.0 + perp_y * h

    a0 = math.atan2(y0 - cy, x0 - cx)
    a1 = math.atan2(y1 - cy, x1 - cx)

    if bulge > 0:
        sweep = a1 - a0
        if sweep <= 0:
            sweep += 2.0 * math.pi
    else:
        sweep = a1 - a0
        if sweep >= 0:
            sweep -= 2.0 * math.pi

    # Dynamic step count: 10-36 points depending on curvature
    n = max(10, min(36, int(math.ceil(20.0 * abs(sweep) / math.pi))))

    return [
        (cx + R * math.cos(a0 + sweep * i / n),
         cy + R * math.sin(a0 + sweep * i / n))
        for i in range(n + 1)
    ]


def _tessellate_arc(cx: float, cy: float, r: float, sd: float, ed: float, steps: int = ARC_TESSELLATION_STEPS) -> list:
    """
    Interpolates an AutoCAD ARC entity (degrees, CCW) into discrete coordinates.
    Returns: list of (x, y) tuples from start to end of arc.
    """
    s = math.radians(sd)
    e = math.radians(ed)
    sweep = e - s
    if sweep <= 0:
        sweep += 2.0 * math.pi
    n = max(10, min(64, int(math.ceil(steps * sweep / (2.0 * math.pi)))))
    return [
        (cx + r * math.cos(s + sweep * i / n),
         cy + r * math.sin(s + sweep * i / n))
        for i in range(n + 1)
    ]


def entity_to_wcs_points(entity, transform=None):
    """
    Extracts ordered (x, y) coordinate chain in WCS for any linear entity.
    Tessellates LWPOLYLINE bulges, POLYLINE bulges, ARCs, CIRCLEs, and SPLINEs.
    Applies INSERT transformation matrix if present.
    """
    t = entity.dxftype()
    pts = []
    try:
        if t == "LINE":
            pts = [(entity.dxf.start.x, entity.dxf.start.y),
                   (entity.dxf.end.x, entity.dxf.end.y)]
        elif t == "LWPOLYLINE":
            raw = list(entity.get_points("xyb"))
            if not raw:
                raw = [(x, y, 0.0) for x, y in entity.get_points("xy")]
            if len(raw) < 2:
                pts = [(p[0], p[1]) for p in raw]
            else:
                for i in range(len(raw) - 1):
                    x0s, y0s, bl = raw[i][0], raw[i][1], raw[i][2]
                    x1s, y1s = raw[i + 1][0], raw[i + 1][1]
                    if abs(bl) < 1e-9:
                        pts.append((x0s, y0s))
                    else:
                        ap = _tessellate_bulge(x0s, y0s, x1s, y1s, bl)
                        pts.extend(ap[:-1])
                pts.append((raw[-1][0], raw[-1][1]))

                if entity.is_closed:
                    last_bl = raw[-1][2]
                    x0s, y0s = raw[-1][0], raw[-1][1]
                    x1s, y1s = raw[0][0], raw[0][1]
                    if abs(last_bl) > 1e-9:
                        ap = _tessellate_bulge(x0s, y0s, x1s, y1s, last_bl)
                        pts.extend(ap[1:])
                    else:
                        if pts and (pts[0][0] != pts[-1][0] or pts[0][1] != pts[-1][1]):
                            pts.append(pts[0])
        elif t == "POLYLINE":
            raw = []
            for v in entity.vertices:
                loc = v.dxf.location
                b = v.dxf.get("bulge", 0.0) if v.dxf.hasattr("bulge") else 0.0
                raw.append((loc.x, loc.y, b))
            if len(raw) < 2:
                pts = [(p[0], p[1]) for p in raw]
            else:
                for i in range(len(raw) - 1):
                    x0s, y0s, bl = raw[i][0], raw[i][1], raw[i][2]
                    x1s, y1s = raw[i + 1][0], raw[i + 1][1]
                    if abs(bl) < 1e-9:
                        pts.append((x0s, y0s))
                    else:
                        ap = _tessellate_bulge(x0s, y0s, x1s, y1s, bl)
                        pts.extend(ap[:-1])
                pts.append((raw[-1][0], raw[-1][1]))

                if entity.is_closed:
                    last_bl = raw[-1][2]
                    x0s, y0s = raw[-1][0], raw[-1][1]
                    x1s, y1s = raw[0][0], raw[0][1]
                    if abs(last_bl) > 1e-9:
                        ap = _tessellate_bulge(x0s, y0s, x1s, y1s, last_bl)
                        pts.extend(ap[1:])
                    else:
                        if pts and (pts[0][0] != pts[-1][0] or pts[0][1] != pts[-1][1]):
                            pts.append(pts[0])
        elif t == "ARC":
            pts = _tessellate_arc(entity.dxf.center.x, entity.dxf.center.y,
                                  entity.dxf.radius, entity.dxf.start_angle, entity.dxf.end_angle)
        elif t == "CIRCLE":
            pts = _tessellate_arc(entity.dxf.center.x, entity.dxf.center.y,
                                  entity.dxf.radius, 0.0, 360.0)
        elif t == "SPLINE":
            p = dxf_path.make_path(entity)
            v = list(p.flattening(distance=0.5))
            pts = [(vi.x, vi.y) for vi in v]
    except Exception:
        return []

    if len(pts) < 2:
        return []

    if transform is not None:
        return [(transform.transform((x, y, 0))[0], transform.transform((x, y, 0))[1])
                for x, y in pts]
    return pts


def walk_entities(entities, doc, transform=None, depth=0):
    if depth > 8:
        return
    for entity in entities:
        et = entity.dxftype()
        if et == "INSERT":
            try:
                im = entity.matrix44()
                combined = (transform @ im) if transform is not None else im
                bn = entity.dxf.name
                if bn in doc.blocks:
                    yield from walk_entities(list(doc.blocks[bn]), doc, combined, depth + 1)
            except Exception:
                continue
        elif et in LINEAR_TYPES or et == "HATCH":
            yield (entity, transform)

# =============================================================================
# Layer discovery
# =============================================================================

def get_layers(dxf_path_str):
    doc = ezdxf.readfile(dxf_path_str)
    layers = set(l.dxf.name for l in doc.layers if l.dxf.name)
    for e in doc.modelspace():
        lyr = e.dxf.get("layer", "")
        if lyr:
            layers.add(lyr)
    return sorted(layers, key=lambda x: x.lower())

# =============================================================================
# Detect best centerline layer
# =============================================================================

def _detect_centerline_layer(msp, doc):
    layer_raw = {}
    for entity, xform in walk_entities(msp, doc):
        if entity.dxftype() not in LINEAR_TYPES:
            continue
        lyr = entity.dxf.get("layer", "0")
        pts = entity_to_wcs_points(entity, xform)
        if pts and len(pts) >= 2:
            layer_raw.setdefault(lyr, []).append(pts)
    
    scored_layers = {}
    for l, segs in layer_raw.items():
        sc = score_centerline_layer(l)
        if sc <= 0:
            continue
        total = sum(math.hypot(seg[i][0] - seg[i - 1][0], seg[i][1] - seg[i - 1][1])
                    for seg in segs for i in range(1, len(seg)))
        if total >= MIN_CENTERLINE_LENGTH:
            scored_layers[l] = (sc, total)
            
    if scored_layers:
        best_layer = max(scored_layers, key=lambda l: (scored_layers[l][0], scored_layers[l][1]))
        return best_layer, layer_raw
    if layer_raw:
        return max(layer_raw, key=lambda l: len(layer_raw[l])), layer_raw
    return None, layer_raw

# =============================================================================
# Extract geometry (RFC 7946 GeoJSON FeatureCollection output)
# =============================================================================

def extract_geometries(dxf_path_str, target_layer=None, srid=4326):
    doc = ezdxf.readfile(dxf_path_str)
    msp = doc.modelspace()
    all_layers = set(l.dxf.name for l in doc.layers if l.dxf.name)

    chosen_layer = None
    layer_raw = {}
    for entity, xform in walk_entities(msp, doc):
        if entity.dxftype() not in LINEAR_TYPES:
            continue
        lyr = entity.dxf.get("layer", "0")
        all_layers.add(lyr)
        pts = entity_to_wcs_points(entity, xform)
        if pts and len(pts) >= 2:
            elev = 0.0
            try:
                elev = float(entity.dxf.get("elevation", 0.0))
            except Exception:
                pass
            layer_raw.setdefault(lyr, []).append({
                "points": pts,
                "elevation": elev,
                "type": entity.dxftype()
            })

    if target_layer:
        if target_layer in layer_raw:
            chosen_layer = target_layer
        else:
            for l in layer_raw:
                if l.lower() == target_layer.lower():
                    chosen_layer = l
                    break
        if not chosen_layer:
            auto, _ = _detect_centerline_layer(msp, doc)
            chosen_layer = auto
    else:
        auto, _ = _detect_centerline_layer(msp, doc)
        chosen_layer = auto

    if not chosen_layer:
        return {
            "type": "FeatureCollection",
            "success": False,
            "error": "NoLinearEntities",
            "detail": "No valid geometry found.",
            "layers": sorted(all_layers, key=str.lower),
            "features": []
        }

    features_out = []
    seg_counter = 1

    for item in layer_raw.get(chosen_layer, []):
        pts = item["points"]
        if len(pts) < 2:
            continue

        projected = project_to_wgs84(pts) if srid == 4326 else project_points(pts, srid)
        if len(projected) < 2:
            continue

        seg_name = f"SEG-{seg_counter:02d}"
        feat = {
            "type": "Feature",
            "properties": {
                "layer": chosen_layer,
                "segment": seg_name,
                "elevation": item["elevation"]
            },
            "geometry": {
                "type": "LineString",
                "coordinates": projected
            },
            "layer": chosen_layer,
            "segment": seg_name,
            "elevation": item["elevation"],
            "coordinates": projected
        }
        features_out.append(feat)
        seg_counter += 1

    return {
        "type": "FeatureCollection",
        "success": True,
        "detectedLayer": chosen_layer,
        "totalEntities": len(features_out),
        "layers": sorted(all_layers, key=str.lower),
        "features": features_out
    }

# =============================================================================
# Road Surface Ribbon Generator
# =============================================================================

def generate_road_ribbon(points: list, road_width: float) -> list:
    """
    Computes a buffered 2D polygon strip (ribbon) along an ordered chain of points.
    """
    if len(points) < 2 or road_width <= 0:
        return []
    half_w = road_width / 2.0
    left_side = []
    right_side = []
    n = len(points)
    normals = []
    for i in range(n - 1):
        dx = points[i + 1][0] - points[i][0]
        dy = points[i + 1][1] - points[i][1]
        l = math.hypot(dx, dy)
        if l < 1e-9:
            normals.append((0.0, 0.0))
        else:
            normals.append((-dy / l, dx / l))
    normals.append(normals[-1])

    for i in range(n):
        if i == 0:
            nx, ny = normals[0]
        elif i == n - 1:
            nx, ny = normals[-1]
        else:
            nx = (normals[i - 1][0] + normals[i][0]) / 2.0
            ny = (normals[i - 1][1] + normals[i][1]) / 2.0
            norm_len = math.hypot(nx, ny)
            if norm_len > 1e-6:
                nx /= norm_len
                ny /= norm_len
        left_side.append((points[i][0] + nx * half_w, points[i][1] + ny * half_w))
        right_side.append((points[i][0] - nx * half_w, points[i][1] - ny * half_w))

    return left_side + right_side[::-1]

# =============================================================================
# Render: DXF plan-view -> Transparent Concrete Road PNG + WGS-84 bounds
# =============================================================================

def compute_layer_bbox(doc, layer_name):
    pts = []
    for entity, xform in walk_entities(doc.modelspace(), doc):
        if entity.dxftype() in LINEAR_TYPES and entity.dxf.get("layer", "0").upper() == layer_name.upper():
            wcs_pts = entity_to_wcs_points(entity, xform)
            pts.extend(wcs_pts)
    valid_pts = [(x, y) for x, y in pts if _is_vn2000(x, y)]
    if not valid_pts:
        valid_pts = pts
    if not valid_pts:
        return None
    xs = [p[0] for p in valid_pts]
    ys = [p[1] for p in valid_pts]
    return min(xs), min(ys), max(xs), max(ys)


def compute_wcs_bbox(doc):
    pts = []
    for entity, xform in walk_entities(doc.modelspace(), doc):
        if entity.dxftype() in LINEAR_TYPES:
            wcs_pts = entity_to_wcs_points(entity, xform)
            for x, y in wcs_pts:
                if _is_vn2000(x, y):
                    pts.append((x, y))
    if not pts:
        for entity, xform in walk_entities(doc.modelspace(), doc):
            if entity.dxftype() in LINEAR_TYPES:
                pts.extend(entity_to_wcs_points(entity, xform))
    if not pts:
        return 0.0, 0.0, 1000.0, 1000.0
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    return min(xs), min(ys), max(xs), max(ys)


def bbox_to_wgs84_leaflet(min_x, min_y, max_x, max_y):
    pts = [(min_x, min_y), (max_x, max_y)]
    wgs = project_to_wgs84(pts)
    return {
        "south": min(wgs[0][1], wgs[1][1]),
        "west":  min(wgs[0][0], wgs[1][0]),
        "north": max(wgs[0][1], wgs[1][1]),
        "east":  max(wgs[0][0], wgs[1][0]),
    }



# =============================================================================
# Extract Corridor Vector Geometries (GeoJSON FeatureCollection)
# =============================================================================

CORRIDOR_KEYWORDS = [
    "TIM", "TUYEN", "CENTER", "MEP", "BO_VIA", "BOVIA",
    "LE_", "LEDUONG", "VACH", "RANH", "VAI", "PHANCACH"
]

def extract_corridor_geojson(msp, doc, chosen_layer, corridor_bbox, srid=4326):
    """
    Extracts key vector features (Centerline, Edges, Curbs, Shoulders, Markings)
    located within the road corridor bounding box.
    Tessellates all curved segments (bulges and arcs) into smooth coordinate chains
    and projects them to WGS-84 [lon, lat] (or target srid).
    Returns an RFC 7946 FeatureCollection dictionary.
    """
    min_x, min_y, max_x, max_y = corridor_bbox
    features = []
    seg_counts = {}

    for entity, xform in walk_entities(msp, doc):
        if entity.dxftype() not in LINEAR_TYPES:
            continue
        lyr = entity.dxf.get("layer", "")
        upper = lyr.upper()
        if any(ex in upper for ex in EXCLUDE_KEYWORDS):
            continue
        if (chosen_layer and lyr.upper() != chosen_layer.upper()) and not any(k in upper for k in CORRIDOR_KEYWORDS):
            continue

        pts = entity_to_wcs_points(entity, xform)
        if not pts or len(pts) < 2:
            continue

        # Spatial filter: at least one vertex must be inside the road corridor bbox
        if not any(min_x <= p[0] <= max_x and min_y <= p[1] <= max_y for p in pts):
            continue

        projected = project_to_wgs84(pts) if srid == 4326 else project_points(pts, srid)
        if len(projected) < 2:
            continue

        if (chosen_layer and lyr.upper() == chosen_layer.upper()) or any(k in upper for k in ["TIM", "CENTER", "TUYEN"]):
            feat_type, prefix = "Centerline", "CL"
        elif any(k in upper for k in ["MEP"]):
            feat_type, prefix = "RoadEdge", "EDGE"
        elif any(k in upper for k in ["BO_VIA", "BOVIA"]):
            feat_type, prefix = "Curb", "CURB"
        elif any(k in upper for k in ["LE", "VAI"]):
            feat_type, prefix = "Shoulder", "SHLD"
        elif any(k in upper for k in ["VACH", "SON"]):
            feat_type, prefix = "Marking", "MRK"
        else:
            feat_type, prefix = "CorridorLine", "SEG"

        seg_counts[prefix] = seg_counts.get(prefix, 0) + 1
        elev = 0.0
        try:
            elev = float(entity.dxf.get("elevation", 0.0))
        except Exception:
            pass

        features.append({
            "type": "Feature",
            "properties": {
                "layer": lyr,
                "type": feat_type,
                "segment": f"{prefix}-{seg_counts[prefix]:03d}",
                "elevation": elev
            },
            "geometry": {
                "type": "LineString",
                "coordinates": projected
            }
        })

    return {
        "type": "FeatureCollection",
        "features": features
    }


# =============================================================================
# Parse Road Vector & 2D Concrete Surface (GPS WGS-84 Coordinates)
# =============================================================================

def parse_road(dxf_path_str: str, road_width: float = 7.0, target_layer: str = None, central_meridian: float = None, swap_xy: bool = False) -> dict:
    """
    Extracts the complete road network from CAD (main body, 90-degree curve, bottom horizontal branch,
    and top triangle intersection).
    Uses Shapely geometry union and buffering to preserve 100% of curves and junctions.
    Transforms coordinates from VN-2000 to WGS-84 GPS [lat, lon].
    """
    doc = ezdxf.readfile(dxf_path_str)
    msp = doc.modelspace()

    chosen_layer = target_layer
    if not chosen_layer:
        chosen_layer, _ = _detect_centerline_layer(msp, doc)

    if not chosen_layer:
        return {
            "success": False,
            "error": "NoCenterlineLayer",
            "detail": "Could not identify a centerline layer in the CAD drawing."
        }

    half_w = max(0.5, road_width / 2.0)

    # 1. Collect linear entities directly from modelspace for chosen_layer
    primary_lines = []
    for e in msp:
        if e.dxf.layer.upper() == chosen_layer.upper():
            pts = []
            if e.dxftype() == 'LWPOLYLINE':
                pts = [(p[0], p[1]) for p in e.get_points()]
            elif e.dxftype() == 'LINE':
                pts = [(e.dxf.start.x, e.dxf.start.y), (e.dxf.end.x, e.dxf.end.y)]
            if pts and len(pts) >= 2:
                fixed_pts = []
                for p in pts:
                    if (p[0] > 1_000_000.0 and p[1] < 900_000.0) or swap_xy:
                        fixed_pts.append((p[1], p[0]))
                    else:
                        fixed_pts.append((p[0], p[1]))
                for i in range(len(fixed_pts) - 1):
                    if math.hypot(fixed_pts[i+1][0] - fixed_pts[i][0], fixed_pts[i+1][1] - fixed_pts[i][1]) > 0.01:
                        primary_lines.append(LineString([fixed_pts[i], fixed_pts[i+1]]))

    if not primary_lines:
        # Fallback to walk_entities if msp direct query was empty
        for entity, xform in walk_entities(msp, doc):
            if entity.dxftype() in LINEAR_TYPES:
                lyr = entity.dxf.get("layer", "")
                if lyr.upper() == chosen_layer.upper():
                    pts = entity_to_wcs_points(entity, xform)
                    if pts and len(pts) >= 2:
                        for i in range(len(pts) - 1):
                            if math.hypot(pts[i+1][0] - pts[i][0], pts[i+1][1] - pts[i][1]) > 0.01:
                                primary_lines.append(LineString([pts[i], pts[i+1]]))

    if not primary_lines:
        return {
            "success": False,
            "error": "EmptyCenterline",
            "detail": f"No linear entities found in layer {chosen_layer}."
        }

    # Merge primary lines to determine the primary corridor bounding box
    primary_union = unary_union(primary_lines) if HAS_SHAPELY else None
    if primary_union:
        min_x, min_y, max_x, max_y = primary_union.bounds
        min_x -= 60.0
        max_x += 60.0
        min_y -= 60.0
        max_y += 60.0
    else:
        min_x, max_x = -1e9, 1e9
        min_y, max_y = -1e9, 1e9

    # Collect connected candidate branches in the immediate corridor (e.g. top junction branches)
    road_lines = list(primary_lines)
    for e in msp:
        lyr = e.dxf.layer.upper()
        if any(k in lyr for k in ("DAUTIM", "TIMDUONGPHU", "TIMPHU", "TIM_PHU")):
            pts = []
            if e.dxftype() == 'LWPOLYLINE':
                pts = [(p[0], p[1]) for p in e.get_points()]
            elif e.dxftype() == 'LINE':
                pts = [(e.dxf.start.x, e.dxf.start.y), (e.dxf.end.x, e.dxf.end.y)]
            if pts and len(pts) >= 2:
                fixed_pts = []
                for p in pts:
                    if (p[0] > 1_000_000.0 and p[1] < 900_000.0) or swap_xy:
                        fixed_pts.append((p[1], p[0]))
                    else:
                        fixed_pts.append((p[0], p[1]))
                if any(min_x <= p[0] <= max_x and min_y <= p[1] <= max_y for p in fixed_pts):
                    for i in range(len(fixed_pts) - 1):
                        if math.hypot(fixed_pts[i+1][0] - fixed_pts[i][0], fixed_pts[i+1][1] - fixed_pts[i][1]) > 0.01:
                            road_lines.append(LineString([fixed_pts[i], fixed_pts[i+1]]))

    # Use Shapely to form full corridor polygon and network
    if HAS_SHAPELY and road_lines:
        full_net = unary_union(road_lines)
        buffered_poly = full_net.buffer(half_w, cap_style='flat', join_style='round')
        if buffered_poly.geom_type == 'MultiPolygon':
            poly_main = max(buffered_poly.geoms, key=lambda p: p.area)
        else:
            poly_main = buffered_poly

        poly_coords = list(poly_main.exterior.coords)
        tot_len = full_net.length

        cl_merged = linemerge(full_net) if full_net.geom_type == 'MultiLineString' else full_net
        if hasattr(cl_merged, 'geoms'):
            branches_sorted = sorted(list(cl_merged.geoms), key=lambda g: g.length, reverse=True)
            main_trunk = branches_sorted[0]
            raw_branches = [list(g.coords) for g in branches_sorted]
        else:
            main_trunk = cl_merged
            raw_branches = [list(cl_merged.coords)]
        main_chain = list(main_trunk.coords)
    else:
        poly_coords = list(primary_lines[0].coords)
        main_chain = poly_coords
        tot_len = 0.0
        raw_branches = [main_chain]

    test_pt = main_chain[0]
    is_vn = _is_vn2000(test_pt[0], test_pt[1])
    effective_cm = central_meridian if (central_meridian and central_meridian > 0) else detect_central_meridian(test_pt[0])

    if is_vn and HAS_PYPROJ:
        tf_custom = make_vn2000_transformer(effective_cm)
        def proj_pts(pts):
            lons, lats = tf_custom.transform([p[0] for p in pts], [p[1] for p in pts])
            return [[round(float(la), 6), round(float(lo), 6)] for lo, la in zip(lons, lats)]
    else:
        def proj_pts(pts):
            return [[round(float(p[1]), 6), round(float(p[0]), 6)] for p in pts]

    cl_gps = proj_pts(main_chain)
    poly_gps = proj_pts(poly_coords)
    branches_gps = [proj_pts(b) for b in raw_branches]

    all_lats = [p[0] for p in poly_gps]
    all_lons = [p[1] for p in poly_gps]
    bounds = [
        [round(min(all_lats), 6), round(min(all_lons), 6)],
        [round(max(all_lats), 6), round(max(all_lons), 6)]
    ]

    mid = len(poly_gps) // 2
    return {
        "success": True,
        "roadName": "Tuyến đường hoàn chỉnh (Đầy đủ nhánh & nút giao)",
        "roadWidth": road_width,
        "totalLengthMeters": round(tot_len, 2),
        "centralMeridian": effective_cm,
        "bounds": bounds,
        "centerline": cl_gps,
        "centerlineBranches": branches_gps,
        "roadSurfacePolygon": poly_gps,
        "leftEdge": poly_gps[:mid],
        "rightEdge": poly_gps[mid:]
    }

def render_dxf_to_png(
    dxf_path_str: str,
    output_size_px: int = 4096,
    background_color: str = "#00000000",
    line_color_override: str = None,
    target_layer: str = None,
    road_width: float = 7.0
) -> dict:
    """
    Renders DXF drawing to a transparent PNG with a realistic 'Concrete Road' theme:
    - Concrete gray road surface ribbon & hatches (80% opacity)
    - Highway yellow thick centerline (tim tuyen)
    - Crisp solid white road edge lines & markings (mep duong, bo via, vach son)
    - Clutter hidden (dimensions, text, title blocks)
    - Bounds transformed to WGS-84 Leaflet coordinates
    """
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    import matplotlib.patches as patches
    from ezdxf.addons.drawing import RenderContext, Frontend
    from ezdxf.addons.drawing.matplotlib import MatplotlibBackend
    from ezdxf.addons.drawing.config import Configuration, BackgroundPolicy, ColorPolicy

    doc = ezdxf.readfile(dxf_path_str)
    msp = doc.modelspace()
    ctx = RenderContext(doc)

    chosen_layer = target_layer
    if not chosen_layer:
        chosen_layer, _ = _detect_centerline_layer(msp, doc)

    # ?? Style Overrides for Realistic Road Visualization ?????????????
    original_resolve_all = ctx.resolve_all

    def concrete_road_resolve_all(entity):
        p = original_resolve_all(entity)
        lyr = (p.layer or "").upper()

        # 1. Hide technical clutter (dimensions, text, title block, cross-sections)
        if any(k in lyr for k in ["TEXT", "DIM", "KICHTHUOC", "KHUNG", "TEN", "BANG", "GHI_CHU", "NOTE", "LUOI", "CATNGANG", "TRACDOC", "BOU"]):
            p.is_visible = False
            return p

        # 2. Tim tuyen / Centerline -> Bold Highway Yellow
        if any(k in lyr for k in ["ENTPLINETUYEN", "TIMTUYEN", "TIM_TUYEN", "TIMDUONG", "CENTERLINE", "TIM"]):
            p.color = "#FFD700FF"
            p.lineweight = 1.4
            return p

        # 3. Road Markings (Vach son) -> Solid White
        if "VACH" in lyr:
            p.color = "#FFFFFFEE"
            p.lineweight = 0.9
            return p

        # 4. Mep duong / Bo via / Ranh / Le -> Crisp White
        if any(k in lyr for k in ["MEP", "BO_VIA", "BOVIA", "VAI", "RANH", "LE"]):
            p.color = "#F8FAFCFF"
            p.lineweight = 0.8
            return p

        # 5. Mat duong / Hatch -> Concrete Gray with 80% opacity (#808080CC)
        if any(k in lyr for k in ["MAT", "SURFACE", "HATCH", "BETONG", "BE_TONG", "ASPHALT", "VIA", "DUONG"]):
            p.color = "#808080CC"
            p.lineweight = 0.5
            if isinstance(p.filling, Filling) or entity.dxftype() == "HATCH":
                filling = Filling()
                filling.type = Filling.SOLID
                p.filling = filling
            return p

        # 6. Other auxiliary entities -> subtle faint lines
        p.color = "#94A3B833"
        p.lineweight = 0.2
        return p

    ctx.resolve_all = concrete_road_resolve_all

    # ?? Compute Bounding Box from Centerline Layer ?????????????????????
    bbox_source = "centerline_layer"
    if chosen_layer:
        layer_bbox = compute_layer_bbox(doc, chosen_layer)
        if layer_bbox:
            min_x, min_y, max_x, max_y = layer_bbox
        else:
            min_x, min_y, max_x, max_y = compute_wcs_bbox(doc)
            bbox_source = "all_entities_vn2000"
    else:
        min_x, min_y, max_x, max_y = compute_wcs_bbox(doc)
        bbox_source = "all_entities_vn2000"

    w = max_x - min_x
    h = max_y - min_y
    if w <= 0: w = 100.0
    if h <= 0: h = 100.0

    margin_x = w * 0.08
    margin_y = h * 0.08
    crop_min_x = min_x - margin_x
    crop_max_x = max_x + margin_x
    crop_min_y = min_y - margin_y
    crop_max_y = max_y + margin_y
    crop_w = crop_max_x - crop_min_x
    crop_h = crop_max_y - crop_min_y

    aspect = crop_w / crop_h
    if aspect >= 1.0:
        fig_w_in = 16.0
        fig_h_in = 16.0 / aspect
        dpi = int(output_size_px / 16.0)
    else:
        fig_h_in = 16.0
        fig_w_in = 16.0 * aspect
        dpi = int(output_size_px / 16.0)
    dpi = max(72, min(dpi, 300))

    fig = plt.figure(figsize=(fig_w_in, fig_h_in), dpi=dpi)
    ax = fig.add_axes([0, 0, 1, 1])
    ax.set_axis_off()
    fig.patch.set_alpha(0.0)
    ax.patch.set_alpha(0.0)

    # ?? Paint Concrete Road Ribbon Surface Under Centerline ???????????
    if road_width > 0 and chosen_layer:
        for entity, xform in walk_entities(msp, doc):
            if entity.dxftype() in LINEAR_TYPES:
                lyr = entity.dxf.get("layer", "").upper()
                if lyr == chosen_layer.upper() or any(k in lyr for k in ["ENTPLINETUYEN", "TIMTUYEN", "TIM_TUYEN"]):
                    pts = entity_to_wcs_points(entity, xform)
                    if len(pts) >= 2:
                        ribbon = generate_road_ribbon(pts, road_width)
                        if ribbon:
                            poly = patches.Polygon(ribbon, closed=True, facecolor="#808080", alpha=0.8, edgecolor="none", zorder=1)
                            ax.add_patch(poly)

    # ?? Render CAD Layout on Top ??????????????????????????????????????
    cfg = Configuration(
        background_policy=BackgroundPolicy.OFF,
        color_policy=ColorPolicy.COLOR,
        lineweight_scaling=1.5
    )
    out = MatplotlibBackend(ax)
    frontend = Frontend(ctx, out, config=cfg)
    frontend.draw_layout(msp, finalize=True)

    ax.set_xlim(crop_min_x, crop_max_x)
    ax.set_ylim(crop_min_y, crop_max_y)
    ax.set_aspect("equal", adjustable="box")

    buf = io.BytesIO()
    fig.savefig(
        buf,
        format="png",
        dpi=dpi,
        transparent=True,
        bbox_inches=None,
        pad_inches=0,
    )
    plt.close(fig)
    buf.seek(0)
    img_bytes = buf.read()
    b64 = base64.b64encode(img_bytes).decode("ascii")

    wgs84 = bbox_to_wgs84_leaflet(crop_min_x, crop_min_y, crop_max_x, crop_max_y)

    size_px = [
        int(round(fig_w_in * dpi)),
        int(round(fig_h_in * dpi))
    ]
    # Extract tessellated vector corridor GeoJSON features
    corridor_geojson = extract_corridor_geojson(
        msp, doc, chosen_layer,
        (crop_min_x, crop_min_y, crop_max_x, crop_max_y)
    )

    return {
        "bounds": [
            [wgs84["south"], wgs84["west"]],
            [wgs84["north"], wgs84["east"]]
        ],
        "image_base64": b64,
        "geojson": corridor_geojson,
        "success": True,
        "style": "concrete_road",
        "bbox_source": bbox_source,
        "cropped_to_layer": chosen_layer,
        "croppedToLayer": chosen_layer,
        "bbox_wgs84": wgs84,
        "bboxWgs84": wgs84,
        "bbox_wcs": [crop_min_x, crop_min_y, crop_max_x, crop_max_y],
        "bboxWcs": [crop_min_x, crop_min_y, crop_max_x, crop_max_y],
        "imageBase64": b64,
        "image_size_px": size_px,
        "imageSizePx": size_px
    }

# =============================================================================
# CLI
# =============================================================================

def main():
    parser = argparse.ArgumentParser(description="RoadGuard CAD ezdxf Bridge v2.3")
    sub = parser.add_subparsers(dest="command")

    lp = sub.add_parser("get-layers"); lp.add_argument("dxf_path")

    pp = sub.add_parser("parse")
    pp.add_argument("dxf_path"); pp.add_argument("--layer", default=None)
    pp.add_argument("--srid", type=int, default=4326)

    rpp = sub.add_parser("parse-road")
    rpp.add_argument("dxf_path")
    rpp.add_argument("--width", type=float, default=7.0, help="Road width in meters")
    rpp.add_argument("--layer", default=None, help="Force centerline layer")
    rpp.add_argument("--cm", type=float, default=None, help="Custom Central Meridian (e.g. 105.75, 105.5, 105.0)")
    rpp.add_argument("--swap-xy", action="store_true", default=False, help="Force swap coordinates X and Y")
    rp = sub.add_parser("render")
    rp.add_argument("dxf_path")
    rp.add_argument("--size", type=int, default=4096)
    rp.add_argument("--width", type=float, default=7.0, help="Road surface width in meters")
    rp.add_argument("--layer", default=None, help="Force centerline layer for bbox")
    rp.add_argument("--color", default=None, help="Force line color e.g. #00FFFF")

    args = parser.parse_args()
    if not hasattr(args, "dxf_path") or not args.dxf_path:
        parser.print_help(); sys.exit(1)

    if not os.path.exists(args.dxf_path):
        print(json.dumps({"success": False, "error": "FileNotFound",
                           "detail": f"File not found: {args.dxf_path}"})); sys.exit(1)

    try:
        if args.command == "get-layers":
            print(json.dumps({"success": True, "layers": get_layers(args.dxf_path)}))
        elif args.command == "parse":
            print(json.dumps(extract_geometries(args.dxf_path, args.layer, args.srid)))
        elif args.command == "parse-road":
            print(json.dumps(parse_road(
                args.dxf_path,
                road_width=getattr(args, "width", 7.0),
                target_layer=getattr(args, "layer", None),
                central_meridian=getattr(args, "cm", None),
                swap_xy=getattr(args, "swap_xy", False)
            ), ensure_ascii=False))
        elif args.command == "render":
            print(json.dumps(render_dxf_to_png(
                args.dxf_path,
                output_size_px=args.size,
                line_color_override=args.color,
                target_layer=getattr(args, "layer", None),
                road_width=getattr(args, "width", 7.0)
            )))
        else:
            parser.print_help(); sys.exit(1)
    except Exception as ex:
        import traceback
        print(json.dumps({"success": False, "error": "BridgeError",
                           "detail": str(ex), "trace": traceback.format_exc()}))
        sys.exit(1)


if __name__ == "__main__":
    main()
