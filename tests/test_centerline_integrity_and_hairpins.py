import json
import math
import os
import pathlib
import sys
import tempfile

REPO_ROOT = pathlib.Path(__file__).resolve().parents[1]
BRIDGE_DIR = REPO_ROOT / "Services" / "PythonBridge"
sys.path.insert(0, str(BRIDGE_DIR))

import cad_bridge
import ezdxf
from shapely.geometry import LineString


def _save_temp_dxf(doc) -> str:
    fd, path = tempfile.mkstemp(suffix=".dxf")
    os.close(fd)
    doc.saveas(path)
    return path


def _call_draft_builder(centerline_latlon, source_crs=32648, road_code="Demo01", change_reason="Regression test"):
    wgs_lonlat = [[pt[1], pt[0]] for pt in centerline_latlon]
    fd, path = tempfile.mkstemp(suffix=".json")
    os.close(fd)
    try:
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(
                {
                    "sourceCrs": source_crs,
                    "roadCode": road_code,
                    "changeReason": change_reason,
                    "roadWidth": 7.0,
                    "surveyWidthMeters": 14.0,
                    "centerlineWgs84": wgs_lonlat,
                },
                fh,
            )
        return cad_bridge.build_and_validate_roadguard_draft(path)
    finally:
        if os.path.exists(path):
            os.remove(path)


def _make_segmented_line(x_start, y_start, x_end, y_end, step_m=5.0):
    dist = math.hypot(x_end - x_start, y_end - y_start)
    n_steps = max(1, int(math.ceil(dist / step_m)))
    return [
        (x_start + (x_end - x_start) * (i / n_steps), y_start + (y_end - y_start) * (i / n_steps))
        for i in range(n_steps + 1)
    ]


def test_valid_hairpins_and_sharp_curves():
    print("[1/5] Testing valid narrow/wide hairpins (U-turns) and sharp curves for zero truncation...")
    x0, y0 = 580000.0, 1270000.0
    leg_len = 120.0

    for radius_m in [3.5, 4.0, 4.5, 6.0, 10.0, 15.0]:
        expected_len = 2.0 * leg_len + math.pi * radius_m
        for mode in [
            "single_lwpoly",
            "exact_multi_vertex",
            "gap_020m_multi_vertex",
            "transverse_gap_multi_vertex",
            "degree3_spur_at_pt",
        ]:
            doc = ezdxf.new("R2010")
            msp = doc.modelspace()
            lyr = "TIM_TUYEN"

            if mode == "single_lwpoly":
                pts = [
                    (x0, y0, 0, 0, 0.0),
                    (x0 + leg_len, y0, 0, 0, 1.0),
                    (x0 + leg_len, y0 + 2.0 * radius_m, 0, 0, 0.0),
                    (x0, y0 + 2.0 * radius_m, 0, 0, 0.0),
                ]
                msp.add_lwpolyline(pts, format="xyseb", dxfattribs={"layer": lyr})
            elif mode == "exact_multi_vertex":
                msp.add_lwpolyline(_make_segmented_line(x0, y0, x0 + leg_len, y0, 5.0), dxfattribs={"layer": lyr})
                msp.add_arc((x0 + leg_len, y0 + radius_m), radius_m, -90.0, 90.0, dxfattribs={"layer": lyr})
                msp.add_lwpolyline(
                    _make_segmented_line(x0 + leg_len, y0 + 2.0 * radius_m, x0, y0 + 2.0 * radius_m, 5.0),
                    dxfattribs={"layer": lyr},
                )
            elif mode == "gap_020m_multi_vertex":
                msp.add_lwpolyline(_make_segmented_line(x0, y0, x0 + leg_len, y0, 5.0), dxfattribs={"layer": lyr})
                msp.add_arc((x0 + leg_len, y0 + radius_m), radius_m, -90.0, 90.0, dxfattribs={"layer": lyr})
                msp.add_lwpolyline(
                    _make_segmented_line(x0 + leg_len - 0.20, y0 + 2.0 * radius_m, x0, y0 + 2.0 * radius_m, 5.0),
                    dxfattribs={"layer": lyr},
                )
            elif mode == "transverse_gap_multi_vertex":
                msp.add_lwpolyline(_make_segmented_line(x0, y0, x0 + leg_len, y0, 5.0), dxfattribs={"layer": lyr})
                msp.add_arc((x0 + leg_len, y0 + radius_m), radius_m, -90.0, 90.0, dxfattribs={"layer": lyr})
                msp.add_lwpolyline(
                    _make_segmented_line(x0 + leg_len - 0.05, y0 + 2.0 * radius_m + 0.18, x0, y0 + 2.0 * radius_m + 0.18, 5.0),
                    dxfattribs={"layer": lyr},
                )
            elif mode == "degree3_spur_at_pt":
                msp.add_lwpolyline(_make_segmented_line(x0, y0, x0 + leg_len, y0, 5.0), dxfattribs={"layer": lyr})
                msp.add_arc((x0 + leg_len, y0 + radius_m), radius_m, -90.0, 90.0, dxfattribs={"layer": lyr})
                msp.add_lwpolyline(
                    _make_segmented_line(x0 + leg_len, y0 + 2.0 * radius_m, x0, y0 + 2.0 * radius_m, 5.0),
                    dxfattribs={"layer": lyr},
                )
                msp.add_line(
                    (x0 + leg_len, y0 + 2.0 * radius_m),
                    (x0 + leg_len + 9.0, y0 + 2.0 * radius_m + 6.0),
                    dxfattribs={"layer": lyr},
                )

            path = _save_temp_dxf(doc)
            try:
                res = cad_bridge.parse_road(path, road_width=6.0, central_meridian=107.75, scale_factor=0.9999)
                assert res.get("success") is True, f"Failed on R={radius_m} mode={mode}: {res}"
                got_len = res["totalLengthMeters"]
                cl_cad = res["centerlineCad"]
                ls = LineString(cl_cad)
                assert ls.is_simple, f"Hairpin R={radius_m} mode={mode} produced non-simple LineString"
                assert abs(got_len - expected_len) <= 1.0, (
                    f"Hairpin truncated! R={radius_m}m mode={mode}: expected ~{expected_len:.2f}m, got {got_len:.2f}m"
                )
            finally:
                os.remove(path)

    # Double-hairpin mountain S-switchback (3 legs + 2 narrow R=4.5m hairpins with 0.20m gaps)
    doc_sw = ezdxf.new("R2010")
    msp_sw = doc_sw.modelspace()
    R_sw = 4.5
    msp_sw.add_lwpolyline(_make_segmented_line(x0, y0, x0 + leg_len, y0, 5.0), dxfattribs={"layer": "TIM_TUYEN"})
    msp_sw.add_arc((x0 + leg_len, y0 + R_sw), R_sw, -90.0, 90.0, dxfattribs={"layer": "TIM_TUYEN"})
    msp_sw.add_lwpolyline(
        _make_segmented_line(x0 + leg_len - 0.20, y0 + 2 * R_sw, x0, y0 + 2 * R_sw, 5.0),
        dxfattribs={"layer": "TIM_TUYEN"},
    )
    msp_sw.add_arc((x0, y0 + 3 * R_sw), R_sw, 90.0, 270.0, dxfattribs={"layer": "TIM_TUYEN"})
    msp_sw.add_lwpolyline(
        _make_segmented_line(x0 + 0.20, y0 + 4 * R_sw, x0 + leg_len, y0 + 4 * R_sw, 5.0),
        dxfattribs={"layer": "TIM_TUYEN"},
    )
    p_sw = _save_temp_dxf(doc_sw)
    try:
        res_sw = cad_bridge.parse_road(p_sw, road_width=6.0, central_meridian=107.75, scale_factor=0.9999)
        exp_sw = 3.0 * leg_len + 2.0 * math.pi * R_sw
        assert abs(res_sw["totalLengthMeters"] - exp_sw) <= 1.5, (
            f"Double switchback truncated: expected ~{exp_sw:.2f}m, got {res_sw['totalLengthMeters']:.2f}m"
        )
        assert LineString(res_sw["centerlineCad"]).is_simple
    finally:
        os.remove(p_sw)

    # Sharp 90-deg and 115-deg corners (non-U-turn) with 0.15m snap gap
    for turn_deg in [90.0, 115.0]:
        doc_c = ezdxf.new("R2010")
        msp_c = doc_c.modelspace()
        rad = math.radians(turn_deg)
        msp_c.add_lwpolyline(_make_segmented_line(x0, y0, x0 + 100.0, y0, 5.0), dxfattribs={"layer": "TIM_TUYEN"})
        msp_c.add_lwpolyline(
            _make_segmented_line(
                x0 + 100.0 + 0.15,
                y0,
                x0 + 100.0 + 0.15 + 100.0 * math.cos(rad),
                y0 + 100.0 * math.sin(rad),
                5.0,
            ),
            dxfattribs={"layer": "TIM_TUYEN"},
        )
        p_c = _save_temp_dxf(doc_c)
        try:
            res_c = cad_bridge.parse_road(p_c, road_width=6.0, central_meridian=107.75, scale_factor=0.9999)
            assert abs(res_c["totalLengthMeters"] - 200.0) <= 1.0, (
                f"Sharp {turn_deg}deg corner truncated: got {res_c['totalLengthMeters']:.2f}m"
            )
            assert LineString(res_c["centerlineCad"]).is_simple
        finally:
            os.remove(p_c)

    print("  -> PASS: 30/30 narrow/wide hairpins, double switchback, and sharp 90°/115° corners preserved 100%.")


def test_pavement_endcap_uturn_blocked():
    print("[2/5] Testing rectangular pavement end-cap U-turn rejection...")
    doc = ezdxf.new("R2010")
    msp = doc.modelspace()
    x0, y0 = 580000.0, 1270000.0
    msp.add_lwpolyline(_make_segmented_line(x0, y0, x0 + 150.0, y0, 5.0), dxfattribs={"layer": "YEUTOCONGTUYEN"})
    msp.add_line((x0 + 150.0, y0), (x0 + 150.0, y0 + 6.0), dxfattribs={"layer": "YEUTOCONGTUYEN"})
    msp.add_lwpolyline(_make_segmented_line(x0 + 149.70, y0 + 6.0, x0, y0 + 6.0, 5.0), dxfattribs={"layer": "YEUTOCONGTUYEN"})
    p = _save_temp_dxf(doc)
    try:
        res = cad_bridge.parse_road(p, road_width=7.0, central_meridian=107.75, scale_factor=0.9999)
        assert res["totalLengthMeters"] <= 160.0, (
            f"End-cap U-turn failed to block opposite parallel edge: got {res['totalLengthMeters']:.2f}m"
        )
        assert LineString(res["centerlineCad"]).is_simple
    finally:
        os.remove(p)
    print(f"  -> PASS: End-cap U-turn rejected cleanly (length={res['totalLengthMeters']:.2f}m, simple=True).")


def test_figure8_self_intersection_blocked():
    print("[3/5] Testing Figure-8 self-intersecting DXF preservation & RoadGuard Draft blocking...")
    doc = ezdxf.new("R2010")
    msp = doc.modelspace()
    x0, y0 = 580000.0, 1270000.0
    pts = [
        (x0, y0),
        (x0 + 120.0, y0 + 120.0),
        (x0 + 120.0, y0),
        (x0, y0 + 120.0),
    ]
    msp.add_lwpolyline(pts, dxfattribs={"layer": "TIM_TUYEN"})
    p = _save_temp_dxf(doc)
    try:
        res = cad_bridge.parse_road(p, road_width=7.0, central_meridian=107.75, scale_factor=0.9999)
        assert res.get("success") is True
        ls_cad = LineString(res["centerlineCad"])
        assert ls_cad.is_simple is False, "Figure-8 CAD self-intersection must NOT be hidden by parse_road"
        draft_res = _call_draft_builder(res["centerline"], source_crs=32648, road_code="FIG8_TEST")
        assert draft_res.get("success") is False and draft_res.get("error") == "InvalidGeometry", (
            f"Expected InvalidGeometry on Figure-8 export, got: {draft_res}"
        )
    finally:
        os.remove(p)
    print("  -> PASS: Figure-8 DXF preserves self-intersection and is blocked with InvalidGeometry.")


def test_repo_valid_dxfs():
    print("[4/5] Testing valid repository DXF fixtures...")
    repo_dxfs = [
        (REPO_ROOT / "3. BDTK.dxf", 105.75),
        (REPO_ROOT / "ROAD_CENTERLINE.dxf", 105.75),
        (REPO_ROOT / "sample_road.dxf", 105.75),
    ]
    for dxf_path, cm in repo_dxfs:
        assert dxf_path.exists(), f"Missing repo DXF: {dxf_path}"
        res = cad_bridge.parse_road(str(dxf_path), central_meridian=cm, scale_factor=0.9999)
        assert res.get("success") is True, f"Failed on {dxf_path.name}: {res}"
        ls = LineString(res["centerlineCad"])
        assert ls.is_simple, f"Non-simple centerline on valid repo DXF {dxf_path.name}"
        coord_state = res.get("crsState")
        if coord_state in ("GEOREFERENCED_VN2000", "GEOGRAPHIC_WGS84") and len(res.get("centerline", [])) >= 2:
            draft = _call_draft_builder(res["centerline"], source_crs=32648, road_code="REG_VALID")
            assert draft.get("success") is True, f"Draft export failed on {dxf_path.name}: {draft}"
            print(
                f"  -> {dxf_path.name}: state={coord_state}, layer={res['centerlineLayerName']}, "
                f"pts={draft['pointCount']}, vn2000_len={res['totalLengthMeters']}m, utm_len={draft['utmLengthMeters']:.2f}m, draft=PASS"
            )
        else:
            print(
                f"  -> {dxf_path.name}: state={coord_state}, layer={res['centerlineLayerName']}, "
                f"cad_pts={len(res['centerlineCad'])}, cad_len={res['totalLengthMeters']}m, simple=True"
            )

    extra_downloads = [
        (pathlib.Path(r"C:\Users\dell\Downloads\3. BINH DO CHIEU SANG.dxf"), 105.75),
        (pathlib.Path(r"C:\Users\dell\Downloads\binh do dieu chinh lan 1.dxf"), 107.75),
        (pathlib.Path(r"C:\Users\dell\Downloads\cadmapper-bao-loc-lam-ong-vn.dxf"), 108.25),
    ]
    for dxf_path, cm in extra_downloads:
        if not dxf_path.exists():
            continue
        res = cad_bridge.parse_road(str(dxf_path), central_meridian=cm, scale_factor=0.9999)
        assert res.get("success") is True, f"Failed on {dxf_path.name}: {res}"
        assert LineString(res["centerlineCad"]).is_simple, f"Non-simple centerline on {dxf_path.name}"
        if len(res.get("centerline", [])) >= 2:
            draft = _call_draft_builder(res["centerline"], source_crs=32648, road_code="REG_DL")
            assert draft.get("success") is True, f"Draft export failed on {dxf_path.name}: {draft}"
            print(
                f"  -> [Downloads] {dxf_path.name}: layer={res['centerlineLayerName']}, "
                f"pts={draft['pointCount']}, len={res['totalLengthMeters']}m, utm_len={draft['utmLengthMeters']:.2f}m, draft=PASS"
            )


def test_optional_baoloc_dxf():
    baoloc_path = pathlib.Path(r"C:\Users\dell\Downloads\BINH DO THIET KE PK.dxf")
    if not baoloc_path.exists():
        print("[5/5] Optional Bảo Lộc DXF not present in Downloads; skipping local fixture check.")
        return

    print("[5/5] Testing Bảo Lộc DXF (ENTPLINETUYEN auto-proposal + forced YEUTOCONGTUYEN trace)...")
    res_auto = cad_bridge.parse_road(str(baoloc_path), road_width=7.0, central_meridian=107.75, scale_factor=0.9999)
    assert res_auto["centerlineLayerName"] == "ENTPLINETUYEN", f"Expected ENTPLINETUYEN, got {res_auto['centerlineLayerName']}"
    assert len(res_auto["centerline"]) == 112, f"Expected 112 vertices, got {len(res_auto['centerline'])}"
    assert LineString(res_auto["centerlineCad"]).is_simple

    draft_auto = _call_draft_builder(res_auto["centerline"], source_crs=32648, road_code="Demo01")
    assert draft_auto.get("success") is True
    assert draft_auto["pointCount"] == 112
    assert abs(draft_auto["utmLengthMeters"] - 700.53) < 0.15, f"Expected ~700.53m UTM, got {draft_auto['utmLengthMeters']}"

    res_yeuto = cad_bridge.parse_road(
        str(baoloc_path),
        road_width=7.0,
        target_layer="YEUTOCONGTUYEN",
        central_meridian=107.75,
        scale_factor=0.9999,
    )
    assert res_yeuto["centerlineLayerName"] == "YEUTOCONGTUYEN"
    assert LineString(res_yeuto["centerlineCad"]).is_simple
    draft_yeuto = _call_draft_builder(res_yeuto["centerline"], source_crs=32648, road_code="Demo01")
    assert draft_yeuto.get("success") is True
    print(
        f"  -> Auto ENTPLINETUYEN: pts={draft_auto['pointCount']}, vn2000_len={res_auto['totalLengthMeters']}m, utm_len={draft_auto['utmLengthMeters']:.6f}m, PASS\n"
        f"  -> Forced YEUTOCONGTUYEN: pts={draft_yeuto['pointCount']}, vn2000_len={res_yeuto['totalLengthMeters']}m, utm_len={draft_yeuto['utmLengthMeters']:.6f}m, PASS (left pavement edge 37BAD + 4EC1E)"
    )


if __name__ == "__main__":
    test_valid_hairpins_and_sharp_curves()
    test_pavement_endcap_uturn_blocked()
    test_figure8_self_intersection_blocked()
    test_repo_valid_dxfs()
    test_optional_baoloc_dxf()
    print("\nALL CENTERLINE INTEGRITY & HAIRPIN REGRESSION TESTS PASSED!")
