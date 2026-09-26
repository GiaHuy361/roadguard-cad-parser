using System;
using System.Collections.Generic;
using GeoJSON.Net.Feature;

namespace RoadGuard.CadParser.DTOs
{
    /// <summary>
    /// Engineering spatial analytics computed from the parsed geometry data.
    /// Domain logic follows TCVN 10380:2014 (Vietnamese Road Engineering Standard).
    /// </summary>
    public sealed class EngineeringAnalytics
    {
        /// <summary>
        /// Total length of all LineString / MultiLineString geometries in the drawing, in metres.
        /// For SRID 4326 (degrees), raw degree-length is multiplied by 111,320 m/° (equatorial approximation).
        /// For SRID 32648/32649 (UTM, metres), the raw NTS length is used directly.
        /// </summary>
        public double TotalLengthMeters { get; init; }

        /// <summary>
        /// Estimated number of concrete road slabs.
        /// Formula (TCVN 10380:2014): TotalLengthMeters / 4.0 m per slab.
        /// </summary>
        public int EstimatedConcreteSlabs { get; init; }

        /// <summary>
        /// Number of 100-metre road segments inferred from total length.
        /// Formula: ceil(TotalLengthMeters / 100.0).
        /// </summary>
        public int RoadSegments { get; init; }

        /// <summary>Total wall-clock time spent parsing the DXF file, in milliseconds.</summary>
        public long ProcessingTimeMs { get; init; }
    }

    /// <summary>
    /// Wraps the parsed GeoJSON FeatureCollection with additional metadata
    /// about the source CAD file, SRID used, parse statistics, and engineering analytics.
    /// </summary>
    public sealed class GeoJsonResponse
    {
        /// <summary>The ISO 8601 timestamp of when parsing completed (UTC).</summary>
        public DateTime ParsedAtUtc { get; init; } = DateTime.UtcNow;

        /// <summary>Original filename as provided by the caller.</summary>
        public string SourceFileName { get; init; } = string.Empty;

        /// <summary>
        /// The SRID applied to all geometries in this response.
        /// Supported: 4326 (WGS-84), 32648 (UTM zone 48N), 32649 (UTM zone 49N).
        /// </summary>
        public int Srid { get; init; }

        /// <summary>Total number of GeoJSON Features extracted across all layers.</summary>
        public int TotalFeatureCount { get; init; }

        /// <summary>
        /// Number of features per layer, keyed by layer name.
        /// Useful for client-side layer filtering and debugging.
        /// </summary>
        public Dictionary<string, int> FeatureCountByLayer { get; init; } = new();

        /// <summary>
        /// The fully parsed GeoJSON FeatureCollection.
        /// Each Feature carries layer metadata in its Properties dictionary.
        /// </summary>
        public FeatureCollection FeatureCollection { get; init; } = new FeatureCollection();

        /// <summary>
        /// Non-fatal warnings accumulated during parsing
        /// (e.g., unsupported entity types, degenerate geometries skipped).
        /// </summary>
        public List<string> Warnings { get; init; } = new();

        /// <summary>
        /// Spatial engineering analytics: total road length, slab count (TCVN 10380:2014),
        /// segment count, and total processing time.
        /// </summary>
        public EngineeringAnalytics Analytics { get; init; } = new();
    }

    /// <summary>
    /// Standardised error envelope returned when parsing fails.
    /// </summary>
    public sealed class CadParserErrorResponse
    {
        public int StatusCode { get; init; }
        public string Error { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    }
}
