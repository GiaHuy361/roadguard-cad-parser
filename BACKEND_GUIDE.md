# TÀI LIỆU HƯỚNG DẪN KIẾN TRÚC & TÍCH HỢP BACKEND (ROADGUARD CAD PARSER)
> **Dành cho:** Hoàng (Backend Developer) & Team RoadGuard  
> **Repository:** `roadguard-cad-parser`  
> **Nhánh làm việc chính:** `develop` / `hoang`

---

## 1. TỔNG QUAN HỆ THỐNG (SYSTEM OVERVIEW)

Module **RoadGuard CAD Parser** là dịch vụ xử lý và trích xuất dữ liệu hình học từ bản vẽ CAD giao thông (`.dxf`), chuyển đổi thành chuẩn GIS quốc tế (**GeoJSON RFC 7946**) để phục vụ:
1. Hiển thị bản đồ số hóa tuyến đường trên Frontend (Leaflet / Mapbox / OpenStreetMap).
2. Phân đoạn tuyến đường (Segmentation), phục vụ gán nhãn hư hỏng mặt đường (Pavement Distress Management).
3. Tính toán các chỉ số kỹ thuật mặt đường theo tiêu chuẩn Việt Nam (**TCVN 10380:2014**).

```
   [User / Frontend]
          │
          ▼ (Upload .dxf)
┌─────────────────────────────────────────────────────────────┐
│ ASP.NET Core Web API (Port 5198)                            │
│  Controllers/CadProcessingController.cs                     │
│    │                                                        │
│    ▼                                                        │
│  Services/Implementations/CadParserService.cs               │
│    │                                                        │
│    ├──► Python Bridge (Services/PythonBridge/cad_bridge.py) │
│    │      ├─ Thư viện ezdxf: Đọc heavy CAD, 355+ layers     │
│    │      └─ Smart Auto-Detection: Nhận diện tim tuyến       │
│    │                                                        │
│    ├──► NetTopologySuite (NTS)                             │
│    │      └─ Buffer 2D tim tuyến -> Mặt đường (Polygon)     │
│    │                                                        │
│    └──► Entity Framework Core (SQL Server)                  │
│           └─ Lưu CadDrawing & CadGeometryFeature            │
└─────────────────────────────────────────────────────────────┘
          │
          ▼ (GeoJSON FeatureCollection + Analytics)
   [Frontend Render Bản Đồ]
```

---

## 2. VÌ SAO PHẢI TÍCH HỢP PYTHON BRIDGE (`ezdxf`)?

### Vấn đề gặp phải trước đây:
- Thư viện C# thuần (`netDxf`) chỉ hoạt động với các file CAD đơn giản, tiêu chuẩn cũ.
- Khi gặp bản vẽ thiết kế đường thực tế ở Việt Nam (xuất từ **AutoCAD Civil 3D**, **Nova TDN**, **T-Road** như file `3. BDTK.dxf` nặng 11MB, hơn 355 layers):
  - `netDxf` bị văng lỗi hoặc trả về `null` do không đọc được Custom Object, Proxy Entity và font tiếng Việt TCVN3/VNI.
  - API trả về HTTP 422 (`ParseFailed`), không load được bản đồ.

### Giải pháp kiến trúc Python Bridge:
- Tạo một script cầu nối nhẹ: `Services/PythonBridge/cad_bridge.py` sử dụng thư viện **`ezdxf`** (thư viện mã nguồn mở C/Python mạnh nhất về CAD).
- C# gọi Python qua CLI process ngầm (`RunPythonBridgeAsync`).
- **Tốc độ:** Xử lý file 11MB, 355 layers chỉ mất **~0.3s - 1.8s**.
- **Cơ chế Fallback an toàn:** Nếu môi trường chưa cài Python hoặc script lỗi, C# sẽ tự động fallback về parser `netDxf` nguyên bản.

---

## 3. THUẬT TOÁN NHẬN DIỆN TIM TUYẾN TỰ ĐỘNG (SMART AUTO-DETECTION)

Bản vẽ thiết kế đường có hàng trăm layer rác (text, ghi chú, cao độ, taluy, cọc...). Thuật toán trong `cad_bridge.py` tự động tìm ra tim tuyến chính xác:

1. **Bộ từ khóa ưu tiên thiết kế đường Việt Nam:**
   - Các layer tim tuyến phổ biến: `ENTPLINETUYEN`, `TIM_THIET_KE`, `TIMDUONG`, `TKDTIMTUYEN`, `TIMTUYEN`, `CENTER`, `TIM`, `AXIS`...
2. **Lọc nhiễu & xếp hạng theo chiều dài liên tục (Continuous Length Ranking):**
   - Loại trừ ngay các layer chứa từ khóa ghi chú, điểm mốc: `SUONTUYEN`, `DAUTIM`, `TEXT`, `DIM`, `NOTE`, `GHICHU`, `COC`, `LYTRINH`.
   - Đo tổng chiều dài hình học thực tế của các ứng viên.
   - **Quy tắc chọn:** Chọn layer có tổng chiều dài liên tục lớn nhất ($\ge 50$m).  
     *(Tránh được lỗi trước đây khi file có layer `TIM_THIET_KE` dài chỉ 7m nhưng bị chọn nhầm thay vì layer chính `ENTPLINETUYEN` dài 2.85 km).*

---

## 4. CHI TIẾT CÁC API ENDPOINTS

### 1. `POST /api/cad/get-layers`
- **Mục đích:** Quét file DXF và trả về danh sách tất cả các layers có trong bản vẽ, đồng thời gợi ý sẵn layer tim tuyến tối ưu.
- **Request:** `multipart/form-data` chứa trường `file` (.dxf).
- **Response mẫu:**
  ```json
  {
    "layers": ["ENTPLINETUYEN", "TIM_THIET_KE", "0", "DEFPOINTS", ...],
    "suggestedCenterlineLayer": "ENTPLINETUYEN",
    "totalLayers": 355
  }
  ```

### 2. `POST /api/cad/parse-dxf`
- **Mục đích:** Parse toàn bộ hình học của tim tuyến, sinh mặt đường 2D, chia cọc phân đoạn và lưu vào CSDL.
- **Request:** `multipart/form-data`
  - `file`: File CAD `.dxf` (bắt buộc).
  - `layerName`: Tên layer tim tuyến (tùy chọn; nếu rỗng thì Python tự auto-detect).
  - `targetSrid`: Hệ tọa độ xuất ra (mặc định `4326` - GPS WGS-84).
- **Response Data Contract (`GeoJsonResponse`):**
  - **`type`**: `"FeatureCollection"`
  - **`features`**: Gồm 3 nhóm đối tượng:
    1. `CENTERLINE`: Đường tim dạng `LineString` nối từ đầu đến cuối tuyến.
    2. `ROAD_SURFACE_2D`: Vùng mặt đường dạng `Polygon` được tạo bởi NTS (Buffer sang 2 bên 3.5m - tiêu chuẩn đường 1 làn 7m).
    3. `STATION_POINTS`: Các điểm cọc lý trình dạng `Point` dọc theo tim tuyến (mỗi cọc cách nhau 100m: `SEG-00 (0m)`, `SEG-01 (100m)`...).
  - **`analytics` (Thông số kỹ thuật TCVN 10380:2014):**
    ```json
    {
      "totalLengthMeters": 2851.04,
      "surfaceAreaSqMeters": 9978.63,
      "estimatedSlabs": 713,
      "segmentCount": 40,
      "averageSegmentLengthMeters": 71.28
    }
    ```

### 3. `GET /api/cad/drawings` & `GET /api/cad/drawings/{id}`
- Lấy lịch sử các bản vẽ đã lưu trong database kèm các GeoJSON Feature tương ứng.

---

## 5. CƠ SỞ DỮ LIỆU (DATABASE SCHEMA & EF CORE)

Dự án dùng **Entity Framework Core** kết nối với SQL Server (`RoadGuardCAD`).

### Các thực thể chính (`Entities/`):
1. **`CadDrawing`**:
   - `Id`: GUID định danh.
   - `FileName`, `FileSize`: Thông tin file gốc.
   - `SelectedLayer`: Layer tim tuyến đã dùng để bóc tách.
   - `TotalLengthMeters`, `SurfaceAreaSqMeters`, `EstimatedSlabs`, `SegmentCount`: Các chỉ số tính toán.
   - `CreatedAt`: Thời điểm upload.
2. **`CadGeometryFeature`**:
   - `Id`: GUID định danh.
   - `DrawingId`: Khóa ngoại trỏ về `CadDrawing`.
   - `FeatureType`: `"CENTERLINE"`, `"ROAD_SURFACE_2D"`, `"STATION_POINTS"`.
   - `GeometryJson`: GeoJSON dạng text thuần của hình học.
   - `PropertiesJson`: Thuộc tính mở rộng (chiều dài, mã cọc, lý trình...).

---

## 6. HƯỚNG DẪN CÀI ĐẶT & CHẠY LOCAL (DEV SETUP)

### Bước 1: Yêu cầu môi trường
- **.NET SDK 8.0**
- **Python 3.10+** (đã thêm vào PATH)
- **SQL Server LocalDB** hoặc **SQL Server Express**

### Bước 2: Cài đặt thư viện Python
Mở terminal (PowerShell / Command Prompt) và chạy:
```powershell
pip install ezdxf
```

### Bước 3: Cấu hình Connection String
Mở file `appsettings.Development.json` và kiểm tra chuỗi kết nối:
```json
"ConnectionStrings": {
  "DefaultConnection": "Server=localhost;Database=RoadGuardCAD;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True"
}
```

### Bước 4: Chạy Migration CSDL
```powershell
dotnet ef database update
```

### Bước 5: Khởi động API Server
```powershell
dotnet run --launch-profile http
```
- API Swagger UI sẽ hiển thị tại: **`http://localhost:5198/swagger`**

---

## 7. CẤU TRÚC THƯ MỤC SOURCE CODE

```
roadguard-cad-parser/
├── Controllers/
│   └── CadProcessingController.cs     # Endpoints HTTP: Upload, Parse, Get Layers, Query Drawings
├── Data/
│   └── RoadGuardDbContext.cs          # DbContext EF Core quản lý Drawing & Features
├── DTOs/
│   └── GeoJsonResponse.cs             # Khung dữ liệu chuẩn GeoJSON RFC 7946 & Analytics
├── Entities/
│   ├── CadDrawing.cs                  # Entity bảng lưu thông tin bản vẽ
│   └── CadGeometryFeature.cs          # Entity bảng lưu các feature hình học (GIS)
├── Migrations/                        # Thư mục migration của EF Core
├── Services/
│   ├── Interfaces/
│   │   └── ICadParserService.cs       # Contract interface của service
│   ├── Implementations/
│   │   └── CadParserService.cs        # Logic chính: gọi Python Bridge, NTS Buffering, tính TCVN
│   └── PythonBridge/
│       └── cad_bridge.py              # Script Python ezdxf xử lý CAD & Auto-detect
├── Program.cs                         # Khởi tạo DI, CORS, DbContext, Swagger
└── appsettings.Development.json       # Config kết nối DB & port
```

---

## 8. CÁC ĐIỂM HOÀNG CÓ THỂ PHÁT TRIỂN TIẾP (NEXT STEPS)

1. **Tích hợp với module AI nhận diện vết nứt:**
   - Dùng các cọc lý trình `STATION_POINTS` (`SEG-00`, `SEG-01`...) và diện tích `ROAD_SURFACE_2D` làm khung lưới chuẩn để map tọa độ vết nứt (Pavement Cracks) từ camera hành trình / drone lên đúng vị trí trên bản đồ CAD.
2. **Cộng dồn lý trình (Chainage Accumulation):**
   - Hiện tại nếu tim tuyến gồm nhiều đoạn Polyline rời rạc được nối lại, có thể hoàn thiện thêm thuật toán cộng dồn lý trình liên tục `Km0+000 -> Km2+850` trong `CadParserService.cs`.
3. **Thêm tùy biến bề rộng mặt đường (Road Width Parameter):**
   - Cho phép người dùng truyền tham số bề rộng mặt đường từ UI (ví dụ 6m, 8m, 12m) thay vì cố định 7m trong hàm buffer 2D.
