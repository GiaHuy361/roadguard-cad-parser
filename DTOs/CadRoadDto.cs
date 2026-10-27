using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using System.Text.Json.Serialization;

namespace RoadGuard.CadParser.DTOs
{
    public sealed class CadParseRoadRequest
    {
        public IFormFile File { get; set; } = null!;
        public double RoadWidth { get; set; } = 7.0;
        public string? CenterlineLayerName { get; set; }
        public double? CentralMeridian { get; set; }
        public double? ScaleFactor { get; set; }
        public string? AxisOrder { get; set; }
    }

    public sealed class CadParseCoordsRequest
    {
        /// <summary>One VN-2000 point per line, e.g. "1199145.22, 614235.15" (Northing, Easting; order configurable or auto-detected).</summary>
        public string Coordinates { get; set; } = string.Empty;
        public double RoadWidth { get; set; } = 7.0;
        public double? CentralMeridian { get; set; }
        public double? ScaleFactor { get; set; }
        public string? AxisOrder { get; set; }
    }

    public sealed class CadParseRoadResponse
    {
        [JsonProperty("success")]
        [JsonPropertyName("success")]
        public bool Success { get; set; } = true;

        [JsonProperty("roadName")]
        [JsonPropertyName("roadName")]
        public string RoadName { get; set; } = "Tuyến đường chính";

        [JsonProperty("roadWidth")]
        [JsonPropertyName("roadWidth")]
        public double RoadWidth { get; set; } = 7.0;

        [JsonProperty("totalLengthMeters")]
        [JsonPropertyName("totalLengthMeters")]
        public double TotalLengthMeters { get; set; }

        [JsonProperty("surfaceAreaSqm")]
        [JsonPropertyName("surfaceAreaSqm")]
        public double? SurfaceAreaSqm { get; set; }

        [JsonProperty("nominalAreaSqm")]
        [JsonPropertyName("nominalAreaSqm")]
        public double? NominalAreaSqm { get; set; }

        [JsonProperty("centralMeridian")]
        [JsonPropertyName("centralMeridian")]
        public double? CentralMeridian { get; set; }

        [JsonProperty("scaleFactor")]
        [JsonPropertyName("scaleFactor")]
        public double? ScaleFactor { get; set; }

        [JsonProperty("axisOrder")]
        [JsonPropertyName("axisOrder")]
        public string? AxisOrder { get; set; }

        [JsonProperty("crsMetadata")]
        [JsonPropertyName("crsMetadata")]
        public Dictionary<string, object>? CrsMetadata { get; set; }

        [JsonProperty("bounds")]
        [JsonPropertyName("bounds")]
        public List<List<double>>? Bounds { get; set; } = new();

        [JsonProperty("bbox")]
        [JsonPropertyName("bbox")]
        public List<double>? Bbox { get; set; }

        [JsonProperty("centerline")]
        [JsonPropertyName("centerline")]
        public List<List<double>> Centerline { get; set; } = new();

        [JsonProperty("centerlineBranches")]
        [JsonPropertyName("centerlineBranches")]
        public List<List<List<double>>>? CenterlineBranches { get; set; }

        [JsonProperty("roadSurfacePolygon")]
        [JsonPropertyName("roadSurfacePolygon")]
        public List<List<double>> RoadSurfacePolygon { get; set; } = new();

        [JsonProperty("roadSurfacePolygons")]
        [JsonPropertyName("roadSurfacePolygons")]
        public List<List<List<double>>>? RoadSurfacePolygons { get; set; }

        [JsonProperty("trafficIslands")]
        [JsonPropertyName("trafficIslands")]
        public List<List<List<double>>>? TrafficIslands { get; set; }

        [JsonProperty("leftEdge")]
        [JsonPropertyName("leftEdge")]
        public List<List<double>>? LeftEdge { get; set; }

        [JsonProperty("rightEdge")]
        [JsonPropertyName("rightEdge")]
        public List<List<double>>? RightEdge { get; set; }

        [JsonProperty("layerStats")]
        [JsonPropertyName("layerStats")]
        public List<CadLayerStatItem>? LayerStats { get; set; }

        [JsonProperty("isGeoreferenced")]
        [JsonPropertyName("isGeoreferenced")]
        public bool? IsGeoreferenced { get; set; } = true;

        [JsonProperty("crsState")]
        [JsonPropertyName("crsState")]
        public string? CrsState { get; set; }

        [JsonProperty("notice")]
        [JsonPropertyName("notice")]
        public string? Notice { get; set; }

        [JsonProperty("candidateLayers")]
        [JsonPropertyName("candidateLayers")]
        public List<CadCandidateLayerItem>? CandidateLayers { get; set; }

        [JsonProperty("centerlineLayerName")]
        [JsonPropertyName("centerlineLayerName")]
        public string? CenterlineLayerName { get; set; }

        [JsonProperty("centerlineCad")]
        [JsonPropertyName("centerlineCad")]
        public List<List<double>>? CenterlineCad { get; set; }

        [JsonProperty("centerlineBranchesCad")]
        [JsonPropertyName("centerlineBranchesCad")]
        public List<List<List<double>>>? CenterlineBranchesCad { get; set; }

        [JsonProperty("roadSurfacePolygonCad")]
        [JsonPropertyName("roadSurfacePolygonCad")]
        public List<List<double>>? RoadSurfacePolygonCad { get; set; }

        [JsonProperty("requiresAxisConfirmation")]
        [JsonPropertyName("requiresAxisConfirmation")]
        public bool? RequiresAxisConfirmation { get; set; }

        [JsonProperty("error")]
        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonProperty("detail")]
        [JsonPropertyName("detail")]
        public string? Detail { get; set; }
    }

    public sealed class CadCandidateLayerItem
    {
        [JsonProperty("name")]
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("score")]
        [JsonPropertyName("score")]
        public double Score { get; set; }

        [JsonProperty("totalLengthMeters")]
        [JsonPropertyName("totalLengthMeters")]
        public double TotalLengthMeters { get; set; }

        [JsonProperty("entityCount")]
        [JsonPropertyName("entityCount")]
        public int EntityCount { get; set; }

        [JsonProperty("isRecommended")]
        [JsonPropertyName("isRecommended")]
        public bool IsRecommended { get; set; }

        [JsonProperty("isSelected")]
        [JsonPropertyName("isSelected")]
        public bool IsSelected { get; set; }
    }

    public sealed class CadLayerStatItem
    {
        [JsonProperty("name")]
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("count")]
        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonProperty("category")]
        [JsonPropertyName("category")]
        public string Category { get; set; } = "Other";
    }
}
