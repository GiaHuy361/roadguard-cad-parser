#!/usr/bin/env python3
"""
RoadGuard CAD Parser - Python ezdxf Bridge v2.1
================================================
Commands:
  get-layers <dxf>                              List all DXF layers
  parse      <dxf> [--layer L] [--srid N]      Extract centerline GeoJSON
  render     <dxf> [--size N] [--layer L]      Render plan-view to transparent PNG + WGS-84 bounds
"""

import sys
import os
import re
import json
import math
import argparse
import base64
import io

import ezdxf
from ezdxf import path as dxf_path
from ezdxf.math import Matrix44

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from ezdxf.addons.drawing import RenderContext, Frontend
from ezdxf.addons.drawing.matplotlib import MatplotlibBackend
from ezdxf.addons.drawing.properties import LayoutProperties

try:
    from pyproj import Transformer, CRS
    HAS_PYPROJ = True
except ImportError:
    HAS_PYPROJ = False

# =============================================================================
# Config
# =============================================================================
CENTERLINE_KEYWORDS = [
    "ENTPLINETUYEN", "TIM_THIET_KE", "TKDTIMTUYENTKT1", "TKDTIMTUYENTKT2",
    "TIMDUONG", "TIM_DUONG", "ROAD_CENTERLINE", "CENTERLINE",
    "TIMTUYEN", "TIM", "CENTER", "TUYEN", "AXIS", "ROAD"
]
EXCLUDE_KEYWORDS = [
    "SUONTUYEN", "DAUTIM", "TEXT", "DIM", "NOTE", "GHICHU",
    "COC", "LYTRINH", "KHUNG", "TITLE", "BORDER", "HATCHING", "HATCH"
]
ARC_TESSELLATION_STEPS = 64
MIN_CENTERLINE_LENGTH  = 50.0
LINEAR_TYPES = {"LINE", "LWPOLYLINE", "POLYLINE", "ARC", "SPLINE", "CIRCLE"}

# VN-2000 valid range for Vietnam (zones 102, 105, 108)
# Easting:  50_000 - 950_000 m; Northing: 800_000 - 2_600_000 m (8N - 23N)
_VN2000_X_MIN, _VN2000_X_MAX = 50_000.0,   950_000.0
_VN2000_Y_MIN, _VN2000_Y_MAX = 800_000.0, 2_600_000.0


def _is_vn2000(x, y) -> bool:
    return (_VN2000_X_MIN <= x <= _VN2000_X_MAX and
            _VN2000_Y_MIN <= y <= _VN2000_Y_MAX)

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


def project_to_wgs84(points_xy: list) -> list:
    if not points_xy:
        return []
    x0, y0 = points_xy[0]
    if abs(x0) <= 180.0 and abs(y0) <= 90.0:
        return [[x, y] for x, y in points_xy]
    if not HAS_PYPROJ:
        return [_manual_utm(x, y) for x, y in points_xy]
    tf = make_vn2000_transformer(detect_central_meridian(x0))
    lons, lats = tf.transform([p[0] for p in points_xy], [p[1] for p in points_xy])
    return [[lon, lat] for lon, lat in zip(lons, lats)]


def _manual_utm(easting, northing):
    a=6378137.0; f=1/298.257223563; k0=0.9996
    e2=2*f-f*f; e=math.sqrt(e2)
    e1=(1-math.sqrt(1-e2))/(1+math.sqrt(1-e2))
    x=easting-500000.0; y=northing; m=y/k0
    mu=m/(a*(1-e2/4-3*e2**2/64-5*e2**3/256))
    phi1=(mu+(3*e1/2-27*e1**3/32)*math.sin(2*mu)
          +(21*e1**2/16-55*e1**4/32)*math.sin(4*mu)
          +(151*e1**3/96)*math.sin(6*mu))
    sp=math.sin(phi1); cp=math.cos(phi1); tp=math.tan(phi1)
    n1=a/math.sqrt(1-e2*sp**2); t1=tp**2; c1=(e2/(1-e2))*cp**2
    r1=a*(1-e2)/(1-e2*sp**2)**1.5; ep2=e2/(1-e2); d=x/(n1*k0)
    lat=phi1-(n1*tp/r1)*(d**2/2-(5+3*t1+10*c1-4*c1**2-9*ep2)*d**4/24
        +(61+90*t1+298*c1+45*t1**2-252*ep2-3*c1**2)*d**6/720)
    lon0=math.radians(105.0)
    lon=lon0+(d-(1+2*t1+c1)*d**3/6
        +(5-2*c1+28*t1-3*c1**2+8*ep2+24*t1**2)*d**5/120)/cp
    return [math.degrees(lon), math.degrees(lat)]

# =============================================================================
# Geometry helpers
# =============================================================================

def _tessellate_bulge(x0,y0,x1,y1,bulge):
    dx=x1-x0; dy=y1-y0; chord=math.hypot(dx,dy)
    if chord<1e-10: return [(x0,y0)]
    theta=4*math.atan(abs(bulge))
    R=chord/(2*math.sin(theta/2))
    mid_x=(x0+x1)/2; mid_y=(y0+y1)/2
    try: pl=math.sqrt(max(0,R**2-(chord/2)**2))
    except: pl=0.0
    px=-(dy/chord)*pl; py=(dx/chord)*pl
    if bulge<0: px,py=-px,-py
    cx=mid_x+px; cy=mid_y+py
    a_s=math.atan2(y0-cy,x0-cx); a_e=math.atan2(y1-cy,x1-cx)
    if bulge>0:
        if a_e<=a_s: a_e+=2*math.pi
    else:
        if a_e>=a_s: a_e-=2*math.pi
    n=max(8,int(abs(theta)/math.pi*ARC_TESSELLATION_STEPS))
    return [(cx+R*math.cos(a_s+(a_e-a_s)*i/n),
             cy+R*math.sin(a_s+(a_e-a_s)*i/n)) for i in range(n+1)]


def _tessellate_arc(cx,cy,r,sd,ed,steps=ARC_TESSELLATION_STEPS):
    s=math.radians(sd); e=math.radians(ed)
    if e<=s: e+=2*math.pi
    d=e-s
    return [(cx+r*math.cos(s+d*i/steps), cy+r*math.sin(s+d*i/steps))
            for i in range(steps+1)]


def entity_to_wcs_points(entity, transform=None):
    t=entity.dxftype(); pts=[]
    try:
        if t=="LINE":
            pts=[(entity.dxf.start.x,entity.dxf.start.y),
                 (entity.dxf.end.x,entity.dxf.end.y)]
        elif t=="LWPOLYLINE":
            raw=list(entity.get_points("xyb"))
            if not raw: raw=[(x,y,0.0) for x,y in entity.get_points("xy")]
            for i in range(len(raw)):
                x0s,y0s,bl=raw[i][0],raw[i][1],raw[i][2]
                if i+1<len(raw):
                    x1s,y1s=raw[i+1][0],raw[i+1][1]
                    if abs(bl)<1e-9: pts.append((x0s,y0s))
                    else:
                        ap=_tessellate_bulge(x0s,y0s,x1s,y1s,bl)
                        pts.extend(ap[:-1])
                else: pts.append((x0s,y0s))
            if raw: pts.append((raw[-1][0],raw[-1][1]))
            if entity.is_closed and pts and pts[0]!=pts[-1]: pts.append(pts[0])
        elif t=="POLYLINE":
            pts=[(v.dxf.location.x,v.dxf.location.y) for v in entity.vertices]
        elif t=="ARC":
            pts=_tessellate_arc(entity.dxf.center.x,entity.dxf.center.y,
                                entity.dxf.radius,entity.dxf.start_angle,entity.dxf.end_angle)
        elif t=="SPLINE":
            p=dxf_path.make_path(entity); v=list(p.flattening(distance=0.5))
            pts=[(vi.x,vi.y) for vi in v]
        elif t=="CIRCLE":
            pts=_tessellate_arc(entity.dxf.center.x,entity.dxf.center.y,
                                entity.dxf.radius,0,360)
    except: return []
    if len(pts)<2: return []
    if transform is not None:
        return [(transform.transform((x,y,0))[0], transform.transform((x,y,0))[1])
                for x,y in pts]
    return pts


def walk_entities(entities, doc, transform=None, depth=0):
    if depth>8: return
    for entity in entities:
        et=entity.dxftype()
        if et=="INSERT":
            try:
                im=entity.matrix44()
                combined=(transform @ im) if transform is not None else im
                bn=entity.dxf.name
                if bn in doc.blocks:
                    yield from walk_entities(list(doc.blocks[bn]),doc,combined,depth+1)
            except: continue
        elif et in LINEAR_TYPES:
            yield (entity, transform)

# =============================================================================
# Layer discovery
# =============================================================================

def get_layers(dxf_path_str):
    doc=ezdxf.readfile(dxf_path_str)
    layers=set(l.dxf.name for l in doc.layers if l.dxf.name)
    for e in doc.modelspace():
        lyr=e.dxf.get("layer","")
        if lyr: layers.add(lyr)
    return sorted(layers, key=lambda x:x.lower())

# =============================================================================
# Detect best centerline layer (shared helper)
# =============================================================================

def _detect_centerline_layer(msp, doc):
    layer_raw={}
    for entity,xform in walk_entities(msp,doc):
        lyr=entity.dxf.get("layer","0")
        pts=entity_to_wcs_points(entity,xform)
        if pts and len(pts)>=2:
            layer_raw.setdefault(lyr,[]).append(pts)
    lengths={}
    for l,segs in layer_raw.items():
        upper=l.upper()
        if any(ex in upper for ex in EXCLUDE_KEYWORDS): continue
        if not any(kw.upper() in upper for kw in CENTERLINE_KEYWORDS): continue
        total=sum(math.hypot(seg[i][0]-seg[i-1][0],seg[i][1]-seg[i-1][1])
                  for seg in segs for i in range(1,len(seg)))
        if total>=MIN_CENTERLINE_LENGTH: lengths[l]=total
    if lengths:
        return max(lengths,key=lengths.get), layer_raw
    if layer_raw:
        return max(layer_raw,key=lambda l:len(layer_raw[l])), layer_raw
    return None, layer_raw

# =============================================================================
# Extract geometry
# =============================================================================

def extract_geometries(dxf_path_str, target_layer=None, srid=4326):
    doc=ezdxf.readfile(dxf_path_str)
    msp=doc.modelspace()
    all_layers=set(l.dxf.name for l in doc.layers if l.dxf.name)

    chosen_layer=None
    if target_layer:
        # Build layer_raw quickly to check
        layer_raw={}
        for entity,xform in walk_entities(msp,doc):
            lyr=entity.dxf.get("layer","0")
            all_layers.add(lyr)
            pts=entity_to_wcs_points(entity,xform)
            if pts and len(pts)>=2: layer_raw.setdefault(lyr,[]).append(pts)
        if target_layer in layer_raw: chosen_layer=target_layer
        else:
            for l in layer_raw:
                if l.lower()==target_layer.lower(): chosen_layer=l; break
        if not chosen_layer:
            auto,_=_detect_centerline_layer(msp,doc)
            chosen_layer=auto
    else:
        auto,lr=_detect_centerline_layer(msp,doc)
        chosen_layer=auto
        layer_raw=lr
        for segs in lr.values():
            pass  # all_layers already built inside

    # Re-collect all_layers if we skipped the main scan
    if not target_layer:
        for entity,_ in walk_entities(msp,doc):
            all_layers.add(entity.dxf.get("layer","0"))

    if not chosen_layer:
        return {"success":False,"error":"NoLinearEntities",
                "detail":"No valid geometry found.","layers":sorted(all_layers)}

    # Re-build layer_raw for chosen layer if needed
    if "layer_raw" not in dir():
        layer_raw={}
        for entity,xform in walk_entities(msp,doc):
            lyr=entity.dxf.get("layer","0")
            pts=entity_to_wcs_points(entity,xform)
            if pts and len(pts)>=2: layer_raw.setdefault(lyr,[]).append(pts)

    features_out=[]
    for seg in layer_raw.get(chosen_layer,[]):
        if len(seg)<2: continue
        projected=project_to_wgs84(seg)
        features_out.append({"layer":chosen_layer,"coordinates":projected})

    return {
        "success":True,
        "detectedLayer":chosen_layer,
        "totalEntities":len(features_out),
        "layers":sorted(all_layers,key=str.lower),
        "features":features_out
    }

# =============================================================================
# Render: DXF plan-view -> transparent PNG + WGS-84 bounds
# =============================================================================

def compute_layer_bbox(doc, layer_name):
    """Compute WCS bbox for entities on a specific layer (VN-2000 filtered)."""
    msp=doc.modelspace()
    xs,ys=[],[]
    for entity,xform in walk_entities(msp,doc):
        if entity.dxf.get("layer","")!=layer_name: continue
        for x,y in entity_to_wcs_points(entity,xform):
            if _is_vn2000(x,y): xs.append(x); ys.append(y)
    if not xs: return None
    return min(xs),min(ys),max(xs),max(ys)


def compute_wcs_bbox(doc):
    """Full VN-2000-filtered bbox across all entities (fallback)."""
    msp=doc.modelspace()
    xs,ys=[],[]
    for entity,xform in walk_entities(msp,doc):
        for x,y in entity_to_wcs_points(entity,xform):
            if _is_vn2000(x,y): xs.append(x); ys.append(y)
    if not xs: return None
    return min(xs),min(ys),max(xs),max(ys)


def bbox_to_wgs84_leaflet(min_x,min_y,max_x,max_y):
    proj=project_to_wgs84([(min_x,min_y),(max_x,max_y)])
    min_lon,min_lat=proj[0]; max_lon,max_lat=proj[1]
    return [[min_lat,min_lon],[max_lat,max_lon]]


def render_dxf_to_png(dxf_path_str, output_size_px=4096,
                      background_alpha=0.0, line_color_override=None,
                      target_layer=None):
    """
    Render plan-view area of DXF to transparent PNG.
    Bounding box is derived from CENTERLINE LAYER only (not full drawing),
    so profile sheets / cross-sections are cropped out of the overlay.

    Returns:
      {
        "success": true,
        "detectedLayer": "ENTPLINETUYEN",
        "bounds": [[min_lat, min_lon], [max_lat, max_lon]],  <- Leaflet format
        "bbox_wgs84": {"west":..,"south":..,"east":..,"north":..},
        "bbox_wcs": [min_x, min_y, max_x, max_y],
        "image_base64": "<base64 PNG>",
        "image_size_px": [w, h]
      }
    """
    doc=ezdxf.readfile(dxf_path_str)
    msp=doc.modelspace()

    # ?? Step 1: Find centerline layer for bbox ????????????????????????????????
    chosen_layer=target_layer
    if not chosen_layer:
        chosen_layer,_=_detect_centerline_layer(msp,doc)

    # ?? Step 2: Bbox from centerline layer (precise plan-view crop) ???????????
    bbox=None
    if chosen_layer:
        bbox=compute_layer_bbox(doc,chosen_layer)
    if bbox is None:
        bbox=compute_wcs_bbox(doc)   # fallback: full VN-2000 scan
    if bbox is None:
        return {"success":False,"error":"EmptyDrawing",
                "detail":"No VN-2000 geometry found."}

    min_x,min_y,max_x,max_y=bbox
    w=max_x-min_x; h=max_y-min_y
    if w<1e-6 or h<1e-6:
        return {"success":False,"error":"ZeroExtents",
                "detail":f"Extents too small: {w:.2f} x {h:.2f}"}

    # Add 15% margin around the road
    margin=max(w,h)*0.15
    min_x-=margin; min_y-=margin; max_x+=margin; max_y+=margin
    w=max_x-min_x; h=max_y-min_y

    # ?? Step 3: WGS-84 bounds ??????????????????????????????????????????????????
    bounds_leaflet=bbox_to_wgs84_leaflet(min_x,min_y,max_x,max_y)

    # ?? Step 4: matplotlib render ?????????????????????????????????????????????
    aspect=w/h
    if aspect>=1.0:
        fw=output_size_px; fh=max(256,int(output_size_px/aspect))
    else:
        fh=output_size_px; fw=max(256,int(output_size_px*aspect))

    DPI=150
    fig=plt.figure(figsize=(fw/DPI,fh/DPI),dpi=DPI)
    fig.patch.set_alpha(background_alpha)
    ax=fig.add_axes([0,0,1,1])
    ax.set_aspect("equal"); ax.axis("off"); ax.patch.set_alpha(background_alpha)

    ctx=RenderContext(doc)
    lp=LayoutProperties.from_layout(msp)
    lp.set_colors(bg="#ffffff")   # keep original line colors visible
    backend=MatplotlibBackend(ax,adjust_figure=False)
    Frontend(ctx,backend).draw_layout(msp,finalize=True,layout_properties=lp)

    if line_color_override:
        for line in ax.get_lines(): line.set_color(line_color_override)
        for coll in ax.collections:
            try: coll.set_edgecolor(line_color_override)
            except: pass

    # CRITICAL CROP: restrict axes to plan-view extent only
    ax.set_xlim(min_x,max_x)
    ax.set_ylim(min_y,max_y)

    # ?? Step 5: Export transparent PNG ????????????????????????????????????????
    buf=io.BytesIO()
    fig.savefig(buf,format="png",dpi=DPI,transparent=True,
                bbox_inches=None,pad_inches=0)
    plt.close(fig); buf.seek(0)
    image_b64=base64.b64encode(buf.read()).decode("utf-8")

    return {
        "success":       True,
        "detectedLayer": chosen_layer,
        "bounds":        bounds_leaflet,
        "bbox_wgs84":    {"west":bounds_leaflet[0][1],"south":bounds_leaflet[0][0],
                          "east":bounds_leaflet[1][1],"north":bounds_leaflet[1][0]},
        "bbox_wcs":      [min_x,min_y,max_x,max_y],
        "image_base64":  image_b64,
        "image_size_px": [fw,fh],
    }

# =============================================================================
# CLI
# =============================================================================

def main():
    parser=argparse.ArgumentParser(description="RoadGuard CAD ezdxf Bridge v2.1")
    sub=parser.add_subparsers(dest="command")

    lp=sub.add_parser("get-layers"); lp.add_argument("dxf_path")

    pp=sub.add_parser("parse")
    pp.add_argument("dxf_path"); pp.add_argument("--layer",default=None)
    pp.add_argument("--srid",type=int,default=4326)

    rp=sub.add_parser("render")
    rp.add_argument("dxf_path")
    rp.add_argument("--size",type=int,default=4096)
    rp.add_argument("--layer",default=None,help="Force centerline layer for bbox")
    rp.add_argument("--color",default=None,help="Force line color e.g. #00FFFF")

    args=parser.parse_args()
    if not hasattr(args,"dxf_path") or not args.dxf_path:
        parser.print_help(); sys.exit(1)

    if not os.path.exists(args.dxf_path):
        print(json.dumps({"success":False,"error":"FileNotFound",
                           "detail":f"File not found: {args.dxf_path}"})); sys.exit(1)

    try:
        if args.command=="get-layers":
            print(json.dumps({"success":True,"layers":get_layers(args.dxf_path)}))
        elif args.command=="parse":
            print(json.dumps(extract_geometries(args.dxf_path,args.layer,args.srid)))
        elif args.command=="render":
            print(json.dumps(render_dxf_to_png(
                args.dxf_path, output_size_px=args.size,
                line_color_override=args.color,
                target_layer=getattr(args,"layer",None)
            )))
        else:
            parser.print_help(); sys.exit(1)
    except Exception as ex:
        import traceback
        print(json.dumps({"success":False,"error":"BridgeError",
                           "detail":str(ex),"trace":traceback.format_exc()}))
        sys.exit(1)


if __name__=="__main__":
    main()
