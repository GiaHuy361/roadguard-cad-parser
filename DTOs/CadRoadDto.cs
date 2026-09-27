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
    }

    public sealed class CadParseRoadResponse
    {
        [JsonProperty("success")]
        [JsonPropertyName("success")]
        public bool Success { get; set; } = true;

        [JsonProperty("roadName")]
        [JsonPropertyName("roadName")]
        public string RoadName { get; set; } = "Tuy?n ???ng ch?nh";

        [JsonProperty("roadWidth")]
        [JsonPropertyName("roadWidth")]
        public double RoadWidth { get; set; } = 7.0;

        [JsonProperty("totalLengthMeters")]
        [JsonPropertyName("totalLengthMeters")]
        public double TotalLengthMeters { get; set; }

        [JsonProperty("centralMeridian")]
        [JsonPropertyName("centralMeridian")]
        public double? CentralMeridian { get; set; }

        [JsonProperty("bounds")]
        [JsonPropertyName("bounds")]
        public List<List<double>> Bounds { get; set; } = new();

        [JsonProperty("centerline")]
        [JsonPropertyName("centerline")]
        public List<List<double>> Centerline { get; set; } = new();

        [JsonProperty("centerlineBranches")]
        [JsonPropertyName("centerlineBranches")]
        public List<List<List<double>>>? CenterlineBranches { get; set; }

        [JsonProperty("roadSurfacePolygon")]
        [JsonPropertyName("roadSurfacePolygon")]
        public List<List<double>> RoadSurfacePolygon { get; set; } = new();

        [JsonProperty("leftEdge")]
        [JsonPropertyName("leftEdge")]
        public List<List<double>>? LeftEdge { get; set; }

        [JsonProperty("rightEdge")]
        [JsonPropertyName("rightEdge")]
        public List<List<double>>? RightEdge { get; set; }

        [JsonProperty("error")]
        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonProperty("detail")]
        [JsonPropertyName("detail")]
        public string? Detail { get; set; }
    }
}
