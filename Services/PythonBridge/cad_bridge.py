#!/usr/bin/env python3
"""
RoadGuard CAD Parser - Python ezdxf Bridge v2.0
================================================
FIXES:
  1. WCS Transform: All entities (including those inside INSERT/Block References)
     are transformed to World Coordinate System (WCS) using ezdxf OCS->WCS math.
  2. Projection: Proper VN-2000 -> WGS-84 via pyproj with PROJ string + always_xy=True.
  3. Bulge interpolation: LWPOLYLINE arc segments are tessellated correctly.
  4. ARC entities: Tessellated into point arrays.
"""

import sys
import os
import re
import json
import math
import argparse
import ezdxf
from ezdxf import path as dxf_path
from ezdxf.math import Matrix44

try:
    from pyproj import Transformer, CRS
    HAS_PYPROJ = True
except ImportError:
    HAS_PYPROJ = False

# ─────────────────────────────────────────────
# Config
# ─────────────────────────────────────────────
CENTERLINE_KEYWORDS = [
    "ENTPLINETUYEN", "TIM_THIET_KE", "TKDTIMTUYENTKT1", "TKDTIMTUYENTKT2",
    "TIMDUONG", "TIM_DUONG", "ROAD_CENTERLINE", "CENTERLINE",
    "TIMTUYEN", "TIM", "CENTER", "TUYEN", "AXIS", "ROAD"
]
EXCLUDE_KEYWORDS = [
    "SUONTUYEN", "DAUTIM", "TEXT", "DIM", "NOTE", "GHICHU",
    "COC", "LYTRINH", "KHUNG", "TITLE", "BORDER", "HATCHING", "HATCH"
]
ARC_TESSELLATION_STEPS = 64   # points per arc
MIN_CENTERLINE_LENGTH = 50.0  # metres

# ─────────────────────────────────────────────
# Coordinate Projection
# ─────────────────────────────────────────────

def detect_central_meridian(x_sample: float) -> float:
    """Guess VN-2000 zone meridian from an easting sample."""
    # VN-2000 uses 3-degree zones: 102, 105, 108, 111 degrees
    # Typical easting near 500000 = zone centre
    # Rough detection by easting range
    if x_sample < 300000:
        return 102.0
    elif x_sample < 600000:
        return 105.0
    elif x_sample < 900000:
        return 108.0
    else:
        return 111.0

def make_vn2000_transformer(central_meridian: float):
    """
    Build a pyproj Transformer: VN-2000 TM zone -> WGS-84 geographic.
    CRITICAL: always_xy=True prevents lat/lon axis swap.
    """
    proj_vn2000 = (
        f"+proj=tmerc +lat_0=0 +lon_0={central_meridian} +k=0.9999 "
        f"+x_0=500000 +y_0=0 +ellps=WGS84 "
        f"+towgs84=-191.90441429,-39.30318279,-111.45032835,"
        f"-0.00928836,0.01975479,-0.00427372,0.252906278 "
        f"+units=m +no_defs"
    )
    wgs84 = "EPSG:4326"
    return Transformer.from_crs(
        CRS.from_proj4(proj_vn2000),
        CRS.from_epsg(4326),
        always_xy=True   # CRITICAL: output is (lon, lat) not (lat, lon)
    )

def project_to_wgs84(points_xy: list, central_meridian: float = 105.0) -> list:
    """
    Convert list of (x, y) VN-2000/UTM coords to [[lon, lat], ...].
    Returns original coords unchanged if pyproj unavailable or coords look like lon/lat already.
    """
    if not points_xy:
        return []

    # If coordinates are already lon/lat range, skip projection
    x0, y0 = points_xy[0]
    if abs(x0) <= 180.0 and abs(y0) <= 90.0:
        return [[x, y] for x, y in points_xy]

    if not HAS_PYPROJ:
        # Fallback: manual UTM Zone 48N (lon_0=105) approximation
        return [_manual_utm48n_to_wgs84(x, y) for x, y in points_xy]

    detected_cm = detect_central_meridian(x0)
    transformer = make_vn2000_transformer(detected_cm)
    lons, lats = transformer.transform(
        [p[0] for p in points_xy],
        [p[1] for p in points_xy]
    )
    return [[lon, lat] for lon, lat in zip(lons, lats)]


def _manual_utm48n_to_wgs84(easting: float, northing: float):
    """Fallback manual UTM Zone 48N -> WGS84 (lon_0=105)."""
    a = 6378137.0
    f = 1.0 / 298.257223563
    k0 = 0.9996
    e2 = 2 * f - f * f
    e = math.sqrt(e2)
    e1 = (1 - math.sqrt(1 - e2)) / (1 + math.sqrt(1 - e2))
    x = easting - 500000.0
    y = northing
    m = y / k0
    mu = m / (a * (1 - e2/4 - 3*e2**2/64 - 5*e2**3/256))
    phi1 = (mu
            + (3*e1/2 - 27*e1**3/32) * math.sin(2*mu)
            + (21*e1**2/16 - 55*e1**4/32) * math.sin(4*mu)
            + (151*e1**3/96) * math.sin(6*mu))
    sin_phi1 = math.sin(phi1)
    cos_phi1 = math.cos(phi1)
    tan_phi1 = math.tan(phi1)
    n1 = a / math.sqrt(1 - e2 * sin_phi1**2)
    t1 = tan_phi1**2
    c1 = (e2 / (1 - e2)) * cos_phi1**2
    r1 = a * (1 - e2) / (1 - e2 * sin_phi1**2)**1.5
    d = x / (n1 * k0)
    ep2 = e2 / (1 - e2)
    lat = phi1 - (n1 * tan_phi1 / r1) * (
        d**2/2
        - (5 + 3*t1 + 10*c1 - 4*c1**2 - 9*ep2) * d**4/24
        + (61 + 90*t1 + 298*c1 + 45*t1**2 - 252*ep2 - 3*c1**2) * d**6/720
    )
    lon0 = math.radians(105.0)
    lon = lon0 + (
        d
        - (1 + 2*t1 + c1) * d**3/6
        + (5 - 2*c1 + 28*t1 - 3*c1**2 + 8*ep2 + 24*t1**2) * d**5/120
    ) / cos_phi1
    return [math.degrees(lon), math.degrees(lat)]


# ─────────────────────────────────────────────
# WCS Transform helpers
# ─────────────────────────────────────────────

def entity_to_wcs_points(entity, transform: Matrix44 = None) -> list:
    """
    Extract 2D point list from a single entity and apply WCS transform.
    Returns list of (x, y) tuples in WCS.

    CRITICAL FIX: When an entity lives inside a Block Reference (INSERT),
    the caller must pass the block's cumulative Matrix44 so we multiply
    each vertex: wcs_pt = transform @ ocs_pt.
    """
    etype = entity.dxftype()
    points = []

    try:
        if etype == "LINE":
            pts = [entity.dxf.start, entity.dxf.end]
            points = [(p.x, p.y) for p in pts]

        elif etype == "LWPOLYLINE":
            # get_points('xy') returns only (x, y) – no Z noise
            raw = list(entity.get_points("xyb"))  # x, y, bulge
            if not raw:
                raw_xy = list(entity.get_points("xy"))
                raw = [(x, y, 0.0) for x, y in raw_xy]

            for i in range(len(raw)):
                x0_seg, y0_seg, bulge = raw[i][0], raw[i][1], raw[i][2]
                if i + 1 < len(raw):
                    x1_seg, y1_seg = raw[i+1][0], raw[i+1][1]
                else:
                    # Last vertex – just add the point, arc handled by previous segment
                    points.append((x0_seg, y0_seg))
                    break

                if abs(bulge) < 1e-9:
                    # Straight segment
                    points.append((x0_seg, y0_seg))
                else:
                    # Arc segment: tessellate bulge
                    arc_pts = _tessellate_bulge(x0_seg, y0_seg, x1_seg, y1_seg, bulge)
                    points.extend(arc_pts[:-1])  # exclude last to avoid duplicate

            if raw:
                last = raw[-1]
                points.append((last[0], last[1]))

            # Handle closed polylines
            if entity.is_closed and points and points[0] != points[-1]:
                points.append(points[0])

        elif etype == "POLYLINE":
            verts = list(entity.vertices)
            for v in verts:
                points.append((v.dxf.location.x, v.dxf.location.y))

        elif etype == "ARC":
            pts = _tessellate_arc(
                entity.dxf.center.x, entity.dxf.center.y,
                entity.dxf.radius,
                entity.dxf.start_angle, entity.dxf.end_angle
            )
            points = pts

        elif etype == "SPLINE":
            p = dxf_path.make_path(entity)
            verts = list(p.flattening(distance=0.5))
            points = [(v.x, v.y) for v in verts]

        elif etype == "CIRCLE":
            pts = _tessellate_arc(
                entity.dxf.center.x, entity.dxf.center.y,
                entity.dxf.radius, 0, 360
            )
            points = pts

    except Exception:
        return []

    if not points or len(points) < 2:
        return []

    # ── CRITICAL WCS TRANSFORM ──────────────────────────────────────────────
    # If entity was encountered via an INSERT (block reference), apply the
    # accumulated transformation matrix to bring OCS -> WCS.
    if transform is not None:
        wcs_points = []
        for x, y in points:
            wcs_pt = transform.transform((x, y, 0.0))
            wcs_points.append((wcs_pt[0], wcs_pt[1]))
        return wcs_points
    # ────────────────────────────────────────────────────────────────────────

    return points


def _tessellate_bulge(x0, y0, x1, y1, bulge, steps=None) -> list:
    """
    Convert a LWPOLYLINE bulge segment into tessellated (x,y) points.
    bulge = tan(included_angle / 4).  Positive = CCW, negative = CW.
    """
    dx = x1 - x0
    dy = y1 - y0
    chord_len = math.hypot(dx, dy)
    if chord_len < 1e-10:
        return [(x0, y0)]

    # Sagitta = |bulge| * chord / 2
    # Radius from bulge: R = chord / (2 * sin(2 * atan(|b|)))
    b = bulge
    theta = 4.0 * math.atan(abs(b))          # total included angle
    R = chord_len / (2.0 * math.sin(theta / 2.0))

    # Centre of arc
    mid_x = (x0 + x1) / 2.0
    mid_y = (y0 + y1) / 2.0
    perp_len = math.sqrt(R**2 - (chord_len / 2.0)**2)
    perp_x = -(dy / chord_len) * perp_len
    perp_y =  (dx / chord_len) * perp_len
    if b < 0:
        perp_x, perp_y = -perp_x, -perp_y
    cx = mid_x + perp_x
    cy = mid_y + perp_y

    # Start and end angles
    a_start = math.atan2(y0 - cy, x0 - cx)
    a_end   = math.atan2(y1 - cy, x1 - cx)

    if b > 0:   # CCW
        if a_end <= a_start:
            a_end += 2.0 * math.pi
    else:       # CW
        if a_end >= a_start:
            a_end -= 2.0 * math.pi

    n = max(8, int(abs(theta) / math.pi * ARC_TESSELLATION_STEPS))
    pts = []
    for i in range(n + 1):
        t = i / n
        a = a_start + t * (a_end - a_start)
        pts.append((cx + R * math.cos(a), cy + R * math.sin(a)))
    return pts


def _tessellate_arc(cx, cy, radius, start_deg, end_deg, steps=ARC_TESSELLATION_STEPS) -> list:
    """Tessellate an ARC entity into (x, y) point list."""
    s = math.radians(start_deg)
    e = math.radians(end_deg)
    if e <= s:
        e += 2 * math.pi
    delta = e - s
    pts = []
    for i in range(steps + 1):
        a = s + (delta * i / steps)
        pts.append((cx + radius * math.cos(a), cy + radius * math.sin(a)))
    return pts


# ─────────────────────────────────────────────
# Entity walking: modelspace + INSERT recursion
# ─────────────────────────────────────────────

LINEAR_TYPES = {"LINE", "LWPOLYLINE", "POLYLINE", "ARC", "SPLINE", "CIRCLE"}


def walk_entities(entities, doc, transform: Matrix44 = None, depth=0):
    """
    Recursively walk entities.  When an INSERT is found, compute its
    cumulative Matrix44 and descend into its block definition.

    Yields (entity, effective_transform) tuples.
    """
    if depth > 8:   # Safety: avoid infinite recursion on corrupt files
        return

    for entity in entities:
        etype = entity.dxftype()

        if etype == "INSERT":
            try:
                # Get this INSERT's transform relative to parent
                insert_matrix = entity.matrix44()
                # Accumulate: parent_transform @ this_transform
                if transform is not None:
                    combined = transform @ insert_matrix
                else:
                    combined = insert_matrix

                block_name = entity.dxf.name
                if block_name in doc.blocks:
                    block_entities = list(doc.blocks[block_name])
                    yield from walk_entities(block_entities, doc, combined, depth + 1)
            except Exception:
                continue

        elif etype in LINEAR_TYPES:
            yield (entity, transform)


# ─────────────────────────────────────────────
# Layer discovery
# ─────────────────────────────────────────────

def get_layers(dxf_path_str: str) -> list:
    doc = ezdxf.readfile(dxf_path_str)
    layers = set()
    for lyr in doc.layers:
        if lyr.dxf.name:
            layers.add(lyr.dxf.name)
    msp = doc.modelspace()
    for e in msp:
        lyr = e.dxf.get("layer", "")
        if lyr:
            layers.add(lyr)
    return sorted(layers, key=lambda x: x.lower())


# ─────────────────────────────────────────────
# Main extraction
# ─────────────────────────────────────────────

def extract_geometries(dxf_path_str: str, target_layer: str = None, srid: int = 4326) -> dict:
    doc = ezdxf.readfile(dxf_path_str)
    msp = doc.modelspace()

    # ── Pass 1: Collect entities per layer (with WCS transform applied) ────
    all_layers = set(lyr.dxf.name for lyr in doc.layers if lyr.dxf.name)
    layer_raw: dict = {}   # layer_name -> list of wcs (x,y) segments (each is a list of tuples)

    for entity, xform in walk_entities(msp, doc):
        lyr = entity.dxf.get("layer", "0")
        all_layers.add(lyr)
        pts = entity_to_wcs_points(entity, xform)
        if pts and len(pts) >= 2:
            layer_raw.setdefault(lyr, []).append(pts)

    # ── Pass 2: Choose target layer ────────────────────────────────────────
    chosen_layer = None

    if target_layer:
        # Exact match first, then case-insensitive
        if target_layer in layer_raw:
            chosen_layer = target_layer
        else:
            for l in layer_raw:
                if l.lower() == target_layer.lower():
                    chosen_layer = l
                    break

    if not chosen_layer:
        # Heuristic: keyword match + rank by total WCS geometry length
        layer_lengths: dict = {}
        for l, segs in layer_raw.items():
            upper = l.upper()
            if any(ex in upper for ex in EXCLUDE_KEYWORDS):
                continue
            kw_match = any(
                re.search(r"(^|[_\-])" + re.escape(kw) + r"($|[_\-])", l, re.I)
                or kw.upper() in upper
                for kw in CENTERLINE_KEYWORDS
            )
            if not kw_match:
                continue
            total_len = 0.0
            for seg in segs:
                for i in range(1, len(seg)):
                    dx = seg[i][0] - seg[i-1][0]
                    dy = seg[i][1] - seg[i-1][1]
                    total_len += math.hypot(dx, dy)
            if total_len >= MIN_CENTERLINE_LENGTH:
                layer_lengths[l] = total_len

        if layer_lengths:
            chosen_layer = max(layer_lengths, key=layer_lengths.get)

    if not chosen_layer and layer_raw:
        # Last resort: the layer with the most entity count
        chosen_layer = max(layer_raw, key=lambda l: len(layer_raw[l]))

    if not chosen_layer:
        return {
            "success": False,
            "error": "NoLinearEntities",
            "detail": "No valid lines or polylines found in the CAD drawing.",
            "layers": sorted(all_layers, key=str.lower)
        }

    # ── Pass 3: Project to WGS-84 ─────────────────────────────────────────
    segs_wcs = layer_raw.get(chosen_layer, [])
    features_out = []

    for seg in segs_wcs:
        if len(seg) < 2:
            continue
        # Project entire segment at once (batch is faster for pyproj)
        projected = project_to_wgs84(seg)
        features_out.append({
            "layer": chosen_layer,
            "coordinates": projected  # [[lon, lat], ...]
        })

    return {
        "success": True,
        "detectedLayer": chosen_layer,
        "totalEntities": len(features_out),
        "layers": sorted(all_layers, key=str.lower),
        "features": features_out
    }


# ─────────────────────────────────────────────
# CLI entry point
# ─────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="RoadGuard CAD ezdxf Python Bridge v2.0")
    subparsers = parser.add_subparsers(dest="command")

    lp = subparsers.add_parser("get-layers")
    lp.add_argument("dxf_path")

    pp = subparsers.add_parser("parse")
    pp.add_argument("dxf_path")
    pp.add_argument("--layer", default=None)
    pp.add_argument("--srid", type=int, default=4326)

    args = parser.parse_args()

    if not hasattr(args, "dxf_path") or not args.dxf_path:
        parser.print_help()
        sys.exit(1)

    if not os.path.exists(args.dxf_path):
        print(json.dumps({
            "success": False, "error": "FileNotFound",
            "detail": f"File not found: {args.dxf_path}"
        }))
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
