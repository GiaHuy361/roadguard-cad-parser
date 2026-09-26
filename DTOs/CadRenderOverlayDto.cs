using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using System.Text.Json.Serialization;
using GeoJSON.Net.Feature;

namespace RoadGuard.CadParser.DTOs
{
    public sealed class CadRenderOverlayRequest
    {
        public IFormFile File { get; set; } = null!;
        public string? CenterlineLayerName { get; set; }
        public double RoadWidth { get; set; } = 7.0;
        public int OutputSizePx { get; set; } = 2048;
    }

    public sealed class CadRenderOverlayResponse
    {
        [JsonProperty("bounds")]
        [JsonPropertyName("bounds")]
        public List<List<double>> Bounds { get; set; } = new();

        [JsonProperty("image_base64")]
        [JsonPropertyName("imageBase64")]
        public string ImageBase64 { get; set; } = string.Empty;

        [JsonProperty("geojson")]
        [JsonPropertyName("geojson")]
        public FeatureCollection? GeoJson { get; set; }

        [JsonProperty("success")]
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonProperty("style")]
        [JsonPropertyName("style")]
        public string Style { get; set; } = "concrete_road";

        [JsonProperty("cropped_to_layer")]
        [JsonPropertyName("croppedToLayer")]
        public string? CroppedToLayer { get; set; }

        [JsonProperty("bbox_wgs84")]
        [JsonPropertyName("bboxWgs84")]
        public Dictionary<string, double>? BboxWgs84 { get; set; }

        [JsonProperty("bbox_wcs")]
        [JsonPropertyName("bboxWcs")]
        public List<double>? BboxWcs { get; set; }

        [JsonProperty("image_size_px")]
        [JsonPropertyName("imageSizePx")]
        public List<int> ImageSizePx { get; set; } = new();

        [JsonProperty("error")]
        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonProperty("detail")]
        [JsonPropertyName("detail")]
        public string? Detail { get; set; }
    }
}
