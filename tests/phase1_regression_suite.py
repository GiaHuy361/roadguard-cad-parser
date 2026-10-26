#!/usr/bin/env python3
"""
Phase 0 / Phase 1 Regression & Geodetic Verification Suite
RoadGuard CAD & GIS Studio
==========================================================
Part A: 5 Baseline Invariant Tests (INV-01 .. INV-05)
Part B: 7 Technical Accuracy Target Tests (TGT-01 .. TGT-07)
Part C: 5-Region VN-2000 Geodetic Control Point Benchmark & Performance Timing

Run from repository root:
  python tests/phase1_regression_suite.py
"""
import sys
import os
import json
import math
import time
import tempfile
from pathlib import Path

BACKEND_DIR = Path(__file__).resolve().parent.parent
FRONTEND_DIR = BACKEND_DIR.parent / "roadguard-frontend"
BRIDGE_DIR = BACKEND_DIR / "Services" / "PythonBridge"
sys.path.insert(0, str(BRIDGE_DIR))

import cad_bridge  # noqa: E402
from pyproj import CRS, Transformer  # noqa: E402


def make_reference_vn2000_transformer(cm: float, k0: float = 0.9999) -> Transformer:
    """Independent reference transformer per MONRE Circular 973/2001/TT-TCĐC & Decision 05/2007/QĐ-BTNMT."""
    proj = (
        f"+proj=tmerc +lat_0=0 +lon_0={cm} +k={k0} "
        f"+x_0=500000 +y_0=0 +ellps=WGS84 "
        f"+towgs84=-191.90441429,-39.30318279,-111.45032835,"
        f"-0.00928836,0.01975479,-0.00427372,0.252906278 "
        f"+units=m +no_defs"
    )
    return Transformer.from_crs(CRS.from_proj4(proj), CRS.from_epsg(4326), always_xy=True)


def haversine_m(lon1: float, lat1: float, lon2: float, lat2: float) -> float:
    R = 6378137.0
    dlat = math.radians(lat2 - lat1)
    dlon = math.radians(lon2 - lon1)
    a = (
        math.sin(dlat / 2) ** 2
        + math.cos(math.radians(lat1)) * math.cos(math.radians(lat2)) * math.sin(dlon / 2) ** 2
    )
    return 2 * R * math.atan2(math.sqrt(a), math.sqrt(1 - a))


def call_parse_coords(payload: dict, road_width: float = 7.0, central_meridian: float = 105.75, scale_factor: float = 0.9999, axis_order: str = "AUTO"):
    with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8") as tf_json:
        json.dump(payload, tf_json)
        tmp_path = tf_json.name
    try:
        return cad_bridge.parse_coords(
            tmp_path,
            road_width=road_width,
            central_meridian=central_meridian,
            scale_factor=scale_factor,
            axis_order=axis_order,
        )
    finally:
        if os.path.exists(tmp_path):
            os.remove(tmp_path)


def main():
    baseline_invariants = []
    phase1_targets = []
    performance = {}

    def add_invariant(test_id, name, passed, metric):
        baseline_invariants.append({
            "id": test_id,
            "name": name,
            "status": "PASS" if passed else "FAIL",
            "passed": passed,
            "metric": metric,
        })

    def add_target(test_id, name, passed, phase0_baseline_observation, phase1_verified_result):
        phase1_targets.append({
            "id": test_id,
            "name": name,
            "status": "PASS" if passed else "FAIL",
            "passed": passed,
            "phase0_baseline": phase0_baseline_observation,
            "phase1_result": phase1_verified_result,
        })

    # ==============================================================================
    # PART A: BASELINE INVARIANTS (INV-01 .. INV-05)
    # ==============================================================================
    dxf_bdtk = BACKEND_DIR / "3. BDTK.dxf"
    t0 = time.perf_counter()
    layers = cad_bridge.get_layers(str(dxf_bdtk))
    performance["get_layers_ms"] = round((time.perf_counter() - t0) * 1000, 1)

    t0 = time.perf_counter()
    road_res = cad_bridge.parse_road(str(dxf_bdtk), road_width=7.0, central_meridian=105.75, scale_factor=0.9999)
    performance["parse_road_bdtk_ms"] = round((time.perf_counter() - t0) * 1000, 1)

    t0 = time.perf_counter()
    ext_res = cad_bridge.extract_geometries(str(dxf_bdtk), srid=4326, central_meridian=105.75)
    performance["extract_geometries_bdtk_ms"] = round((time.perf_counter() - t0) * 1000, 1)

    add_invariant(
        "INV-01",
        "3. BDTK.dxf get_layers & layerStats",
        len(layers) == 355 and len(road_res.get("layerStats", [])) == 110,
        f"defined_layers={len(layers)}, active_layerStats={len(road_res.get('layerStats', []))} ({performance['get_layers_ms']} ms)",
    )
    add_invariant(
        "INV-02",
        "3. BDTK.dxf parse_road main corridor & polygon",
        road_res.get("success") is True
        and abs(road_res.get("totalLengthMeters", 0) - 746.57) < 0.05
        and len(road_res.get("centerline", [])) == 149
        and len(road_res.get("roadSurfacePolygon", [])) >= 100,
        f"totalLengthMeters={road_res.get('totalLengthMeters')}m, nominalAreaSqm={road_res.get('nominalAreaSqm')}m², surfaceAreaSqm={road_res.get('surfaceAreaSqm')}m², centerline_pts={len(road_res.get('centerline', []))} ({performance['parse_road_bdtk_ms']} ms)",
    )
    add_invariant(
        "INV-03",
        "3. BDTK.dxf extract_geometries (parse-dxf bridge)",
        ext_res.get("success") is True
        and ext_res.get("detectedLayer") == "TKDTIMTUYENTKT2"
        and ext_res.get("totalEntities") == 57,
        f"detectedLayer={ext_res.get('detectedLayer')}, totalEntities={ext_res.get('totalEntities')} ({performance['extract_geometries_bdtk_ms']} ms)",
    )

    c_res = call_parse_coords({"text": "1191450.250, 602340.125\n1191550.250, 602340.125"}, road_width=7.0, central_meridian=105.75)
    add_invariant(
        "INV-04",
        "parse_coords 100m North-South segment in TP.HCM",
        c_res.get("success") is True
        and abs(c_res.get("totalLengthMeters", 0) - 100.0) < 0.01
        and len(c_res.get("roadSurfacePolygon", [])) == 5,
        f"totalLengthMeters={c_res.get('totalLengthMeters')}m, nominalAreaSqm={c_res.get('nominalAreaSqm')}m², surfaceAreaSqm={c_res.get('surfaceAreaSqm')}m², polygon_pts={len(c_res.get('roadSurfacePolygon', []))}",
    )

    cadmap_code = (FRONTEND_DIR / "src" / "components" / "CadMap.jsx").read_text(encoding="utf-8")
    worker_file = FRONTEND_DIR / "public" / "maplibre" / "maplibre-gl-worker.mjs"
    add_invariant(
        "INV-05",
        "MapLibre Production Worker & isStyleReady Guard",
        "setWorkerUrl" in cadmap_code and "isStyleReady" in cadmap_code and worker_file.exists(),
        f"worker_exists={worker_file.exists()}, setWorkerUrl={'setWorkerUrl' in cadmap_code}",
    )

    # ==============================================================================
    # PART B: PHASE 1 TARGET TECHNICAL ACCURACY TESTS (TGT-01 .. TGT-07)
    # ==============================================================================

    # Target 1: 8-decimal geodetic output precision across 5 Vietnam control points
    control_points = [
        {"name": "TP.HCM (Múi 3°, CM=105.75)", "cm": 105.75, "k0": 0.9999, "e": 602340.125, "n": 1191450.250},
        {"name": "Hà Nội (Múi 3°, CM=105.00)", "cm": 105.00, "k0": 0.9999, "e": 584500.000, "n": 2325600.000},
        {"name": "Đà Nẵng (Múi 3°, CM=107.75)", "cm": 107.75, "k0": 0.9999, "e": 545120.000, "n": 1775890.000},
        {"name": "Cà Mau (Múi 3°, CM=104.50, Y<10^6)", "cm": 104.50, "k0": 0.9999, "e": 515430.000, "n": 955210.000},
        {"name": "Toàn quốc (Múi 6°, CM=105.00, k0=0.9996)", "cm": 105.00, "k0": 0.9996, "e": 842500.000, "n": 1776300.000},
    ]
    cp_benchmarks = []
    max_err_mm = 0.0
    for cp in control_points:
        ref_tf = make_reference_vn2000_transformer(cp["cm"], cp["k0"])
        ref_lon, ref_lat = ref_tf.transform(cp["e"], cp["n"])
        res_cp = call_parse_coords(
            {"text": f"{cp['n']}, {cp['e']}\n{cp['n'] + 100.0}, {cp['e']}", "scaleFactor": cp["k0"], "axisOrder": "VN2000_XY"},
            road_width=7.0,
            central_meridian=cp["cm"],
            scale_factor=cp["k0"],
            axis_order="VN2000_XY",
        )
        out_lat, out_lon = res_cp["centerline"][0]
        err_mm = haversine_m(ref_lon, ref_lat, out_lon, out_lat) * 1000.0
        max_err_mm = max(max_err_mm, err_mm)
        cp_benchmarks.append({
            "region": cp["name"],
            "input_vn2000_EN": [cp["e"], cp["n"]],
            "reference_wgs84_lon_lat": [round(ref_lon, 8), round(ref_lat, 8)],
            "output_wgs84_lon_lat": [out_lon, out_lat],
            "numerical_discrepancy_mm": round(err_mm, 3),
        })

    add_target(
        "TGT-01",
        "Geodetic Coordinate Precision (8 decimal places, <= 2.0 mm numerical error vs pyproj reference)",
        max_err_mm <= 2.0,
        "round(val, 6) caused 54.43 mm quantization error",
        f"round(val, 8) achieves max numerical discrepancy = {max_err_mm:.2f} mm across 5 control points (<= 2.0 mm tolerance)",
    )

    # Target 2: Ca Mau Southern Vietnam Coordinates (Northing 955,210m < 1,000,000m)
    res_cm = call_parse_coords(
        {"text": "955210.000, 515430.000\n955310.000, 515430.000"},
        road_width=7.0,
        central_meridian=104.50,
    )
    add_target(
        "TGT-02",
        "Southern Vietnam (Cà Mau) VN-2000 Support (Northing < 1,000,000m)",
        res_cm.get("success") is True and abs(res_cm.get("totalLengthMeters", 0) - 100.0) < 0.02,
        "Rejected with CoordinateOutOfRange because _VN2000_Y_MIN was hardcoded to 1,000,000.0m",
        f"Accepted: totalLengthMeters={res_cm.get('totalLengthMeters')}m, firstPt={res_cm.get('centerline', [None])[0]}",
    )

    # Target 3: 6-Degree Zone (k0 = 0.9996) vs 3-Degree Zone (k0 = 0.9999)
    ref_tf_6deg = make_reference_vn2000_transformer(105.00, 0.9996)
    ref6_lon, ref6_lat = ref_tf_6deg.transform(842500.000, 1776300.000)
    res_z6 = call_parse_coords(
        {"text": "1776300.000, 842500.000\n1776400.000, 842500.000", "scaleFactor": 0.9996},
        road_width=7.0,
        central_meridian=105.00,
        scale_factor=0.9996,
    )
    z6_lat, z6_lon = res_z6["centerline"][0]
    err_z6_mm = haversine_m(ref6_lon, ref6_lat, z6_lon, z6_lat) * 1000.0
    add_target(
        "TGT-03",
        "VN-2000 6-Degree Zone Support (k0 = 0.9996)",
        err_z6_mm <= 2.0,
        "Hardcoded k=0.9999 caused 125.92 m error on 6-degree zone data",
        f"Configurable scaleFactor=0.9996 matches pyproj 6° reference with {err_z6_mm:.2f} mm discrepancy",
    )

    # Target 4: Ambiguous X/Y Axis Confidence Check
    res_amb_auto = call_parse_coords(
        {"text": "910000.0, 880000.0\n910100.0, 880000.0", "axisOrder": "AUTO"},
        road_width=7.0,
        central_meridian=105.00,
        axis_order="AUTO",
    )
    res_amb_explicit = call_parse_coords(
        {"text": "910000.0, 880000.0\n910100.0, 880000.0", "axisOrder": "VN2000_XY"},
        road_width=7.0,
        central_meridian=105.00,
        axis_order="VN2000_XY",
    )
    add_target(
        "TGT-04",
        "Ambiguous X/Y Coordinate Confidence Check in AUTO Mode",
        res_amb_auto.get("error") == "AmbiguousAxisOrder"
        and res_amb_auto.get("requiresAxisConfirmation") is True
        and res_amb_explicit.get("success") is True,
        "Generic failure without flagging axis ambiguity or allowing explicit axisOrder override",
        f"AUTO returns error='AmbiguousAxisOrder' (requiresAxisConfirmation=True); explicit axisOrder='VN2000_XY' succeeds ({res_amb_explicit.get('totalLengthMeters')}m)",
    )

    # Target 5: RoadWidth Exact Consistency (No < 5.0m Auto-Doubling)
    sidebar_code = (FRONTEND_DIR / "src" / "components" / "Sidebar.jsx").read_text(encoding="utf-8")
    app_code = (FRONTEND_DIR / "src" / "App.jsx").read_text(encoding="utf-8")
    has_auto_double = (
        "rawW >= 5.0 ? rawW : rawW * 2.0" in sidebar_code
        or "parseFloat(roadWidth) >= 5.0" in sidebar_code
        or ">= 5 ?" in app_code
    )
    res_w35 = call_parse_coords(
        {"text": "1191450.250, 602340.125\n1191550.250, 602340.125"},
        road_width=3.5,
        central_meridian=105.75,
    )
    add_target(
        "TGT-05",
        "RoadWidth Exact Consistency (No Silent Doubling when < 5.0m)",
        not has_auto_double and res_w35.get("roadWidth") == 3.5 and abs(res_w35.get("nominalAreaSqm", 0) - 350.0) < 0.1,
        "Sidebar.jsx and App.jsx multiplied roadWidth by 2.0 when < 5.0m, causing 2x mismatch with CadMap.jsx",
        f"Zero auto-doubling in Frontend/Backend; roadWidth=3.5m -> nominalAreaSqm={res_w35.get('nominalAreaSqm')}m², surfaceAreaSqm={res_w35.get('surfaceAreaSqm')}m²",
    )

    # Target 6: RFC 7946 GeoJSON Compliance (bbox & [lon, lat])
    builder_code = (FRONTEND_DIR / "src" / "utils" / "roadEngineeringBuilder.js").read_text(encoding="utf-8")
    bdtk_bbox = road_res.get("bbox")
    has_rfc7946_bbox = (
        "computeRfc7946Bbox" in builder_code
        and isinstance(bdtk_bbox, list)
        and len(bdtk_bbox) == 4
        and bdtk_bbox[0] < bdtk_bbox[2]
        and bdtk_bbox[1] < bdtk_bbox[3]
    )
    add_target(
        "TGT-06",
        "RFC 7946 GeoJSON Root `bbox: [minLon, minLat, maxLon, maxLat]`",
        has_rfc7946_bbox,
        "Output lacked RFC 7946 `bbox` array",
        f"Both Backend and Frontend output RFC 7946 bbox={bdtk_bbox}",
    )

    # Target 7: Single Source of Truth (SSOT) for Stations & Concrete Slabs (Map == GeoJSON == Profile == Excel)
    excel_code = (FRONTEND_DIR / "src" / "utils" / "excelExporter.js").read_text(encoding="utf-8")
    profile_code = (FRONTEND_DIR / "src" / "components" / "CrossSectionProfile.jsx").read_text(encoding="utf-8")
    has_approx_fallback = "idx * 20 // approx" in excel_code
    has_dead_api = "/api/cad/export-excel" in excel_code
    all_use_ssot = (
        "enrichGeoJsonWithEngineeringFeatures" in cadmap_code
        and "enrichGeoJsonWithEngineeringFeatures" in sidebar_code
        and "enrichGeoJsonWithEngineeringFeatures" in profile_code
        and "enrichGeoJsonWithEngineeringFeatures" in excel_code
        and "enrichGeoJsonWithEngineeringFeatures" in app_code
    )
    add_target(
        "TGT-07",
        "Unified Station & Slab SSOT (Zero Approximations in Excel & Profile)",
        not has_approx_fallback and not has_dead_api and all_use_ssot,
        "StationMilestone & Slab features were only generated inside CadMap.jsx; Excel called dead /api/cad/export-excel and fell back to idx * 20 // approx",
        "All 5 modules (App, Sidebar, CadMap, CrossSectionProfile, excelExporter) use enrichGeoJsonWithEngineeringFeatures from roadEngineeringBuilder.js",
    )

    report = {
        "summary": {
            "baseline_invariants_passed": f"{sum(1 for x in baseline_invariants if x['passed'])}/{len(baseline_invariants)}",
            "phase1_targets_passed": f"{sum(1 for x in phase1_targets if x['passed'])}/{len(phase1_targets)}",
            "performance_bdtk_23mb": performance,
        },
        "part_a_baseline_invariants": baseline_invariants,
        "part_b_phase1_targets": phase1_targets,
        "part_c_geodetic_control_points": cp_benchmarks,
    }
    print(json.dumps(report, indent=2, ensure_ascii=False))
    all_ok = all(x["passed"] for x in baseline_invariants + phase1_targets)
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
