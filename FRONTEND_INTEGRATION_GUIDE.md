# T?I LI?U T?CH H?P BACKEND - FRONTEND: B?C T?CH GPS TIM ???NG & M?T ???NG B? T?NG 2D (CHU?N VECTOR GIS)
> **D?nh cho:** Team Frontend (Web / Mobile / GIS UI)  
> **Backend Service:** `RoadGuard.CadParser` (ASP.NET Core 8 + Python GIS Bridge)  
> **M?i tr??ng Dev:** `http://localhost:5198`  
> **Swagger UI:** `http://localhost:5198/index.html`  
> **Ng?y c?p nh?t:** 27/09/2026 (Phi?n b?n Vector GPS v3.0 - B? xu?t ?nh CAD th?)

---

## 1. T?I SAO B? PH??NG ?N XU?T ?NH RASTER CAD V? CHUY?N SANG VECTOR GPS?

Trong th?c t?, c?c b?n v? CAD giao th?ng (`.dxf`) lu?n c? c?c ???ng gi?ng tr?c d?c, m?t c?t ngang v? khung t?n k?o d?i c?ch xa tr?c ???ng h?ng km.
* N?u render to?n b? CAD th?nh ?nh PNG: Bounding Box b? k?o gi?n 2.5km - 3km, khi?n con ???ng th?c t? ch? chi?m 1 g?c nh? li ti, b? co r?t v? bi?n d?ng.
* Th?i gian render ?nh r?t ch?m (40 - 50 gi?y), dung l??ng ?nh base64 n?ng h?ng ch?c MB.

?? **PH??NG ?N VECTOR GPS THU?N (VECTOR / JSON)**:
* Backend **l?c s?ch 100% r?c CAD** (tr?c d?c, m?t c?t, text, dim), ch? t?p trung b?c t?ch **Tim ???ng ch?nh** v? **n?i c?c ?o?n li?n t?c th?nh chu?i t?a ?? m??t m?**.
* T? ??ng m? r?ng tim ???ng sang 2 b?n theo b? r?ng `roadWidth` (m?c ??nh 7.0m, m?i b?n 3.5m) ?? t?nh to?n **?a gi?c m?t ???ng 2D (Road Corridor Polygon)**.
* Chuy?n ??i ch?nh x?c VN-2000 sang **WGS-84 GPS [Latitude, Longitude]**.
* **?u ?i?m v??t tr?i:**
  1. **Si?u nh?:** D? li?u JSON ch? kho?ng **10KB - 20KB** (thay v? ch?c MB ?nh).
  2. **T?c ?? t?c th?:** X? l? xong to?n b? tuy?n ???ng 2.5km ch? trong **1.5 - 3 gi?y**!
  3. **S?c n?t tuy?t ??i:** V? tr?c ti?p b?ng vector c?a Leaflet / Mapbox, ph?ng to thu nh? ? b?t k? m?c zoom n?o c?ng n?t c?ng, kh?ng v? h?t, kh?ng m?o l?ch.

---

## 2. API ENDPOINT & C?U TR?C JSON

### `POST /api/cad/parse-road`

#### Request (Multipart Form-Data):
| Field | Ki?u d? li?u | B?t bu?c | M?c ??nh | M? t? |
| :--- | :--- | :---: | :---: | :--- |
| `file` | File (`.dxf`) | **C?** | - | File CAD b?n v? tuy?n ???ng |
| `roadWidth` | `double` | Kh?ng | `7.0` | B? r?ng m?t ???ng (m?t) ?? t?o ?a gi?c b? t?ng (3.5m m?i b?n) |
| `centerlineLayerName` | `string` | Kh?ng | `null` | T?n layer tim tuy?n (n?u b? tr?ng Backend t? nh?n di?n `ENTPLINETUYEN`, `TIMTUYEN`...) |

#### JSON Response tr? v?:
```json
{
  "success": true,
  "roadName": "Tuy?n ???ng ch?nh",
  "roadWidth": 7.0,
  "totalLengthMeters": 2466.2,
  "bounds": [
    [10.870323, 105.85231],
    [10.887982, 105.864945]
  ],
  "centerline": [
    [10.887953, 105.852848],
    [10.887717, 105.852342],
    [10.887626, 105.852512],
    [10.887340, 105.853213]
  ],
  "roadSurfacePolygon": [
    [10.887925, 105.852861],
    [10.887716, 105.852374],
    [10.887655, 105.852526],
    ...
    [10.887925, 105.852861]
  ],
  "leftEdge": [
    [10.887925, 105.852861],
    [10.887716, 105.852374]
  ],
  "rightEdge": [
    [10.887982, 105.852834],
    [10.887718, 105.852310]
  ]
}
```

---

## 3. ? NGH?A C?C TR??NG D? LI?U

1. **`bounds`**: `[[min_lat, min_lon], [max_lat, max_lon]]`
   - Bounding box ???c t?nh to?n kh?t theo con ???ng th?c t?.
   - Frontend ch? c?n g?i `map.fitBounds(data.bounds)` l? camera bay th?ng ??n ??ng cung ???ng tr?n v? tinh!
2. **`centerline`**: M?ng c?c ?i?m GPS `[latitude, longitude]` theo th? t? ch?y d?c tuy?n ???ng.
3. **`roadSurfacePolygon`**: M?ng c?c ?i?m GPS `[latitude, longitude]` t?o th?nh v?ng kh?p k?n bao quanh d?i m?t ???ng (Polygon) m? r?ng ??u 3.5m sang hai b?n tim.
4. **`leftEdge` & `rightEdge`**: M?ng t?a ?? 2 b?n m?p ???ng (ph?c v? v? v?ch vi?n tr?ng s?c n?t).
5. **`totalLengthMeters`**: T?ng chi?u d?i th?c t? c?a tuy?n ???ng t?nh b?ng m?t (v? d?: `2466.2m`).

---

## 4. CODE M?U T?CH H?P TR?C TI?P TR?N FRONTEND (REACT + LEAFLET)

D??i ??y l? component React ho?n ch?nh:

```tsx
import React, { useState } from 'react';
import { MapContainer, TileLayer, Polygon, Polyline, useMap } from 'react-leaflet';
import 'leaflet/dist/leaflet.css';

interface RoadData {
  success: boolean;
  roadName: string;
  roadWidth: number;
  totalLengthMeters: number;
  bounds: [[number, number], [number, number]];
  centerline: [number, number][];
  roadSurfacePolygon: [number, number][];
  leftEdge?: [number, number][];
  rightEdge?: [number, number][];
}

// Hook t? ??ng zoom camera v?o v?a v?n con ???ng
function FitRoadBounds({ bounds }: { bounds: [[number, number], [number, number]] }) {
  const map = useMap();
  React.useEffect(() => {
    if (bounds && bounds.length === 2) {
      map.fitBounds(bounds, { padding: [50, 50], maxZoom: 18 });
    }
  }, [bounds, map]);
  return null;
}

export function RoadGisViewer() {
  const [roadData, setRoadData] = useState<RoadData | null>(null);
  const [loading, setLoading] = useState(false);

  const handleUploadDxf = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;

    setLoading(true);
    const formData = new FormData();
    formData.append('file', file);
    formData.append('roadWidth', '7.0'); // 7 m?t b? r?ng m?t ???ng

    try {
      const res = await fetch('http://localhost:5198/api/cad/parse-road', {
        method: 'POST',
        body: formData,
      });

      if (!res.ok) throw new Error(`L?i server: ${res.status}`);

      const data: RoadData = await res.json();
      setRoadData(data);
    } catch (err) {
      console.error('L?i n?p ???ng CAD:', err);
      alert('Kh?ng th? b?c t?ch tim ???ng t? CAD!');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div style={{ position: 'relative', width: '100vw', height: '100vh' }}>
      {/* N?t Upload v? Th?ng s? Tuy?n */}
      <div style={{
        position: 'absolute', top: 16, left: 16, zIndex: 1000,
        background: '#0f172a', color: '#f8fafc', padding: '14px 18px',
        borderRadius: 12, boxShadow: '0 4px 20px rgba(0,0,0,0.4)',
        border: '1px solid #1e293b', fontFamily: 'sans-serif'
      }}>
        <div style={{ fontWeight: 700, fontSize: 15, marginBottom: 8, color: '#38bdf8' }}>
          ??? RoadGuard CAD ? GIS Vector
        </div>
        <input type="file" accept=".dxf" onChange={handleUploadDxf} disabled={loading} />
        {loading && <div style={{ marginTop: 8, color: '#fbbf24' }}>?ang b?c t?ch t?a ?? GPS & ?a gi?c m?t ???ng...</div>}

        {roadData && (
          <div style={{ marginTop: 10, fontSize: 13, borderTop: '1px solid #334155', paddingTop: 8 }}>
            <div><b>T?n tuy?n:</b> {roadData.roadName}</div>
            <div><b>Chi?u d?i:</b> {roadData.totalLengthMeters.toLocaleString()} m</div>
            <div><b>B? r?ng:</b> {roadData.roadWidth} m</div>
          </div>
        )}
      </div>

      {/* B?n ?? GIS Th?c t? */}
      <MapContainer center={[10.88, 105.85]} zoom={15} style={{ width: '100%', height: '100%' }}>
        {/* B?n ?? v? tinh ESRI World Imagery */}
        <TileLayer
          attribution='&copy; Esri World Imagery'
          url="https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}"
          maxZoom={19}
        />

        {roadData && (
          <>
            <FitRoadBounds bounds={roadData.bounds} />

            {/* 1. D?I M?T ???NG B? T?NG 2D (M?u x?m b? t?ng #71717a, ??c 85%) */}
            <Polygon
              positions={roadData.roadSurfacePolygon}
              pathOptions={{
                fillColor: '#71717a',
                fillOpacity: 0.85,
                stroke: false,
              }}
            />

            {/* 2. VI?N M?P ???NG 2 B?N (M?u tr?ng n?t li?n #FFFFFF) */}
            {roadData.leftEdge && (
              <Polyline
                positions={roadData.leftEdge}
                pathOptions={{ color: '#FFFFFF', weight: 2, opacity: 0.95 }}
              />
            )}
            {roadData.rightEdge && (
              <Polyline
                positions={roadData.rightEdge}
                pathOptions={{ color: '#FFFFFF', weight: 2, opacity: 0.95 }}
              />
            )}

            {/* 3. TIM TUY?N ???NG (M?u v?ng Highway #FFD700 n?t ??t) */}
            <Polyline
              positions={roadData.centerline}
              pathOptions={{
                color: '#FFD700',
                weight: 3,
                dashArray: '8, 8',
                opacity: 1.0,
              }}
            />
          </>
        )}
      </MapContainer>
    </div>
  );
}
```

---

## 5. T?NG K?T
1. **Endpoint duy nh?t c?n d?ng**: `POST /api/cad/parse-road`.
2. **T?a ??**: Tr? v? `[latitude, longitude]` chu?n Leaflet, kh?ng c?n Proj4 hay chuy?n ??i g? th?m.
3. **Hi?u n?ng**: C?c nh? (<20KB), render t?c th? trong 2s, kh?ng lag b?n ??.
