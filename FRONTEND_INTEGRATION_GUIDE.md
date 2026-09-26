# T?I LI?U T?CH H?P BACKEND - FRONTEND (ROADGUARD CAD & GIS ENGINE)
> **D?nh cho:** Team Frontend (Web / Mobile / GIS UI)  
> **Backend Service:** `RoadGuard.CadParser` (ASP.NET Core 8 + Python GIS Bridge)  
> **M?i tr??ng Dev:** `http://localhost:5198`  
> **Swagger UI:** `http://localhost:5198/index.html`  
> **Ng?y c?p nh?t:** 26/09/2026 (Phi?n b?n Hybrid Overlay v2.3)

---

## 1. T?NG QUAN GI?I PH?P HYBRID (RASTER + VECTOR GEOJSON)

Khi l?m vi?c v?i c?c b?n v? CAD giao th?ng (`.dxf`), m?t file th??ng ch?a t? **v?i ch?c ngh?n ??n h?ng tr?m ngh?n n?t v?** (???ng gi?ng, khung t?n, m?t c?t ngang, tr?c d?c, cao ??...). N?u ??y to?n b? vector th? n?y xu?ng Frontend ?? v? b?ng Canvas/SVG:
* Tr?nh duy?t s? b? **gi?t lag, ?? m?n h?nh ho?c crash b? nh?**.
* C?c ???ng tr?c d?c, m?t c?t n?m ? t?a ?? xa l?m b?n ?? b? thu nh? ho?c sai l?ch v? tr? ??a l?.

### Gi?i ph?p Backend ?? hi?n th?c:
Backend cung c?p ki?n tr?c **Hybrid 2-trong-1 (T?ch h?p trong 1 API duy nh?t)**:
1. **?nh Raster Overlay 2D (`image_base64`)**: To?n b? b?n v? k? thu?t ???c Backend render th?nh 1 t?m ?nh PNG **n?n trong su?t (Transparent)**, ???c ?p d?ng theme ?? h?a **"M?t ???ng b? t?ng th?c t?"** (M?t ???ng x?m b? t?ng #808080 ??c 80%, tim tuy?n v?ng highway #FFD700 d?y n?i b?t, m?p ???ng tr?ng tinh #FFFFFF). Frontend ch? vi?c d?n 1 t?m ?nh n?y l?n b?n ?? v? tinh b?ng `L.imageOverlay` ho?c `map.addImage`, nh? nh?ng v? ??t 60fps m??t m?.
2. **D? li?u Vector GeoJSON (`geojson`)**: Backend b?c t?ch ri?ng c?c n?t c?t l?i n?m trong h?nh lang tuy?n (**Tim tuy?n, M?p ???ng, B? v?a, L? ???ng, V?ch s?n**), ?? ???c **gi?i thu?t n?i suy cong m??t m? (tessellation)** v? chuy?n sang h? t?a ?? qu?c t? **WGS-84 (RFC 7946)**. Frontend d?ng t?p GeoJSON si?u nh? n?y ??:
   - Click ch?n ?o?n ???ng (Segment Selection).
   - ?o ??c kho?ng c?ch, l? tr?nh (Chainage Km).
   - Hi?n th? th?ng tin ph?n ?o?n g?n nh?n h? h?ng m?t ???ng (Pavement Distress).

---

## 2. CHI TI?T API T?CH H?P B?T BU?C (API CH?NH)

### `POST /api/cad/render-overlay`
??y l? API ch?nh Frontend c?n g?i sau khi ng??i d?ng upload file CAD `.dxf`.

#### Request (Multipart Form-Data):
| Field | Ki?u d? li?u | B?t bu?c | M?c ??nh | M? t? |
| :--- | :--- | :---: | :---: | :--- |
| `file` | File (`.dxf`) | **C?** | - | File CAD DXF b?n v? tuy?n ???ng |
| `roadWidth` | `double` | Kh?ng | `7.0` | Chi?u r?ng m?t ???ng (m?t) ?? render d?i b? t?ng 2 b?n tim tuy?n |
| `outputSizePx` | `int` | Kh?ng | `2048` | ?? ph?n gi?i c?nh d?i c?a ?nh PNG xu?t ra (v? d? 1024, 2048, 4096) |
| `centerlineLayerName` | `string` | Kh?ng | `null` | T?n layer tim tuy?n n?u mu?n ch? ??nh (n?u b? tr?ng Backend t? nh?n di?n qua t? kh?a `ENTPLINETUYEN`, `TIMTUYEN`, `TIM_TUYEN`...) |

#### Response Format (JSON T?ch h?p chu?n):
```json
{
  "bounds": [
    [10.868975318570724, 105.85121900976873], 
    [10.889322768638527, 105.86598974052092]
  ],
  "image_base64": "iVBORw0KGgoAAAANSUhEUgAA...",
  "geojson": {
    "type": "FeatureCollection",
    "features": [
      {
        "type": "Feature",
        "properties": {
          "layer": "ENTPLINETUYEN",
          "type": "Centerline",
          "segment": "CL-001",
          "elevation": 0.0
        },
        "geometry": {
          "type": "LineString",
          "coordinates": [
            [105.8531180167472, 10.887495542129889],
            [105.8530180211124, 10.887522238491823],
            [105.85281808540142, 10.887575767004561]
          ]
        }
      },
      {
        "type": "Feature",
        "properties": {
          "layer": "MEPNHUA",
          "type": "RoadEdge",
          "segment": "EDGE-014",
          "elevation": 0.0
        },
        "geometry": {
          "type": "LineString",
          "coordinates": [
            [105.85280691023904, 10.887693686869504],
            [105.85279123412093, 10.887710129482112],
            [105.85274512938122, 10.887758921837423]
          ]
        }
      }
    ]
  },
  "success": true,
  "style": "concrete_road",
  "cropped_to_layer": "ENTPLINETUYEN",
  "bbox_wgs84": {
    "south": 10.868975318570724,
    "west": 105.85121900976873,
    "north": 10.889322768638527,
    "east": 105.86598974052092
  }
}
```

---

## 3. GI?I TH?CH ? NGH?A C?C TR??NG D? LI?U

### 1. `bounds` (Bounding Box cho Leaflet / OpenLayers):
* ??nh d?ng: `[[min_lat, min_lon], [max_lat, max_lon]]` t??ng ?ng `[[south, west], [north, east]]`.
* Kh?p 100% v?i tham s? kh?i t?o `L.latLngBounds(bounds)` trong Leaflet.
* Khi truy?n v?o `L.imageOverlay(imageUrl, bounds)`, ?nh m?t ???ng s? t? ??ng r?i v?o ??ng v? tr? th?c t? tr?n ?nh v? tinh Google / ESRI m? kh?ng b? l?ch hay m?o.

### 2. `image_base64`:
* Chu?i base64 c?a file PNG ??nh d?ng RGBA c? n?n trong su?t (alpha = 0).
* C?ch d?ng tr?n Frontend: `const imageUrl = `data:image/png;base64,${data.image_base64}`;`.
* M?t ???ng ?? ???c ?? h?a t? ??ng: d?i b? t?ng x?m ?m tr?n tuy?n, tim ???ng v?ng, m?p ???ng tr?ng, ?n s?ch c?c layer r?c (DIM, TEXT, Khung t?n, tr?c d?c).

### 3. `geojson`:
* C?u tr?c `FeatureCollection` chu?n qu?c t? RFC 7946.
* T?a ?? trong `geometry.coordinates` l? `[longitude, latitude]` theo chu?n GIS.
* **?? n?i suy ???ng cong m??t m?**: C?c ?o?n cua, kh?c l??n (LWPOLYLINE bulge v? ARC) ?? ???c Backend b?m th?nh 10?36 t?a ?? li?n ti?p. Kh?ng c?n t?nh tr?ng ???ng cong b? bi?n th?nh 1 ???ng th?ng t?p.
* M?i `Feature` c? thu?c t?nh `properties`:
  - `layer`: T?n layer g?c trong CAD (`ENTPLINETUYEN`, `MEPNHUA`, `BO_VIA`, `LE_DUONG`, `VACH_SON`...).
  - `type`: Lo?i ??i t??ng (`Centerline`, `RoadEdge`, `Curb`, `Shoulder`, `Marking`, `CorridorLine`).
  - `segment`: M? ??nh danh ?o?n (`CL-001`, `EDGE-014`...).
  - `elevation`: Cao ?? thi?t k? n?u c?.

---

## 4. C?C API PH? TR? KH?C

### 1. `POST /api/cad/get-layers`
D?ng khi mu?n ng??i d?ng xem tr??c v? t? ch?n layer tim tuy?n tr??c khi render.
* **Request**: FormData `{ file: <dxf_file> }`
* **Response**:
```json
{
  "success": true,
  "layers": [
    "0",
    "BO_VIA",
    "ENTPLINETUYEN",
    "LE_DUONG",
    "MEPNHUA",
    "VACH_7_1"
  ]
}
```

### 2. `POST /api/cad/parse-upload`
D?ng khi ch? mu?n b?c t?ch to?n b? vector l?u Database SQL Server ho?c l?y ph?n t?ch k? thu?t theo ti?u chu?n TCVN 10380:2014.
* **Request**: FormData `{ file: <dxf_file>, srid: 4326, layerName: "ENTPLINETUYEN" }`
* **Response**: Bao g?m `FeatureCollection`, `Analytics` (t?ng chi?u d?i m?t, di?n t?ch m?t ???ng m?, s? t?m b? t?ng ??c t?nh, s? ph?n ?o?n...).

---

## 5. CODE M?U T?CH H?P TR?C TI?P CHO FRONTEND

### C?ch 1: React + Leaflet (`react-leaflet` ho?c Vanilla Leaflet)

```typescript
import React, { useState } from 'react';
import { MapContainer, TileLayer, ImageOverlay, GeoJSON, useMap } from 'react-leaflet';
import L from 'leaflet';
import 'leaflet/dist/leaflet.css';

interface CadOverlayData {
  bounds: [[number, number], [number, number]]; // [[min_lat, min_lon], [max_lat, max_lon]]
  image_base64: string;
  geojson: any;
}

// Component t? ??ng zoom v?a v?n tuy?n ???ng sau khi load
function AutoFitBounds({ bounds }: { bounds: [[number, number], [number, number]] }) {
  const map = useMap();
  React.useEffect(() => {
    if (bounds && bounds.length === 2) {
      map.fitBounds(bounds, { padding: [40, 40] });
    }
  }, [bounds, map]);
  return null;
}

export function RoadCadViewer() {
  const [data, setData] = useState<CadOverlayData | null>(null);
  const [loading, setLoading] = useState(false);

  const handleUploadDxf = async (event: React.ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    if (!file) return;

    setLoading(true);
    const formData = new FormData();
    formData.append('file', file);
    formData.append('roadWidth', '7.0');      // B? r?ng d?i ???ng b? t?ng (m)
    formData.append('outputSizePx', '2048');  // ?? ph?n gi?i ?nh

    try {
      const response = await fetch('http://localhost:5198/api/cad/render-overlay', {
        method: 'POST',
        body: formData,
      });

      if (!response.ok) {
        throw new Error(`Upload failed with status ${response.status}`);
      }

      const result: CadOverlayData = await response.json();
      setData(result);
    } catch (err) {
      console.error('Error rendering CAD overlay:', err);
      alert('Kh?ng th? render b?n v? CAD!');
    } finally {
      setLoading(false);
    }
  };

  // Style cho c?c ???ng Vector GeoJSON khi t??ng t?c
  const geoJsonStyle = (feature: any) => {
    const type = feature?.properties?.type;
    switch (type) {
      case 'Centerline':
        return { color: '#FFD700', weight: 4, opacity: 0.9 }; // Tim tuy?n: V?ng
      case 'RoadEdge':
        return { color: '#FFFFFF', weight: 2, opacity: 0.8 }; // M?p ???ng: Tr?ng
      case 'Curb':
        return { color: '#38BDF8', weight: 2, opacity: 0.7 }; // B? v?a: Xanh nh?t
      default:
        return { color: '#94A3B8', weight: 1, opacity: 0.5 };
    }
  };

  const onEachFeature = (feature: any, layer: L.Layer) => {
    const props = feature.properties || {};
    layer.bindPopup(`
      <div style="font-family: sans-serif; font-size: 13px;">
        <strong style="color: #0284c7;">${props.type || '?o?n tuy?n'}</strong><br/>
        <b>M? ?o?n:</b> ${props.segment || 'N/A'}<br/>
        <b>Layer CAD:</b> ${props.layer || 'N/A'}<br/>
        <b>Cao ??:</b> ${props.elevation || 0} m
      </div>
    `);
  };

  const imageUrl = data ? `data:image/png;base64,${data.image_base64}` : '';

  return (
    <div style={{ position: 'relative', width: '100vw', height: '100vh' }}>
      {/* Thanh c?ng c? Upload */}
      <div style={{ position: 'absolute', top: 16, left: 16, zIndex: 1000, background: 'white', padding: 12, borderRadius: 8, boxShadow: '0 2px 8px rgba(0,0,0,0.15)' }}>
        <input type="file" accept=".dxf" onChange={handleUploadDxf} disabled={loading} />
        {loading && <span style={{ marginLeft: 8 }}>?ang x? l? CAD & Render ?nh m?t ???ng...</span>}
      </div>

      <MapContainer center={[10.87, 105.85]} zoom={15} style={{ width: '100%', height: '100%' }}>
        {/* N?n b?n ?? v? tinh ESRI World Imagery ?? th?y r? m?t ???ng th?c t? */}
        <TileLayer
          attribution='&copy; <a href="https://www.esri.com/">Esri</a>'
          url="https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}"
          maxZoom={19}
        />

        {data && (
          <>
            <AutoFitBounds bounds={data.bounds} />

            {/* 1. L?P ?NH RASTER OVERLAY 2D (Nh?, m??t, th? hi?n m?t ???ng b? t?ng) */}
            <ImageOverlay
              url={imageUrl}
              bounds={data.bounds}
              opacity={0.95}
              zIndex={500}
            />

            {/* 2. L?P VECTOR GEOJSON (?? t??ng t?c click, ?o ??c, g?n nh?n h? h?ng) */}
            {data.geojson && (
              <GeoJSON
                data={data.geojson}
                style={geoJsonStyle}
                onEachFeature={onEachFeature}
              />
            )}
          </>
        )}
      </MapContainer>
    </div>
  );
}
```

---

### C?ch 2: Mapbox GL JS

```typescript
import mapboxgl from 'mapbox-gl';

function addCadOverlayToMapbox(map: mapboxgl.Map, data: CadOverlayData) {
  const [[south, west], [north, east]] = data.bounds;

  // Mapbox image coordinate: [top-left, top-right, bottom-right, bottom-left]
  const coordinates = [
    [west, north], // T?y - B?c
    [east, north], // ??ng - B?c
    [east, south], // ??ng - Nam
    [west, south], // T?y - Nam
  ];

  // 1. Th?m ?nh Raster Overlay
  map.addSource('cad-road-raster', {
    type: 'image',
    url: `data:image/png;base64,${data.image_base64}`,
    coordinates: coordinates,
  });

  map.addLayer({
    id: 'cad-road-layer',
    type: 'raster',
    source: 'cad-road-raster',
    paint: { 'raster-opacity': 0.9 },
  });

  // 2. Th?m L?p Vector GeoJSON
  map.addSource('cad-road-vector', {
    type: 'geojson',
    data: data.geojson,
  });

  map.addLayer({
    id: 'cad-vector-lines',
    type: 'line',
    source: 'cad-road-vector',
    paint: {
      'line-color': [
        'match',
        ['get', 'type'],
        'Centerline', '#FFD700',
        'RoadEdge', '#FFFFFF',
        '#38BDF8',
      ],
      'line-width': 2.5,
    },
  });

  // Zoom v?a v?n bounding box
  map.fitBounds([[west, south], [east, north]], { padding: 50 });
}
```

---

## 6. L?U ? QUAN TR?NG V? T?A ?? (BEST PRACTICES & GOTCHAS)

| ??c ?i?m | Chi ti?t Frontend c?n n?m |
| :--- | :--- |
| **Th? t? t?a ?? trong `bounds`** | `[[min_lat, min_lon], [max_lat, max_lon]]` = `[[v?_??_Nam, kinh_??_T?y], [v?_??_B?c, kinh_??_??ng]]`. D?ng tr?c ti?p cho Leaflet `L.latLngBounds`. |
| **Th? t? t?a ?? trong `geojson`** | Theo chu?n RFC 7946: `[longitude, latitude]` = `[kinh_??, v?_??]`. N?u t? vi?t h?m custom v?, l?u ? kh?ng ??o ng??c t?a ??. |
| **H? t?a ?? ?? chu?n h?a** | Backend ?? chuy?n ??i to?n b? t? h? t?a ?? c?ng tr?nh Vi?t Nam (**VN-2000 kinh tuy?n tr?c ??a ph??ng**) sang h? t?a ?? qu?c t? **WGS-84 (EPSG:4326)**. Frontend kh?ng c?n c?i ??t th? vi?n `proj4` ?? chuy?n ??i n?a. |
| **Hi?u n?ng hi?n th?** | Kh?ng render to?n b? vector n?u ch? c?n quan s?t tuy?n ???ng. Ch? c?n hi?n th? `ImageOverlay` l? ng??i d?ng ?? th?y to?n b? m?t ???ng n?t c?ng v? m??t m?. L?p GeoJSON ch? c?n hi?n th? ???ng tim tuy?n v? m?p ???ng ch?nh. |

---

## 7. LI?N H? & B?O L?I H? TR?
* **Backend Dev:** Huy / Ho?ng
* **Local Test Port:** `http://localhost:5198`
* **File test m?u c? s?n:** `3. BDTK.dxf` (11MB, ?? test th?nh c?ng 207 features m??t m?).
