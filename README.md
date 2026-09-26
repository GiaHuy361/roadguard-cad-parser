# 🛣️ RoadGuard CAD Parser Service

> **High-Performance CAD & Civil Engineering Ingestion Microservice for Road Infrastructure Digitization and Pavement Distress Management.**

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Python](https://img.shields.io/badge/Python-3.10%2B-3776AB?logo=python&logoColor=white)](https://www.python.org/)
[![ezdxf](https://img.shields.io/badge/CAD-ezdxf-blue)](https://ezdxf.readthedocs.io/)
[![GeoJSON](https://img.shields.io/badge/GIS-GeoJSON%20RFC%207946-orange)](https://datatracker.ietf.org/doc/html/rfc7946)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

---

## 📌 Tổng quan (Overview)

**RoadGuard CAD Parser** là dịch vụ lõi chịu trách nhiệm đọc, phân tích và trích xuất dữ liệu hình học từ các bản vẽ thiết kế kỹ thuật cầu đường định dạng AutoCAD (`.dxf`), chuyển đổi thành dữ liệu không gian địa lý chuẩn quốc tế (**GeoJSON RFC 7946**) để hiển thị trực quan trên bản đồ số (Leaflet, Mapbox, OpenStreetMap, ESRI Satellite).

Hệ thống được thiết kế theo kiến trúc lai (**Hybrid C# & Python Bridge**), kết hợp sức mạnh xử lý web/CSDL của **ASP.NET Core 8** và khả năng bóc tách CAD chuyên sâu của **`ezdxf`**, đảm bảo đọc mượt các bản vẽ giao thông thực tế cực nặng (Civil 3D, Nova TDN, T-Road) với hơn 350+ layers mà không bị nghẽn hay tràn bộ nhớ.

---

## ✨ Tính năng nổi bật (Key Features)

- ⚡ **Heavy CAD Ingestion:** Đọc và bóc tách các file CAD giao thông phức tạp (dung lượng lớn, nhiều block, proxy entities, spline, font tiếng Việt TCVN3/VNI) chỉ trong **0.3s - 1.8s**.
- 🧠 **Smart Centerline Auto-Detection:** Tự động nhận diện tim tuyến thông minh dựa trên bộ từ khóa quy chuẩn giao thông Việt Nam (`ENTPLINETUYEN`, `TIM_THIET_KE`, `TIMDUONG`, `TKDTIMTUYEN`...) kết hợp thuật toán xếp hạng theo **chiều dài hình học liên tục** ($\ge 50$m), tự động loại trừ các layer chú thích hoặc cọc ngắn.
- 📐 **2D Road Surface Generation (NTS):** Tự động sinh diện tích mặt đường 2D (Polygon) từ tim tuyến thông qua thuật toán buffer hình học của **NetTopologySuite** (bề rộng tiêu chuẩn 7m, 3.5m mỗi bên tim).
- 📍 **Chainage Segmentation (Cọc lý trình):** Tự động chia tim tuyến thành các phân đoạn 100m chuẩn kỹ thuật (`SEG-00`, `SEG-01`...) phục vụ việc đánh dấu và gán nhãn vị trí hư hỏng mặt đường (Pavement Distress).
- 📊 **TCVN 10380:2014 Analytics:** Tự động tính toán các chỉ số kỹ thuật mặt đường bê tông xi măng:
  - Tổng chiều dài tuyến (m).
  - Diện tích bề mặt thảm nhựa / bê tông ($m^2$).
  - Số lượng tấm bê tông tiêu chuẩn ($3.5m \times 4.0m$) ước tính.
  - Số lượng phân đoạn và chiều dài trung bình.
- 💾 **Lưu trữ & Truy xuất:** Tích hợp **Entity Framework Core** và **SQL Server**, lưu trữ toàn bộ bản vẽ và các GeoJSON feature tương ứng.

---

## 🏗️ Kiến trúc hệ thống (Architecture)

```
                       [Client / Frontend]
                                │
                                ▼ POST /api/cad/parse-dxf (.dxf)
┌──────────────────────────────────────────────────────────────────────────┐
│  ASP.NET Core 8 Web API (Port 5198)                                      │
│                                                                          │
│  [Controllers] CadProcessingController.cs                                │
│       │                                                                  │
│       ▼                                                                  │
│  [Services] CadParserService.cs                                          │
│       │                                                                  │
│       ├──► Python Bridge Process (CLI Subprocess)                        │
│       │      └─ Services/PythonBridge/cad_bridge.py (ezdxf)              │
│       │           ├─ Ingest 350+ Layers, Proxy Objects                   │
│       │           ├─ Smart Auto-Detect Centerline Layer                  │
│       │           └─ Flatten Polylines & Coordinate Projection           │
│       │                                                                  │
│       ├──► NetTopologySuite (NTS)                                        │
│       │      └─ Buffer Centerline (3.5m) ──► 2D Asphalt Polygon          │
│       │                                                                  │
│       └──► Entity Framework Core (SQL Server)                            │
│              └─ Persist CadDrawing & CadGeometryFeature                  │
└──────────────────────────────────────────────────────────────────────────┘
                                │
                                ▼ GeoJSON FeatureCollection + Analytics
                       [Leaflet / Map View]
```

---

## 🔌 API Endpoints

### 1. `POST /api/cad/get-layers`
Quét và trích xuất danh sách tất cả các layer có trong bản vẽ, đồng thời gợi ý layer tim tuyến tối ưu nhất.
- **Content-Type:** `multipart/form-data`
- **Body:** `file` (File `.dxf`)
- **Response:**
  ```json
  {
    "layers": ["ENTPLINETUYEN", "TIM_THIET_KE", "SUONTUYEN", "0", "DEFPOINTS"],
    "suggestedCenterlineLayer": "ENTPLINETUYEN",
    "totalLayers": 355
  }
  ```

### 2. `POST /api/cad/parse-dxf`
Phân tích toàn diện bản vẽ, xuất GeoJSON đa lớp và tính toán thông số kỹ thuật.
- **Content-Type:** `multipart/form-data`
- **Body:**
  - `file`: File `.dxf` (bắt buộc).
  - `layerName`: Tên layer tim tuyến (tùy chọn, để trống sẽ tự auto-detect).
  - `targetSrid`: Hệ quy chiếu tọa độ đích (mặc định `4326` - GPS WGS-84).
- **Response:** `GeoJsonResponse` chuẩn RFC 7946 gồm:
  - `CENTERLINE` (`LineString`): Tim đường.
  - `ROAD_SURFACE_2D` (`Polygon`): Bề mặt đường 2 làn xe.
  - `STATION_POINTS` (`Point`): Cọc lý trình 100m dọc theo tuyến.
  - `analytics`: Thông số chiều dài, diện tích, số tấm bê tông theo TCVN 10380:2014.

### 3. `GET /api/cad/drawings`
Lấy danh sách các bản vẽ đã lưu trong hệ thống.

### 4. `GET /api/cad/drawings/{id}`
Lấy chi tiết bản vẽ và tái tạo toàn bộ GeoJSON FeatureCollection đã lưu trong CSDL.

---

## 🚀 Hướng dẫn cài đặt & Chạy cục bộ (Getting Started)

### 1. Yêu cầu môi trường (Prerequisites)
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Python 3.10+](https://www.python.org/downloads/) (đảm bảo chọn tick *"Add python.exe to PATH"*)
- SQL Server (LocalDB hoặc SQL Server Express)

### 2. Cài đặt thư viện Python
```powershell
pip install ezdxf
```

### 3. Cấu hình CSDL
Mở file `appsettings.Development.json` và cấu hình chuỗi kết nối SQL Server:
```json
"ConnectionStrings": {
  "DefaultConnection": "Server=localhost;Database=RoadGuardCAD;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True"
}
```

Chạy migration để khởi tạo cấu trúc bảng:
```powershell
dotnet ef database update
```

### 4. Khởi chạy ứng dụng
```powershell
dotnet run --launch-profile http
```

- **Swagger UI:** Mở trình duyệt truy cập: [`http://localhost:5198/swagger`](http://localhost:5198/swagger)

---

## 📂 Cấu trúc thư mục (Project Structure)

```
roadguard-cad-parser/
├── Controllers/
│   └── CadProcessingController.cs       # HTTP Endpoints (Upload, Parse, Drawings)
├── Data/
│   └── RoadGuardDbContext.cs            # EF Core DbContext
├── DTOs/
│   └── GeoJsonResponse.cs               # Data Contracts chuẩn RFC 7946 & Analytics
├── Entities/
│   ├── CadDrawing.cs                    # Bản vẽ & Metadata thống kê
│   └── CadGeometryFeature.cs            # Các Feature GIS trong bản vẽ
├── Migrations/                          # EF Core Code-First Migrations
├── Services/
│   ├── Interfaces/
│   │   └── ICadParserService.cs         # Service Contract
│   ├── Implementations/
│   │   └── CadParserService.cs          # Pipeline xử lý: Bridge, NTS Buffer, TCVN
│   └── PythonBridge/
│       └── cad_bridge.py                # Python ezdxf Engine & Smart Detection
├── BACKEND_GUIDE.md                     # Tài liệu hướng dẫn chuyên sâu cho lập trình viên
├── Program.cs                           # Program startup & Dependency Injection
└── appsettings.Development.json         # Cấu hình môi trường dev
```

---

## 👥 Nhóm phát triển (Contributors)

- **Gia Huy** - Lead Developer / System Architect
- **Hoàng** - Backend Developer
- **RoadGuard Team**
