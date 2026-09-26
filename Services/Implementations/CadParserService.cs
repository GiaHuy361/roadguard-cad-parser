using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GeoJSON.Net.Feature;
using GeoJSON.Net.Geometry;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using netDxf;
using netDxf.Entities;
using netDxf.Tables;
using RoadGuard.CadParser.Data;
using RoadGuard.CadParser.DTOs;
using RoadGuard.CadParser.Entities;
using NtsGeometry = NetTopologySuite.Geometries;
using NtsGeometryFactory = NetTopologySuite.Geometries.GeometryFactory;
using NtsPrecisionModel = NetTopologySuite.Geometries.PrecisionModel;
using NtsPolygon = NetTopologySuite.Geometries.Polygon;
using NtsMultiPolygon = NetTopologySuite.Geometries.MultiPolygon;
using RoadGuard.CadParser.Helpers;
using RoadGuard.CadParser.Services.Interfaces;

// ── NTS coordinate alias to avoid conflicts ──────────────────────────────── //
using NtsCoordinate = NetTopologySuite.Geometries.Coordinate;

// ── GeoJSON.Net aliases ──────────────────────────────────────────────────── //
using GjsPoint    = GeoJSON.Net.Geometry.Point;
using GjsLineStr  = GeoJSON.Net.Geometry.LineString;
using GjsPolygon  = GeoJSON.Net.Geometry.Polygon;
using GjsPosition = GeoJSON.Net.Geometry.Position;
using GjsIPos     = GeoJSON.Net.Geometry.IPosition;

namespace RoadGuard.CadParser.Services.Implementations
{
    /// <summary>
    /// Production-grade DXF parser converting AutoCAD vector entities to GeoJSON Features
    /// backed by correctly-SRID-stamped NTS geometries.
    ///
    /// SOLID notes:
    ///   SRP  - Parsing only; curve math is delegated to <see cref="CurveTessellationHelper"/>.
    ///   OCP  - New entity type handlers are additive; no existing code changes required.
    ///   LSP  - Implements <see cref="ICadParserService"/> fully.
    ///   ISP  - One focused public method; all helpers are private.
    ///   DIP  - ILogger injected; no concrete infrastructure dependencies.
    ///
    /// netDxf 2.1.1 API notes (verified via reflection):
    ///   - Entity collections are direct properties on DxfDocument (NOT under .Entities).
    ///   - LwPolylineVertex.Position   → Vector2  (X, Y)
    ///   - PolylineVertex.Position     → Vector3  (X, Y, Z)
    ///   - HatchBoundaryPath.Line.Start/End → Vector2 fields
    ///   - HatchBoundaryPath.Arc.*          → fields (Center=Vector2, Radius, StartAngle, EndAngle, IsCounterclockwise)
    ///   - HatchBoundaryPath.Ellipse.*      → fields (Center=Vector2, EndMajorAxis=Vector2, MinorRatio, StartAngle, EndAngle)
    ///   - HatchBoundaryPath.Polyline.Vertexes → Vector3[] field
    ///   - AciColor.Index → short; AciColor.IsByLayer → bool
    ///   - Layer: Color, IsFrozen, IsVisible, Linetype, Lineweight, Name
    /// </summary>
    public sealed class CadParserService : ICadParserService
    {
        // ------------------------------------------------------------------ //
        //  Constants                                                           //
        // ------------------------------------------------------------------ //
        private const long   MaxFileSizeBytes    = 100 * 1024 * 1024; // 100 MB
        private const string SupportedExtension  = ".dxf";
        private const int    MinTessellationSegs = 8;
        private const double MetersPerDegree     = 111320.0;

        // ------------------------------------------------------------------ //
        //  Dependencies                                                        //
        // ------------------------------------------------------------------ //
        private readonly ILogger<CadParserService> _logger;
        private readonly RoadGuardDbContext         _db;

        // ------------------------------------------------------------------ //
        //  Constructor                                                         //
        // ------------------------------------------------------------------ //
        public CadParserService(ILogger<CadParserService> logger, RoadGuardDbContext db)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _db     = db     ?? throw new ArgumentNullException(nameof(db));
        }

        // ================================================================== //
        //  ICadParserService implementation                                   //
        // ================================================================== //

        /// <inheritdoc/>
        public async Task<GeoJsonResponse> ParseDxfAsync(
            IFormFile file,
            int srid                 = 4326,
            int tessellationSegments = 72,
            double segmentLength     = 100.0,
            double roadWidth         = 3.5,
            double slabLength        = 4.0,
            CancellationToken cancellationToken = default)
        {
            // ── 1. Validate inputs ───────────────────────────────────────── //
            ValidateFile(file);
            ValidateSrid(srid);
            tessellationSegments = Math.Max(MinTessellationSegs, tessellationSegments);
            if (segmentLength <= 0) segmentLength = 100.0;
            if (roadWidth <= 0)     roadWidth     = 3.5;
            if (slabLength <= 0)    slabLength    = 4.0;

            var stopwatch = Stopwatch.StartNew();

            _logger.LogInformation(
                "Starting DXF parse: file={FileName}, size={Size}B, srid={Srid}, tessSegs={Segs}",
                file.FileName, file.Length, srid, tessellationSegments);

            // ── 2. Buffer IFormFile entirely in-memory (no temp file writes) //
            using var stream = new MemoryStream();
            await file.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
            stream.Position = 0;

            // ── 3. Load DXF document ─────────────────────────────────────── //
            DxfDocument dxfDoc;
            try
            {
                dxfDoc = DxfDocument.Load(stream);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DxfDocument.Load failed for '{FileName}'.", file.FileName);
                throw new InvalidOperationException(
                    $"The file '{file.FileName}' could not be parsed as a DXF document. " +
                    $"It may be corrupted or in an unsupported DXF version. Details: {ex.Message}", ex);
            }

            if (dxfDoc is null)
                throw new InvalidOperationException(
                    $"DxfDocument.Load returned null for '{file.FileName}'. The file may be empty or corrupted.");

            // ── 4. Extract all features across all layers ────────────────── //
            var features        = new List<Feature>();
            var warnings        = new List<string>();
            var featuresByLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            ProcessAllEntities(dxfDoc, features, warnings, featuresByLayer,
                               tessellationSegments, srid);

            GenerateRoadSurfaceBuffers(features, featuresByLayer, roadWidth, srid);

            _logger.LogInformation(
                "Parse complete: {Count} features, {Layers} layer(s), {Warns} warning(s).",
                features.Count, featuresByLayer.Count, warnings.Count);

            // -- Analytics (TCVN 10380:2014) & Dynamic Spatial Math --
            stopwatch.Stop();
            const double MetersPerDegree = 111320.0;
            double rawLen = 0.0;
            foreach (var feat in features)
            {
                if (feat.Properties.TryGetValue("isGeneratedBuffer", out var isGen) && isGen is true)
                    continue;

                switch (feat.Geometry)
                {
                    case GjsLineStr ls:
                        var lsCoords = ls.Coordinates;
                        for (int i = 1; i < lsCoords.Count; i++)
                        {
                            double dx = lsCoords[i].Longitude - lsCoords[i-1].Longitude;
                            double dy = lsCoords[i].Latitude  - lsCoords[i-1].Latitude;
                            rawLen += Math.Sqrt(dx*dx + dy*dy);
                        }
                        break;
                    case GjsPolygon poly:
                        if (poly.Coordinates.Count > 0)
                        {
                            var ring = poly.Coordinates[0].Coordinates;
                            for (int i = 1; i < ring.Count; i++)
                            {
                                double dx = ring[i].Longitude - ring[i-1].Longitude;
                                double dy = ring[i].Latitude  - ring[i-1].Latitude;
                                rawLen += Math.Sqrt(dx*dx + dy*dy);
                            }
                        }
                        break;
                }
            }
            double totalMeters  = srid == 4326 ? rawLen * MetersPerDegree : rawLen;
            double totalAreaSqm = Math.Round(totalMeters * roadWidth, 2);
            int estimatedSlabs  = (int)Math.Ceiling(totalMeters / slabLength);
            int roadSegments    = (int)Math.Ceiling(totalMeters / segmentLength);

            // Step 5: Database Persistence
            await PersistToDatabaseAsync(file.FileName, srid, features, cancellationToken).ConfigureAwait(false);

            return new GeoJsonResponse
            {
                SourceFileName      = file.FileName,
                Srid                = srid,
                TotalFeatureCount   = features.Count,
                FeatureCountByLayer = featuresByLayer,
                FeatureCollection   = new FeatureCollection(features),
                Warnings            = warnings,
                Analytics           = new EngineeringAnalytics
                {
                    TotalLengthMeters      = Math.Round(totalMeters, 3),
                    TotalAreaSqm           = totalAreaSqm,
                    EstimatedConcreteSlabs = estimatedSlabs,
                    RoadSegments           = roadSegments,
                    ProcessingTimeMs       = stopwatch.ElapsedMilliseconds
                }
            };
        }

        // ================================================================== //
        //  Private — Entity dispatch layer                                    //
        // ================================================================== //

        /// <summary>
        /// Dispatches every supported entity collection directly from the DxfDocument.
        /// In netDxf 2.1.1, entity collections are top-level properties on DxfDocument,
        /// NOT under a nested .Entities property.
        /// </summary>
        private void ProcessAllEntities(
            DxfDocument             dxfDoc,
            List<Feature>           features,
            List<string>            warnings,
            Dictionary<string, int> featuresByLayer,
            int                     tessSegs,
            int                     srid)
        {
            // LwPolyline ─── 2D polyline with optional bulge per vertex
            Dispatch(dxfDoc.LwPolylines,  e => MapLwPolyline(e, tessSegs),  features, warnings, featuresByLayer);
            // Polyline ────── 3D legacy polyline
            Dispatch(dxfDoc.Polylines,    e => MapPolyline(e),              features, warnings, featuresByLayer);
            // Line ──────────── simple two-point segment
            Dispatch(dxfDoc.Lines,        e => MapLine(e),                  features, warnings, featuresByLayer);
            // Arc ─────────── circular arc
            Dispatch(dxfDoc.Arcs,         e => MapArc(e, tessSegs),         features, warnings, featuresByLayer);
            // Circle ─────── full circle → closed polygon ring
            Dispatch(dxfDoc.Circles,      e => MapCircle(e, tessSegs),      features, warnings, featuresByLayer);
            // Point ──────── single coordinate
            Dispatch(dxfDoc.Points,       e => MapPoint(e),                 features, warnings, featuresByLayer);
            // Hatch ──────── filled region bounded by edges
            Dispatch(dxfDoc.Hatches,      e => MapHatch(e, tessSegs),       features, warnings, featuresByLayer);
        }

        /// <summary>
        /// Generic entity-to-feature dispatcher. Catches per-entity exceptions
        /// and records them as non-fatal warnings so one bad entity never aborts the parse.
        /// </summary>
        private void Dispatch<TEntity>(
            IEnumerable<TEntity>                  entities,
            Func<TEntity, IGeometryObject?>       converter,
            List<Feature>                         features,
            List<string>                          warnings,
            Dictionary<string, int>              featuresByLayer)
            where TEntity : EntityObject
        {
            if (entities is null) return;

            foreach (var entity in entities)
            {
                try
                {
                    var geometry = converter(entity);
                    if (geometry is null) continue;

                    var props   = BuildProperties(entity);
                    features.Add(new Feature(geometry, props));

                    var layerName = entity.Layer?.Name ?? "0";
                    featuresByLayer[layerName] =
                        featuresByLayer.TryGetValue(layerName, out var c) ? c + 1 : 1;
                }
                catch (Exception ex)
                {
                    var msg = $"[{typeof(TEntity).Name}] handle={entity.Handle} layer='{entity.Layer?.Name}': {ex.Message}";
                    warnings.Add(msg);
                    _logger.LogWarning(msg);
                }
            }
        }

        // ================================================================== //
        //  Private — Entity-to-GeoJSON mappers                               //
        // ================================================================== //

        /// <summary>
        /// Maps an LwPolyline to a GeoJSON LineString or Polygon.
        /// Bulge parameters on vertices are tessellated into arc segments.
        ///
        /// LwPolylineVertex.Position is Vector2 (X, Y) in netDxf 2.1.1.
        /// </summary>
        private static IGeometryObject? MapLwPolyline(LwPolyline lwp, int tessSegs)
        {
            var verts = lwp.Vertexes;
            if (verts is null || verts.Count < 2) return null;

            var coords = new List<NtsCoordinate>(verts.Count * 2);
            int count  = verts.Count;

            for (int i = 0; i < count; i++)
            {
                var v      = verts[i];
                var isLast = i == count - 1;
                double bulge = v.Bulge;

                if (!isLast || lwp.IsClosed)
                {
                    var next = isLast ? verts[0] : verts[i + 1];

                    if (Math.Abs(bulge) > 1e-9)
                    {
                        // Vector2 has .X and .Y
                        var arcPts = CurveTessellationHelper.TessellateBulgeSegment(
                            v.Position.X,    v.Position.Y,
                            next.Position.X, next.Position.Y,
                            bulge, tessSegs);
                        coords.AddRange(arcPts);
                    }
                    else
                    {
                        coords.Add(new NtsCoordinate(v.Position.X, v.Position.Y));
                    }
                }
                else
                {
                    coords.Add(new NtsCoordinate(v.Position.X, v.Position.Y));
                }
            }

            if (coords.Count < 2) return null;

            if (lwp.IsClosed && coords.Count >= 3)
            {
                CloseRing(coords);
                return ToGjsPolygon(coords);
            }

            return ToGjsLineString(coords);
        }

        /// <summary>
        /// Maps a legacy 3D Polyline.
        /// PolylineVertex.Position is Vector3 (X, Y, Z) in netDxf 2.1.1.
        /// </summary>
        private static IGeometryObject? MapPolyline(Polyline poly)
        {
            var verts = poly.Vertexes;
            if (verts is null || verts.Count < 2) return null;

            // Vector3 exposes .X and .Y directly
            var coords = verts
                .Select(v => new NtsCoordinate(v.Position.X, v.Position.Y))
                .ToList();

            if (poly.IsClosed && coords.Count >= 3)
            {
                CloseRing(coords);
                return ToGjsPolygon(coords);
            }

            return ToGjsLineString(coords);
        }

        /// <summary>
        /// Maps a two-point Line entity.
        /// Line.StartPoint and Line.EndPoint are Vector3 in netDxf 2.1.1.
        /// </summary>
        private static IGeometryObject? MapLine(Line line)
        {
            var start = new NtsCoordinate(line.StartPoint.X, line.StartPoint.Y);
            var end   = new NtsCoordinate(line.EndPoint.X,   line.EndPoint.Y);
            if (start.Equals2D(end)) return null; // degenerate

            return ToGjsLineString(new List<NtsCoordinate> { start, end });
        }

        /// <summary>
        /// Maps an Arc entity via trigonometric tessellation.
        /// Arc.Center is Vector3; Arc.Radius, StartAngle, EndAngle are double.
        /// </summary>
        private static IGeometryObject? MapArc(Arc arc, int tessSegs)
        {
            var coords = CurveTessellationHelper.TessellateArc(
                arc.Center.X, arc.Center.Y,
                arc.Radius,
                arc.StartAngle, arc.EndAngle,
                isCcw: true,
                segmentsPerCircle: tessSegs);

            return CurveTessellationHelper.HasMinimumPoints(coords)
                ? ToGjsLineString(coords)
                : null;
        }

        /// <summary>
        /// Maps a Circle entity to a closed polygon ring.
        /// Circle.Center is Vector3; Circle.Radius is double.
        /// </summary>
        private static IGeometryObject? MapCircle(Circle circle, int tessSegs)
        {
            var coords = CurveTessellationHelper.TessellateCircle(
                circle.Center.X, circle.Center.Y,
                circle.Radius,
                segmentsPerCircle: tessSegs);

            return CurveTessellationHelper.HasMinimumPoints(coords, 4)
                ? ToGjsPolygon(coords)
                : null;
        }

        /// <summary>
        /// Maps a Point entity.
        /// Point.Position is Vector3 in netDxf 2.1.1.
        /// GeoJSON Position takes (latitude=Y, longitude=X).
        /// </summary>
        private static IGeometryObject MapPoint(netDxf.Entities.Point pt)
            => new GjsPoint(new GjsPosition(pt.Position.Y, pt.Position.X));

        /// <summary>
        /// Maps a Hatch entity by extracting its boundary path edges.
        ///
        /// netDxf 2.1.1 HatchBoundaryPath edge types and their fields:
        ///   Line     → .Start (Vector2),  .End (Vector2)  [fields]
        ///   Arc      → .Center (Vector2), .Radius, .StartAngle, .EndAngle, .IsCounterclockwise [fields]
        ///   Ellipse  → .Center (Vector2), .EndMajorAxis (Vector2), .MinorRatio,
        ///              .StartAngle, .EndAngle, .IsCounterclockwise [fields]
        ///   Polyline → .Vertexes (Vector3[]) [field], .IsClosed [property]
        /// </summary>
        private static IGeometryObject? MapHatch(Hatch hatch, int tessSegs)
        {
            if (hatch.BoundaryPaths is null || hatch.BoundaryPaths.Count == 0)
                return null;

            // Process the first (outer) boundary path
            var path   = hatch.BoundaryPaths[0];
            var coords = new List<NtsCoordinate>();

            foreach (var edge in path.Edges)
            {
                switch (edge)
                {
                    case HatchBoundaryPath.Line lineEdge:
                        // Start is a Vector2 field
                        coords.Add(new NtsCoordinate(lineEdge.Start.X, lineEdge.Start.Y));
                        break;

                    case HatchBoundaryPath.Arc arcEdge:
                        // All fields: Center (Vector2), Radius, StartAngle, EndAngle, IsCounterclockwise
                        var arcCoords = CurveTessellationHelper.TessellateArc(
                            arcEdge.Center.X, arcEdge.Center.Y,
                            arcEdge.Radius,
                            arcEdge.StartAngle, arcEdge.EndAngle,
                            arcEdge.IsCounterclockwise,
                            tessSegs);
                        coords.AddRange(arcCoords);
                        break;

                    case HatchBoundaryPath.Ellipse ellEdge:
                        // EndMajorAxis is a Vector2 offset from Center; its length is the semi-major axis.
                        // MinorRatio = semi-minor / semi-major (between 0 and 1).
                        double semiMajor  = Math.Sqrt(
                            ellEdge.EndMajorAxis.X * ellEdge.EndMajorAxis.X +
                            ellEdge.EndMajorAxis.Y * ellEdge.EndMajorAxis.Y);
                        // Approximate as a circular arc using the semi-major axis radius.
                        var ellCoords = CurveTessellationHelper.TessellateArc(
                            ellEdge.Center.X, ellEdge.Center.Y,
                            semiMajor,
                            ellEdge.StartAngle, ellEdge.EndAngle,
                            ellEdge.IsCounterclockwise,
                            tessSegs);
                        coords.AddRange(ellCoords);
                        break;

                    case HatchBoundaryPath.Polyline polyEdge:
                        // Vertexes is a Vector3[] field
                        if (polyEdge.Vertexes is not null)
                        {
                            foreach (var v in polyEdge.Vertexes)
                                coords.Add(new NtsCoordinate(v.X, v.Y));
                        }
                        break;
                }
            }

            if (coords.Count < 2) return null;

            if (path.PathType.HasFlag(HatchBoundaryPathTypeFlags.Outermost) && coords.Count >= 3)
            {
                CloseRing(coords);
                return ToGjsPolygon(coords);
            }

            return ToGjsLineString(coords);
        }

        // ================================================================== //
        //  Private — GeoJSON geometry builders                               //
        // ================================================================== //

        /// <summary>
        /// Converts an ordered list of NTS coordinates to a GeoJSON LineString.
        /// GeoJSON Position signature: (latitude, longitude) → (Y, X) from Cartesian.
        /// </summary>
        private static GjsLineStr ToGjsLineString(IList<NtsCoordinate> coords)
        {
            var positions = coords
                .Select(c => (GjsIPos)new GjsPosition(c.Y, c.X))
                .ToList();
            return new GjsLineStr(positions);
        }

        /// <summary>
        /// Converts a closed coordinate ring to a GeoJSON Polygon.
        /// The ring must already be closed (first == last coordinate).
        /// </summary>
        private static GjsPolygon ToGjsPolygon(IList<NtsCoordinate> coords)
        {
            var positions = coords
                .Select(c => (GjsIPos)new GjsPosition(c.Y, c.X))
                .ToList();
            // GeoJSON.Net Polygon takes a list of LineString rings
            var ring = new GjsLineStr(positions);
            return new GjsPolygon(new List<GjsLineStr> { ring });
        }

        /// <summary>Ensures the coordinate list forms a closed ring (first == last).</summary>
        private static void CloseRing(List<NtsCoordinate> coords)
        {
            if (coords.Count > 0 && !coords[0].Equals2D(coords[^1]))
                coords.Add(new NtsCoordinate(coords[0].X, coords[0].Y));
        }

        // ================================================================== //
        //  Private — Properties builder                                       //
        // ================================================================== //

        /// <summary>
        /// Builds the GeoJSON Feature Properties dictionary from entity and layer metadata.
        /// No layer names are hardcoded; all metadata is derived dynamically.
        /// </summary>
        private static Dictionary<string, object?> BuildProperties(EntityObject entity)
        {
            var layer = entity.Layer ?? Layer.Default;

            // Determine the effective ACI color (entity override vs layer color)
            // AciColor.IsByLayer is true when the entity defers to its layer's color.
            var aciColor = (entity.Color is not null && !entity.Color.IsByLayer)
                ? entity.Color
                : layer.Color;

            string colorHex = aciColor is not null ? AciToHex(aciColor.Index) : "#FFFFFF";

            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                // UI layer classification
                ["layerType"]       = DetermineLayerType(entity),
                // Layer metadata — populated dynamically from whatever layers exist in the DXF
                ["layerName"]       = layer.Name,
                ["layerColor"]      = colorHex,
                ["layerLineweight"] = layer.Lineweight == Lineweight.Default
                                          ? "default"
                                          : layer.Lineweight.ToString(),
                ["layerLinetype"]   = layer.Linetype?.Name ?? "CONTINUOUS",
                ["layerIsVisible"]  = layer.IsVisible,
                ["layerIsFrozen"]   = layer.IsFrozen,

                // Entity metadata
                ["entityType"]      = entity.Type.ToString(),
                ["entityHandle"]    = entity.Handle,
                ["entityLinetype"]  = entity.Linetype?.Name,
                ["entityLineweight"]= entity.Lineweight == Lineweight.Default
                                          ? null
                                          : (object?)entity.Lineweight.ToString(),
            };
        }

        // ================================================================== //
        //  Private — Validation                                               //
        // ================================================================== //

        private static void ValidateFile(IFormFile file)
        {
            if (file is null)
                throw new ArgumentNullException(nameof(file), "No file was provided.");

            if (file.Length == 0)
                throw new ArgumentException("The uploaded file is empty.", nameof(file));

            if (file.Length > MaxFileSizeBytes)
                throw new ArgumentException(
                    $"File size ({file.Length / 1024 / 1024} MB) exceeds the maximum allowed 100 MB.",
                    nameof(file));

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext != SupportedExtension)
                throw new NotSupportedException(
                    $"File type '{ext}' is not supported. Only '{SupportedExtension}' files are accepted. " +
                    "For DWG files, please convert to DXF first using a tool such as ODA File Converter.");
        }

        private static void ValidateSrid(int srid)
        {
            if (srid is not (4326 or 32648 or 32649))
                throw new ArgumentOutOfRangeException(nameof(srid),
                    $"Unsupported SRID '{srid}'. Supported: 4326 (WGS-84), 32648 (UTM 48N), 32649 (UTM 49N).");
        }

        // ================================================================== //
        //  Private — AutoCAD Color Index → CSS hex                           //
        // ================================================================== //

        /// <summary>
        /// Maps a standard ACI (AutoCAD Color Index, 0-255) value to a CSS hex color string.
        /// The first 15 standard ACI colors are mapped precisely.
        /// Remaining colors use a deterministic hash-based approximation.
        /// </summary>
        private static string AciToHex(short aci) => aci switch
        {
            0  => "#000000", // ByBlock placeholder
            1  => "#FF0000", // Red
            2  => "#FFFF00", // Yellow
            3  => "#00FF00", // Green
            4  => "#00FFFF", // Cyan
            5  => "#0000FF", // Blue
            6  => "#FF00FF", // Magenta
            7  => "#FFFFFF", // White
            8  => "#414141", // Dark grey
            9  => "#808080", // Grey
            10 => "#FF0000",
            11 => "#FF7F7F",
            12 => "#A50000",
            13 => "#A57F7F",
            14 => "#7F0000",
            15 => "#7F5F5F",
            _  => $"#{(aci * 13 % 256):X2}{(aci * 37 % 256):X2}{(aci * 71 % 256):X2}"
        };

        // ================================================================== //
        //  Private - Layer Classification & Surface Buffer Generator         //
        // ================================================================== //

        private static string DetermineLayerType(EntityObject entity)
        {
            var layerName = entity.Layer?.Name?.ToUpperInvariant() ?? string.Empty;
            if (layerName.Contains("EDGE") || layerName.Contains("BIEN") || layerName.Contains("LE"))
                return "Edges";
            if (layerName.Contains("BOUND") || layerName.Contains("RANH") || layerName.Contains("GIOI"))
                return "Boundaries";
            if (layerName.Contains("SURFACE") || layerName.Contains("MAT"))
                return "RoadSurface";
            return "Centerline";
        }

        private void GenerateRoadSurfaceBuffers(
            List<Feature> features,
            Dictionary<string, int> featuresByLayer,
            double roadWidth,
            int srid)
        {
            double bufferDistance = (srid == 4326) ? (roadWidth / 2.0) / MetersPerDegree : (roadWidth / 2.0);
            var bufferFeatures = new List<Feature>();
            var ntsFactory = new NtsGeometryFactory(new NtsPrecisionModel(), srid);

            foreach (var feat in features)
            {
                if (feat.Properties.TryGetValue("layerType", out var lt) &&
                    lt is string typeStr &&
                    string.Equals(typeStr, "Centerline", StringComparison.OrdinalIgnoreCase) &&
                    feat.Geometry is GjsLineStr lineStr &&
                    lineStr.Coordinates.Count >= 2)
                {
                    try
                    {
                        var ntsCoords = lineStr.Coordinates
                            .Select(c => new NtsCoordinate(c.Longitude, c.Latitude))
                            .ToArray();
                        var ntsLine = ntsFactory.CreateLineString(ntsCoords);
                        var bufferedGeom = ntsLine.Buffer(bufferDistance);

                        if (bufferedGeom is NtsPolygon ntsPoly && !ntsPoly.IsEmpty)
                        {
                            var shellCoords = ntsPoly.ExteriorRing.Coordinates
                                .Select(c => (GjsIPos)new GjsPosition(c.Y, c.X))
                                .ToList();
                            var gjsRing = new GjsLineStr(shellCoords);
                            var gjsPoly = new GjsPolygon(new List<GjsLineStr> { gjsRing });

                            var bufferProps = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["layerName"]        = "ROAD_SURFACE",
                                ["layerType"]        = "RoadSurface",
                                ["layerColor"]       = "#3B82F6",
                                ["layerLineweight"]  = "default",
                                ["layerLinetype"]    = "CONTINUOUS",
                                ["layerIsVisible"]   = true,
                                ["layerIsFrozen"]    = false,
                                ["entityType"]       = "Polygon",
                                ["entityHandle"]     = "GEN_SURFACE",
                                ["isGeneratedBuffer"] = true
                            };

                            bufferFeatures.Add(new Feature(gjsPoly, bufferProps));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to create road surface buffer for centerline feature.");
                    }
                }
            }

            if (bufferFeatures.Count > 0)
            {
                features.AddRange(bufferFeatures);
                featuresByLayer["ROAD_SURFACE"] = featuresByLayer.TryGetValue("ROAD_SURFACE", out var cur)
                    ? cur + bufferFeatures.Count
                    : bufferFeatures.Count;
            }
        }

        private async Task PersistToDatabaseAsync(
            string fileName,
            int srid,
            List<Feature> features,
            CancellationToken cancellationToken)
        {
            try
            {
                var ntsFactory = new NtsGeometryFactory(new NtsPrecisionModel(), srid);
                var drawing = new CadDrawing
                {
                    Id          = Guid.NewGuid(),
                    FileName    = fileName,
                    ParsedAtUtc = DateTime.UtcNow,
                    Srid        = srid
                };

                foreach (var feat in features)
                {
                    NtsGeometry.Geometry? geom = null;
                    switch (feat.Geometry)
                    {
                        case GjsLineStr ls when ls.Coordinates.Count >= 2:
                            var lineCoords = ls.Coordinates
                                .Select(c => new NtsCoordinate(c.Longitude, c.Latitude))
                                .ToArray();
                            geom = ntsFactory.CreateLineString(lineCoords);
                            break;

                        case GjsPolygon poly when poly.Coordinates.Count > 0 && poly.Coordinates[0].Coordinates.Count >= 4:
                            var shellCoords = poly.Coordinates[0].Coordinates
                                .Select(c => new NtsCoordinate(c.Longitude, c.Latitude))
                                .ToArray();
                            geom = ntsFactory.CreatePolygon(shellCoords);
                            break;

                        case GjsPoint pt:
                            geom = ntsFactory.CreatePoint(new NtsCoordinate(pt.Coordinates.Longitude, pt.Coordinates.Latitude));
                            break;
                    }

                    if (geom is not null)
                    {
                        geom.SRID = srid;
                        string layerName = feat.Properties.TryGetValue("layerName", out var ln) && ln is string sLn ? sLn : "0";
                        string? layerColor = feat.Properties.TryGetValue("layerColor", out var lc) && lc is string sLc ? sLc : null;

                        drawing.GeometryFeatures.Add(new CadGeometryFeature
                        {
                            Id           = Guid.NewGuid(),
                            CadDrawingId = drawing.Id,
                            LayerName    = layerName,
                            LayerColor   = layerColor,
                            Geometry     = geom
                        });
                    }
                }

                _db.CadDrawings.Add(drawing);
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Persisted CadDrawing '{DrawingId}' with {Count} spatial features to SQL Server.", drawing.Id, drawing.GeometryFeatures.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Database persistence encountered an error. Proceeding without failing the parse request.");
            }
        }
    }
}
