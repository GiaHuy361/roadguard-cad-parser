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
using NetTopologySuite.LinearReferencing;
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
        public async Task<List<string>> GetLayersAsync(
            IFormFile file,
            CancellationToken cancellationToken = default)
        {
            ValidateFile(file);

            var tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.dxf");
            try
            {
                await using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await file.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    var json = await RunPythonBridgeAsync($"get-layers \"{tempFile}\"", cancellationToken).ConfigureAwait(false);
                    var pyRes = Newtonsoft.Json.JsonConvert.DeserializeObject<PythonLayersResult>(json);
                    if (pyRes is not null && pyRes.Success && pyRes.Layers.Count > 0)
                    {
                        return pyRes.Layers;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Python bridge get-layers failed; falling back to netDxf.");
                }

                // Fallback to netDxf
                using var stream = new MemoryStream();
                await using (var fs = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    await fs.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
                }
                stream.Position = 0;

                return await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var dxfDoc = LoadDxfDocument(stream, file.FileName);
                    if (dxfDoc is null)
                    {
                        throw new InvalidOperationException(
                            $"DxfDocument.Load returned null for '{file.FileName}'. The file may be empty or corrupted.");
                    }

                    var layerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var layer in dxfDoc.Layers)
                    {
                        if (!string.IsNullOrWhiteSpace(layer.Name))
                            layerNames.Add(layer.Name);
                    }
                    foreach (var e in dxfDoc.LwPolylines) if (!string.IsNullOrWhiteSpace(e.Layer?.Name)) layerNames.Add(e.Layer.Name);
                    foreach (var e in dxfDoc.Polylines)   if (!string.IsNullOrWhiteSpace(e.Layer?.Name)) layerNames.Add(e.Layer.Name);
                    foreach (var e in dxfDoc.Lines)       if (!string.IsNullOrWhiteSpace(e.Layer?.Name)) layerNames.Add(e.Layer.Name);
                    foreach (var e in dxfDoc.Arcs)        if (!string.IsNullOrWhiteSpace(e.Layer?.Name)) layerNames.Add(e.Layer.Name);
                    foreach (var e in dxfDoc.Circles)     if (!string.IsNullOrWhiteSpace(e.Layer?.Name)) layerNames.Add(e.Layer.Name);
                    foreach (var e in dxfDoc.Splines)     if (!string.IsNullOrWhiteSpace(e.Layer?.Name)) layerNames.Add(e.Layer.Name);
                    foreach (var e in dxfDoc.Points)      if (!string.IsNullOrWhiteSpace(e.Layer?.Name)) layerNames.Add(e.Layer.Name);
                    foreach (var e in dxfDoc.Hatches)     if (!string.IsNullOrWhiteSpace(e.Layer?.Name)) layerNames.Add(e.Layer.Name);

                    return layerNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }


        /// <inheritdoc/>
        public async Task<CadParseRoadResponse> ParseRoadAsync(
            IFormFile file,
            double roadWidth = 7.0,
            string? centerlineLayerName = null,
            double? centralMeridian = null,
            CancellationToken cancellationToken = default)
        {
            ValidateFile(file);

            var tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.dxf");
            try
            {
                await using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await file.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
                }

                var bridgeArgs = $"parse-road \"{tempFile}\" --width {roadWidth}";
                if (!string.IsNullOrWhiteSpace(centerlineLayerName))
                {
                    bridgeArgs += $" --layer \"{centerlineLayerName}\"";
                }
                if (centralMeridian.HasValue && centralMeridian.Value > 0)
                {
                    bridgeArgs += $" --cm {centralMeridian.Value}";
                }

                var json = await RunPythonBridgeAsync(bridgeArgs, cancellationToken).ConfigureAwait(false);
                var result = Newtonsoft.Json.JsonConvert.DeserializeObject<CadParseRoadResponse>(json);
                if (result is null || !result.Success)
                {
                    _logger.LogWarning("Python parse-road bridge returned unsuccessful or empty response.");
                    return result ?? new CadParseRoadResponse
                    {
                        Success = false,
                        Error   = "ParseRoadFailed",
                        Detail  = "Failed to parse road centerline and polygon from CAD drawing."
                    };
                }

                return result;
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { /* best-effort cleanup */ }
                }
            }
        }

        /// <inheritdoc/>
        public async Task<CadRenderOverlayResponse> RenderOverlayAsync(
            IFormFile file,
            string? centerlineLayerName = null,
            double roadWidth = 7.0,
            int outputSizePx = 2048,
            CancellationToken cancellationToken = default)
        {
            ValidateFile(file);

            var tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.dxf");
            try
            {
                await using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await file.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
                }

                var bridgeArgs = $"render \"{tempFile}\" --size {outputSizePx} --width {roadWidth}";
                if (!string.IsNullOrWhiteSpace(centerlineLayerName))
                {
                    bridgeArgs += $" --layer \"{centerlineLayerName}\"";
                }

                var json = await RunPythonBridgeAsync(bridgeArgs, cancellationToken).ConfigureAwait(false);
                var result = Newtonsoft.Json.JsonConvert.DeserializeObject<CadRenderOverlayResponse>(json);
                if (result is null || !result.Success)
                {
                    throw new InvalidOperationException(result?.Detail ?? result?.Error ?? "Raster render failed in Python bridge.");
                }

                return result;
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }

        /// <inheritdoc/>
        public async Task<GeoJsonResponse> ParseDxfAsync(
            IFormFile file,
            int srid                 = 4326,
            int tessellationSegments = 72,
            double segmentLength     = 100.0,
            double roadWidth         = 3.5,
            double slabLength        = 4.0,
            string? centerlineLayerName = null,
            double? centralMeridian  = null,
            CancellationToken cancellationToken = default)
        {
            // ?? 1. Validate inputs ????????????????????????????????????????? //
            ValidateFile(file);
            ValidateSrid(srid);
            tessellationSegments = Math.Max(MinTessellationSegs, tessellationSegments);
            if (segmentLength <= 0) segmentLength = 100.0;
            if (roadWidth <= 0)     roadWidth     = 3.5;
            if (slabLength <= 0)    slabLength    = 4.0;

            var stopwatch = Stopwatch.StartNew();

            _logger.LogInformation(
                "Starting DXF parse: file={FileName}, size={Size}B, srid={Srid}, tessSegs={Segs}, layer={Layer}, cm={Cm}",
                file.FileName, file.Length, srid, tessellationSegments, centerlineLayerName ?? "(auto-detect)", centralMeridian);

            var tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.dxf");
            try
            {
                await using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await file.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
                }

                // ?? Try Python Bridge first for heavy & modern CAD ingestion ? //
                PythonParseResult? pyParseResult = null;
                try
                {
                    var bridgeArgs = $"parse \"{tempFile}\" --srid {srid}";
                    if (!string.IsNullOrWhiteSpace(centerlineLayerName))
                    {
                        bridgeArgs += $" --layer \"{centerlineLayerName}\"";
                    }
                    if (centralMeridian.HasValue && centralMeridian.Value > 0)
                    {
                        bridgeArgs += $" --cm {centralMeridian.Value}";
                    }

                    var json = await RunPythonBridgeAsync(bridgeArgs, cancellationToken).ConfigureAwait(false);
                    pyParseResult = Newtonsoft.Json.JsonConvert.DeserializeObject<PythonParseResult>(json);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Python bridge parse failed or threw exception; attempting netDxf fallback.");
                }

                if (pyParseResult is not null)
                {
                    if (!pyParseResult.Success)
                    {
                        throw new InvalidOperationException(
                            pyParseResult.Detail ?? pyParseResult.Error ?? "CAD parsing failed in Python bridge.");
                    }

                    var chosenCenterlineLayer = pyParseResult.DetectedLayer ?? centerlineLayerName ?? "ROAD_CENTERLINE";
                    var feats = new List<Feature>();
                    var warns = new List<string>();
                    var byLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                    foreach (var item in pyParseResult.Features)
                    {
                        var itemCoords = item.GetCoordinates();
                        if (itemCoords == null || itemCoords.Count < 2) continue;

                        var coords = new List<NtsCoordinate>();
                        foreach (var pt in itemCoords)
                        {
                            if (pt.Count >= 2)
                            {
                                double x = pt[0];
                                double y = pt[1];
                                double z = pt.Count >= 3 ? pt[2] : (item.Properties?.Elevation ?? item.Elevation);
                                coords.Add(new CoordinateZ(x, y, z));
                            }
                        }

                        if (coords.Count < 2) continue;

                        var geom = ToGjsLineString(coords);
                        var segName = item.GetSegment() ?? $"SEG-{feats.Count + 1:D2}";
                        var props = new Dictionary<string, object>
                        {
                            ["layer"] = chosenCenterlineLayer,
                            ["layerName"] = chosenCenterlineLayer,
                            ["layerType"] = "Centerline",
                            ["elevation"] = item.Properties?.Elevation ?? item.Elevation,
                            ["segment"] = segName
                        };

                        feats.Add(new Feature(geom, props));
                        byLayer[chosenCenterlineLayer] = byLayer.TryGetValue(chosenCenterlineLayer, out var c) ? c + 1 : 1;
                    }

                    if (feats.Count == 0)
                    {
                        throw new InvalidOperationException(
                            $"No valid linear entities found on detected layer '{chosenCenterlineLayer}' to represent the road centerline.");
                    }

                    // Physical stationing points via Linear Referencing
                    var stations = GenerateStationPoints(feats, byLayer, segmentLength, srid, chosenCenterlineLayer);

                    // Generate RoadSurface buffer polygon
                    GenerateRoadSurfaceBuffers(feats, byLayer, roadWidth, srid, chosenCenterlineLayer);

                    // Analytics calculation
                    stopwatch.Stop();
                    double rawLen = 0.0;
                    foreach (var feat in feats)
                    {
                        if (feat.Properties.TryGetValue("isGeneratedBuffer", out var isGen) && isGen is true)
                            continue;
                        if (feat.Properties.TryGetValue("layerType", out var lt) &&
                            lt is string sLt && string.Equals(sLt, "StationPoint", StringComparison.OrdinalIgnoreCase))
                            continue;

                        switch (feat.Geometry)
                        {
                            case GjsLineStr ls:
                                var lsCoords = ls.Coordinates;
                                for (int i = 1; i < lsCoords.Count; i++)
                                {
                                    double dx = lsCoords[i].Longitude - lsCoords[i - 1].Longitude;
                                    double dy = lsCoords[i].Latitude  - lsCoords[i - 1].Latitude;
                                    rawLen += Math.Sqrt(dx * dx + dy * dy);
                                }
                                break;
                            case GjsPolygon poly:
                                if (poly.Coordinates.Count > 0)
                                {
                                    var ring = poly.Coordinates[0].Coordinates;
                                    for (int i = 1; i < ring.Count; i++)
                                    {
                                        double dx = ring[i].Longitude - ring[i - 1].Longitude;
                                        double dy = ring[i].Latitude  - ring[i - 1].Latitude;
                                        rawLen += Math.Sqrt(dx * dx + dy * dy);
                                    }
                                }
                                break;
                        }
                    }

                    double totalMeters  = srid == 4326 ? rawLen * MetersPerDegree : rawLen;
                    double totalAreaSqm = Math.Round(totalMeters * roadWidth, 2);
                    int estimatedSlabs  = (int)Math.Ceiling(totalMeters / slabLength);
                    int roadSegments    = stations.Count > 0 ? stations.Count : (int)Math.Ceiling(totalMeters / segmentLength);

                    var engAnalytics = new EngineeringAnalytics
                    {
                        TotalLengthMeters      = Math.Round(totalMeters, 3),
                        TotalAreaSqm           = totalAreaSqm,
                        EstimatedConcreteSlabs = estimatedSlabs,
                        RoadSegments           = roadSegments,
                        ProcessingTimeMs       = stopwatch.ElapsedMilliseconds
                    };

                    _logger.LogInformation(
                        "Python bridge parse complete: {Count} features, layer='{Layer}', {Elapsed}ms.",
                        feats.Count, chosenCenterlineLayer, stopwatch.ElapsedMilliseconds);

                    await PersistToDatabaseAsync(file.FileName, srid, feats, cancellationToken).ConfigureAwait(false);

                    return new GeoJsonResponse
                    {
                        SourceFileName      = file.FileName,
                        Srid                = srid,
                        TotalFeatureCount   = feats.Count,
                        FeatureCountByLayer = byLayer,
                        FeatureCollection   = new FeatureCollection(feats),
                        Warnings            = warns,
                        Analytics           = engAnalytics
                    };
                }

                // ?? Fallback to legacy netDxf parsing ????????????????????????? //
                using var stream = new MemoryStream();
                await using (var fs = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    await fs.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
                }
                stream.Position = 0;

                var (features, warnings, featuresByLayer, analytics) = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var dxfDoc = LoadDxfDocument(stream, file.FileName);
                    if (dxfDoc is null)
                        throw new InvalidOperationException(
                            $"DxfDocument.Load returned null for '{file.FileName}'. The file may be empty or corrupted.");

                    cancellationToken.ThrowIfCancellationRequested();

                    // Dynamic layer validation: if centerlineLayerName is provided, verify presence and valid geometries
                    if (!string.IsNullOrWhiteSpace(centerlineLayerName))
                    {
                        bool layerExists = dxfDoc.Layers.Any(l => string.Equals(l.Name, centerlineLayerName, StringComparison.OrdinalIgnoreCase))
                            || dxfDoc.LwPolylines.Any(e => string.Equals(e.Layer?.Name, centerlineLayerName, StringComparison.OrdinalIgnoreCase))
                            || dxfDoc.Polylines.Any(e => string.Equals(e.Layer?.Name, centerlineLayerName, StringComparison.OrdinalIgnoreCase))
                            || dxfDoc.Lines.Any(e => string.Equals(e.Layer?.Name, centerlineLayerName, StringComparison.OrdinalIgnoreCase))
                            || dxfDoc.Splines.Any(e => string.Equals(e.Layer?.Name, centerlineLayerName, StringComparison.OrdinalIgnoreCase))
                            || dxfDoc.Arcs.Any(e => string.Equals(e.Layer?.Name, centerlineLayerName, StringComparison.OrdinalIgnoreCase));

                        if (!layerExists)
                        {
                            throw new InvalidOperationException(
                                $"Layer '{centerlineLayerName}' was not found in the DXF file. Please select a valid layer from the file.");
                        }

                        bool hasLinearGeometries =
                            dxfDoc.LwPolylines.Any(e => string.Equals(e.Layer?.Name, centerlineLayerName, StringComparison.OrdinalIgnoreCase) && e.Vertexes.Count >= 2) ||
                            dxfDoc.Polylines.Any(e => string.Equals(e.Layer?.Name, centerlineLayerName, StringComparison.OrdinalIgnoreCase) && e.Vertexes.Count >= 2) ||
                            dxfDoc.Lines.Any(e => string.Equals(e.Layer?.Name, centerlineLayerName, StringComparison.OrdinalIgnoreCase)) ||
                            dxfDoc.Splines.Any(e => string.Equals(e.Layer?.Name, centerlineLayerName, StringComparison.OrdinalIgnoreCase)) ||
                            dxfDoc.Arcs.Any(e => string.Equals(e.Layer?.Name, centerlineLayerName, StringComparison.OrdinalIgnoreCase));

                        if (!hasLinearGeometries)
                        {
                            throw new InvalidOperationException(
                                $"The selected layer '{centerlineLayerName}' contains no valid polylines or lines to represent the road centerline.");
                        }
                    }

                    var feats   = new List<Feature>();
                    var warns   = new List<string>();
                    var byLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                    // Step 3: Complex entities (Arc, Spline, Polyline with bulges)
                    ProcessAllEntities(dxfDoc, feats, warns, byLayer, tessellationSegments, srid, centerlineLayerName);

                    // Step 2: Physical stationing points via Linear Referencing
                    var stations = GenerateStationPoints(feats, byLayer, segmentLength, srid, centerlineLayerName);

                    // Step 4 Bonus: Generate RoadSurface buffer polygon for Centerline features
                    GenerateRoadSurfaceBuffers(feats, byLayer, roadWidth, srid, centerlineLayerName);

                    // Analytics calculation
                    stopwatch.Stop();
                    double rawLen = 0.0;
                    foreach (var feat in feats)
                    {
                        if (feat.Properties.TryGetValue("isGeneratedBuffer", out var isGen) && isGen is true)
                            continue;
                        if (feat.Properties.TryGetValue("layerType", out var lt) &&
                            lt is string sLt && string.Equals(sLt, "StationPoint", StringComparison.OrdinalIgnoreCase))
                            continue;

                        switch (feat.Geometry)
                        {
                            case GjsLineStr ls:
                                var lsCoords = ls.Coordinates;
                                for (int i = 1; i < lsCoords.Count; i++)
                                {
                                    double dx = lsCoords[i].Longitude - lsCoords[i - 1].Longitude;
                                    double dy = lsCoords[i].Latitude  - lsCoords[i - 1].Latitude;
                                    rawLen += Math.Sqrt(dx * dx + dy * dy);
                                }
                                break;
                            case GjsPolygon poly:
                                if (poly.Coordinates.Count > 0)
                                {
                                    var ring = poly.Coordinates[0].Coordinates;
                                    for (int i = 1; i < ring.Count; i++)
                                    {
                                        double dx = ring[i].Longitude - ring[i - 1].Longitude;
                                        double dy = ring[i].Latitude  - ring[i - 1].Latitude;
                                        rawLen += Math.Sqrt(dx * dx + dy * dy);
                                    }
                                }
                                break;
                        }
                    }

                    double totalMeters  = srid == 4326 ? rawLen * MetersPerDegree : rawLen;
                    double totalAreaSqm = Math.Round(totalMeters * roadWidth, 2);
                    int estimatedSlabs  = (int)Math.Ceiling(totalMeters / slabLength);
                    int roadSegments    = stations.Count > 0 ? stations.Count : (int)Math.Ceiling(totalMeters / segmentLength);

                    var engAnalytics = new EngineeringAnalytics
                    {
                        TotalLengthMeters      = Math.Round(totalMeters, 3),
                        TotalAreaSqm           = totalAreaSqm,
                        EstimatedConcreteSlabs = estimatedSlabs,
                        RoadSegments           = roadSegments,
                        ProcessingTimeMs       = stopwatch.ElapsedMilliseconds
                    };

                    return (feats, warns, byLayer, engAnalytics);
                }, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "netDxf fallback complete: {Count} features, {Layers} layer(s), {Warns} warning(s).",
                    features.Count, featuresByLayer.Count, warnings.Count);

                await PersistToDatabaseAsync(file.FileName, srid, features, cancellationToken).ConfigureAwait(false);

                return new GeoJsonResponse
                {
                    SourceFileName      = file.FileName,
                    Srid                = srid,
                    TotalFeatureCount   = features.Count,
                    FeatureCountByLayer = featuresByLayer,
                    FeatureCollection   = new FeatureCollection(features),
                    Warnings            = warnings,
                    Analytics           = analytics
                };
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try { File.Delete(tempFile); } catch { }
                }
            }
        }

        private async Task<string> RunPythonBridgeAsync(string arguments, CancellationToken cancellationToken)
        {
            var scriptPath = Path.Combine(Directory.GetCurrentDirectory(), "Services", "PythonBridge", "cad_bridge.py");
            if (!File.Exists(scriptPath))
            {
                scriptPath = Path.Combine(AppContext.BaseDirectory, "Services", "PythonBridge", "cad_bridge.py");
            }

            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException($"Python bridge script not found at '{scriptPath}'.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = $"\"{scriptPath}\" {arguments}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
            {
                throw new InvalidOperationException($"Python bridge failed: {error}");
            }

            return output;
        }

        private sealed class PythonLayersResult
        {
            public bool Success { get; set; }
            public List<string> Layers { get; set; } = new();
            public string? Error { get; set; }
            public string? Detail { get; set; }
        }

        private sealed class PythonGeometryData
        {
            public string Type { get; set; } = "LineString";
            public List<List<double>> Coordinates { get; set; } = new();
        }

        private sealed class PythonPropertiesData
        {
            public string? Layer { get; set; }
            public string? Segment { get; set; }
            public double Elevation { get; set; }
        }

        private sealed class PythonFeatureData
        {
            public string Type { get; set; } = "Feature";
            public PythonPropertiesData? Properties { get; set; }
            public PythonGeometryData? Geometry { get; set; }

            // Flat/backward-compatibility fields
            public string Layer { get; set; } = string.Empty;
            public string? Segment { get; set; }
            public double Elevation { get; set; }
            public List<List<double>>? Coordinates { get; set; }

            public List<List<double>> GetCoordinates()
                => Geometry?.Coordinates ?? Coordinates ?? new List<List<double>>();

            public string GetLayer(string fallback = "")
                => Properties?.Layer ?? (!string.IsNullOrEmpty(Layer) ? Layer : fallback);

            public string? GetSegment()
                => Properties?.Segment ?? Segment;
        }

        private sealed class PythonParseResult
        {
            public string? Type { get; set; }
            public bool Success { get; set; } = true;
            public string? DetectedLayer { get; set; }
            public int TotalEntities { get; set; }
            public List<string> Layers { get; set; } = new();
            public List<PythonFeatureData> Features { get; set; } = new();
            public string? Error { get; set; }
            public string? Detail { get; set; }
        }

        // ================================================================== //
        //  Private — Entity dispatch layer                                    //
        // ================================================================== //

        /// <summary>
        /// Dispatches every supported entity collection directly from the DxfDocument.
        /// In netDxf 2.1.1, entity collections are top-level properties on DxfDocument,
        /// NOT under a nested .Entities property.
        /// </summary>
        private static IEnumerable<T> FilterByLayer<T>(IEnumerable<T> entities, string? layerName) where T : EntityObject
        {
            if (string.IsNullOrWhiteSpace(layerName)) return entities;
            return entities.Where(e => string.Equals(e.Layer?.Name, layerName, StringComparison.OrdinalIgnoreCase));
        }

        private void ProcessAllEntities(
            DxfDocument             dxfDoc,
            List<Feature>           features,
            List<string>            warnings,
            Dictionary<string, int> featuresByLayer,
            int                     tessSegs,
            int                     srid,
            string?                 centerlineLayerName = null)
        {
            // LwPolyline ─── 2D polyline with optional bulge per vertex
            Dispatch(FilterByLayer(dxfDoc.LwPolylines, centerlineLayerName), e => MapLwPolyline(e, tessSegs), features, warnings, featuresByLayer, centerlineLayerName);
            // Polyline ────── 3D legacy polyline
            Dispatch(FilterByLayer(dxfDoc.Polylines, centerlineLayerName),   e => MapPolyline(e),             features, warnings, featuresByLayer, centerlineLayerName);
            // Line ──────────── simple two-point segment
            Dispatch(FilterByLayer(dxfDoc.Lines, centerlineLayerName),       e => MapLine(e),                 features, warnings, featuresByLayer, centerlineLayerName);
            // Arc ─────────── circular arc
            Dispatch(FilterByLayer(dxfDoc.Arcs, centerlineLayerName),        e => MapArc(e, tessSegs),        features, warnings, featuresByLayer, centerlineLayerName);
            // Circle ─────── full circle → closed polygon ring
            Dispatch(FilterByLayer(dxfDoc.Circles, centerlineLayerName),     e => MapCircle(e, tessSegs),     features, warnings, featuresByLayer, centerlineLayerName);
            Dispatch(FilterByLayer(dxfDoc.Splines, centerlineLayerName),     e => MapSpline(e, tessSegs),     features, warnings, featuresByLayer, centerlineLayerName);
            // Point ──────── single coordinate
            Dispatch(FilterByLayer(dxfDoc.Points, centerlineLayerName),      e => MapPoint(e),                features, warnings, featuresByLayer, centerlineLayerName);
            // Hatch ──────── filled region bounded by edges
            Dispatch(FilterByLayer(dxfDoc.Hatches, centerlineLayerName),     e => MapHatch(e, tessSegs),      features, warnings, featuresByLayer, centerlineLayerName);
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
            Dictionary<string, int>              featuresByLayer,
            string?                               centerlineLayerName = null)
            where TEntity : EntityObject
        {
            if (entities is null) return;

            foreach (var entity in entities)
            {
                try
                {
                    var geometry = converter(entity);
                    if (geometry is null) continue;

                    var props   = BuildProperties(entity, centerlineLayerName);
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
                .Select(v => (NtsCoordinate)new CoordinateZ(v.Position.X, v.Position.Y, v.Position.Z))
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
            var start = new CoordinateZ(line.StartPoint.X, line.StartPoint.Y, line.StartPoint.Z);
            var end   = new CoordinateZ(line.EndPoint.X,   line.EndPoint.Y,   line.EndPoint.Z);
            if (start.Equals2D(end)) return null;

            return ToGjsLineString(new List<NtsCoordinate> { start, end });
        }

        /// <summary>
        /// Maps an Arc entity via trigonometric tessellation.
        /// Arc.Center is Vector3; Arc.Radius, StartAngle, EndAngle are double.
        /// </summary>
        private static IGeometryObject? MapArc(Arc arc, int tessSegs)
        {
            var rawCoords = CurveTessellationHelper.TessellateArc(
                arc.Center.X, arc.Center.Y,
                arc.Radius,
                arc.StartAngle, arc.EndAngle,
                isCcw: true,
                segmentsPerCircle: tessSegs);

            double z = arc.Center.Z;
            var coords = rawCoords.Select(c => (NtsCoordinate)new CoordinateZ(c.X, c.Y, z)).ToList();

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
            var rawCoords = CurveTessellationHelper.TessellateCircle(
                circle.Center.X, circle.Center.Y,
                circle.Radius,
                segmentsPerCircle: tessSegs);

            double z = circle.Center.Z;
            var coords = rawCoords.Select(c => (NtsCoordinate)new CoordinateZ(c.X, c.Y, z)).ToList();

            return CurveTessellationHelper.HasMinimumPoints(coords, 4)
                ? ToGjsPolygon(coords)
                : null;
        }

        /// <summary>
        /// Maps a Point entity.
        /// Point.Position is Vector3 in netDxf 2.1.1.
        /// GeoJSON Position takes (latitude=Y, longitude=X).
        /// </summary>
        private static IGeometryObject? MapSpline(Spline spline, int tessSegs)
        {
            List<netDxf.Vector3>? vertexes = null;

            if (spline.ControlPoints is not null && spline.ControlPoints.Count > 0)
            {
                try
                {
                    int precision = Math.Clamp(tessSegs / 2, 16, 128);
                    vertexes = spline.PolygonalVertexes(precision);
                }
                catch
                {
                    // Fall back to fit points or control points
                }
            }

            if (vertexes is null || vertexes.Count < 2)
            {
                if (spline.FitPoints is not null && spline.FitPoints.Count >= 2)
                {
                    vertexes = spline.FitPoints;
                }
                else if (spline.ControlPoints is not null && spline.ControlPoints.Count >= 2)
                {
                    vertexes = spline.ControlPoints.Select(cp => cp.Position).ToList();
                }
            }

            if (vertexes is null || vertexes.Count < 2) return null;

            var coords = vertexes
                .Select(v => (NtsCoordinate)new CoordinateZ(v.X, v.Y, v.Z))
                .ToList();

            if (spline.IsClosed)
            {
                CloseRing(coords);
                return CurveTessellationHelper.HasMinimumPoints(coords, 4)
                    ? ToGjsPolygon(coords)
                    : null;
            }

            return CurveTessellationHelper.HasMinimumPoints(coords)
                ? ToGjsLineString(coords)
                : null;
        }

        private static IGeometryObject MapPoint(netDxf.Entities.Point pt)
            => new GjsPoint(new GjsPosition(pt.Position.Y, pt.Position.X, pt.Position.Z));

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
                .Select(c => (GjsIPos)new GjsPosition(c.Y, c.X, double.IsNaN(c.Z) ? 0.0 : c.Z))
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
                .Select(c => (GjsIPos)new GjsPosition(c.Y, c.X, double.IsNaN(c.Z) ? 0.0 : c.Z))
                .ToList();
            // GeoJSON.Net Polygon takes a list of LineString rings
            var ring = new GjsLineStr(positions);
            return new GjsPolygon(new List<GjsLineStr> { ring });
        }

        /// <summary>Ensures the coordinate list forms a closed ring (first == last).</summary>
        private static void CloseRing(List<NtsCoordinate> coords)
        {
            if (coords.Count > 0 && !coords[0].Equals2D(coords[^1]))
                coords.Add(new CoordinateZ(coords[0].X, coords[0].Y, coords[0].Z));
        }

        // ================================================================== //
        //  Private — Properties builder                                       //
        // ================================================================== //

        /// <summary>
        /// Builds the GeoJSON Feature Properties dictionary from entity and layer metadata.
        /// No layer names are hardcoded; all metadata is derived dynamically.
        /// </summary>
        private static Dictionary<string, object?> BuildProperties(EntityObject entity, string? centerlineLayerName = null)
        {
            var layer = entity.Layer ?? Layer.Default;

            // Determine the effective ACI color (entity override vs layer color)
            // AciColor.IsByLayer is true when the entity defers to its layer's color.
            var aciColor = (entity.Color is not null && !entity.Color.IsByLayer)
                ? entity.Color
                : layer.Color;

            string colorHex = aciColor is not null ? AciToHex(aciColor.Index) : "#FFFFFF";
            double elevation = GetEntityElevation(entity);

            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                // UI layer classification (dynamically maps selected layer to Centerline)
                ["layerType"]        = DetermineLayerType(entity, centerlineLayerName),

                // Z-Axis elevation metadata (Step 1)
                ["averageElevation"] = Math.Round(elevation, 3),
                ["elevation"]        = Math.Round(elevation, 3),
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

                /// <summary>
        /// Resilient DXF loader. Many real-world DXF files contain TextStyle font names without
        /// .ttf/.shx extension (e.g. 'txt', 'vntime'), causing netDxf 2.1.1 to throw an ArgumentException
        /// and return null. This helper sanitizes font definitions in memory before giving up.
        /// </summary>
        private DxfDocument? LoadDxfDocument(Stream stream, string fileName)
        {
            stream.Position = 0;
            DxfDocument? doc = null;
            try
            {
                doc = DxfDocument.Load(stream);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Initial DxfDocument.Load threw exception for '{FileName}'. Attempting sanitization.", fileName);
            }

            if (doc is not null) return doc;

            // Fallback: sanitize TextStyle entries in ASCII DXF
            try
            {
                stream.Position = 0;
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
                var sb = new System.Text.StringBuilder();
                string? prevLine = null;
                bool inStyleTable = false;

                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    string trimmed = line.Trim();
                    if (trimmed == "STYLE") inStyleTable = true;
                    else if (trimmed == "ENDTAB") inStyleTable = false;

                    // Group code 3 in STYLE table specifies the font file name
                    if (inStyleTable && prevLine?.Trim() == "3" && !string.IsNullOrWhiteSpace(trimmed))
                    {
                        if (!trimmed.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) &&
                            !trimmed.EndsWith(".shx", StringComparison.OrdinalIgnoreCase))
                        {
                            line = trimmed + ".ttf";
                        }
                    }

                    sb.AppendLine(line);
                    prevLine = line;
                }

                var sanitizedBytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
                using var sanitizedStream = new MemoryStream(sanitizedBytes);
                doc = DxfDocument.Load(sanitizedStream);
                if (doc is not null)
                {
                    _logger.LogInformation("Successfully loaded '{FileName}' after font sanitization.", fileName);
                }
                return doc;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Sanitization fallback failed for '{FileName}'.", fileName);
                return null;
            }
        }

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

        private static double GetEntityElevation(EntityObject entity) => entity switch
        {
            Line l => (l.StartPoint.Z + l.EndPoint.Z) / 2.0,
            LwPolyline lw => lw.Elevation,
            Polyline p when p.Vertexes.Count > 0 => p.Vertexes.Average(v => v.Position.Z),
            netDxf.Entities.Point pt => pt.Position.Z,
            Arc a => a.Center.Z,
            Circle c => c.Center.Z,
            Spline s when s.ControlPoints.Count > 0 => s.ControlPoints.Average(cp => cp.Position.Z),
            Hatch h => h.Elevation,
            _ => 0.0
        };

        private static List<Feature> GenerateStationPoints(
            List<Feature> features,
            Dictionary<string, int> featuresByLayer,
            double segmentLength,
            int srid,
            string? centerlineLayerName = null)
        {
            var stationFeatures = new List<Feature>();
            var ntsFactory = new NtsGeometryFactory(new NtsPrecisionModel(), srid);
            int stationCounter = 0;

            foreach (var feat in features)
            {
                if (!feat.Properties.TryGetValue("layerType", out var lt) ||
                    lt is not string typeStr ||
                    !string.Equals(typeStr, "Centerline", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (feat.Geometry is not GjsLineStr lineStr || lineStr.Coordinates.Count < 2)
                    continue;

                var ntsCoords = lineStr.Coordinates
                    .Select(c => (NtsCoordinate)new CoordinateZ(c.Longitude, c.Latitude, c.Altitude ?? 0.0))
                    .ToArray();
                var ntsLine = ntsFactory.CreateLineString(ntsCoords);
                if (ntsLine.Length <= 1e-9) continue;

                var indexedLine = new LengthIndexedLine(ntsLine);
                double lineLengthNative = ntsLine.Length;
                double lineLengthMeters = (srid == 4326) ? (lineLengthNative * MetersPerDegree) : lineLengthNative;

                for (double distMeters = 0.0; distMeters <= lineLengthMeters + 1e-4; distMeters += segmentLength)
                {
                    double distNative = (srid == 4326) ? (distMeters / MetersPerDegree) : distMeters;
                    distNative = Math.Clamp(distNative, indexedLine.StartIndex, indexedLine.EndIndex);

                    var ptCoord = indexedLine.ExtractPoint(distNative);

                    // Interpolate exact 3D Z elevation at distNative along the centerline
                    double z = 0.0;
                    double accumDist = 0.0;
                    var coordsList = lineStr.Coordinates;
                    for (int i = 1; i < coordsList.Count; i++)
                    {
                        double segDx = coordsList[i].Longitude - coordsList[i-1].Longitude;
                        double segDy = coordsList[i].Latitude  - coordsList[i-1].Latitude;
                        double segLen = Math.Sqrt(segDx * segDx + segDy * segDy);

                        if (accumDist + segLen >= distNative - 1e-9 || i == coordsList.Count - 1)
                        {
                            double t = segLen > 1e-9 ? Math.Clamp((distNative - accumDist) / segLen, 0.0, 1.0) : 0.0;
                            double z1 = coordsList[i-1].Altitude ?? 0.0;
                            double z2 = coordsList[i].Altitude ?? z1;
                            z = z1 + t * (z2 - z1);
                            break;
                        }
                        accumDist += segLen;
                    }

                    var gjsPoint = new GjsPoint(new GjsPosition(ptCoord.Y, ptCoord.X, z));

                    string stationName = $"STA-{stationCounter:D2}";
                    var stationProps = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["layerName"]        = "STATION_POINTS",
                        ["layerType"]        = "StationPoint",
                        ["stationName"]      = stationName,
                        ["distance"]         = Math.Round(distMeters, 2),
                        ["averageElevation"] = Math.Round(z, 3),
                        ["elevation"]        = Math.Round(z, 3),
                        ["layerColor"]       = "#EF4444",
                        ["layerLineweight"]  = "default",
                        ["layerLinetype"]    = "CONTINUOUS",
                        ["layerIsVisible"]   = true,
                        ["layerIsFrozen"]    = false,
                        ["entityType"]       = "Point",
                        ["entityHandle"]     = $"STATION_{stationCounter}"
                    };

                    stationFeatures.Add(new Feature(gjsPoint, stationProps));
                    stationCounter++;
                }
            }

            if (stationFeatures.Count > 0)
            {
                features.AddRange(stationFeatures);
                featuresByLayer["STATION_POINTS"] = featuresByLayer.TryGetValue("STATION_POINTS", out var cur)
                    ? cur + stationFeatures.Count
                    : stationFeatures.Count;
            }

            return stationFeatures;
        }

        private static string DetermineLayerType(EntityObject entity, string? centerlineLayerName = null)
        {
            var rawLayerName = entity.Layer?.Name ?? string.Empty;

            // If caller explicitly selected a centerline layer, map that layer to Centerline
            if (!string.IsNullOrWhiteSpace(centerlineLayerName) &&
                string.Equals(rawLayerName, centerlineLayerName, StringComparison.OrdinalIgnoreCase))
            {
                return "Centerline";
            }

            var layerName = rawLayerName.ToUpperInvariant();
            if (layerName.Contains("EDGE") || layerName.Contains("BIEN") || layerName.Contains("LE"))
                return "Edges";
            if (layerName.Contains("BOUND") || layerName.Contains("RANH") || layerName.Contains("GIOI"))
                return "Boundaries";
            if (layerName.Contains("SURFACE") || layerName.Contains("MAT"))
                return "RoadSurface";
            if (layerName.Contains("TIM") || layerName.Contains("CENTER"))
                return "Centerline";

            return "Centerline";
        }

        private void GenerateRoadSurfaceBuffers(
            List<Feature> features,
            Dictionary<string, int> featuresByLayer,
            double roadWidth,
            int srid,
            string? centerlineLayerName = null)
        {
            if (roadWidth <= 0) return;

            double bufferDistance = (srid == 4326) ? (roadWidth / 2.0) / MetersPerDegree : (roadWidth / 2.0);
            var bufferFeatures = new List<Feature>();
            var ntsFactory = new NtsGeometryFactory(new NtsPrecisionModel(), srid);

            // Step 1: Identify continuous LineString or merged geometries representing the Centerline
            var centerlineGeoms = new List<NtsGeometry.Geometry>();
            foreach (var feat in features)
            {
                if (feat.Properties.TryGetValue("isGeneratedBuffer", out var isGen) && isGen is true)
                    continue;

                if (feat.Properties.TryGetValue("layerType", out var lt) &&
                    lt is string typeStr &&
                    string.Equals(typeStr, "Centerline", StringComparison.OrdinalIgnoreCase) &&
                    feat.Geometry is GjsLineStr lineStr &&
                    lineStr.Coordinates.Count >= 2)
                {
                    var ntsCoords = lineStr.Coordinates
                        .Select(c => new NtsCoordinate(c.Longitude, c.Latitude))
                        .ToArray();
                    centerlineGeoms.Add(ntsFactory.CreateLineString(ntsCoords));
                }
            }

            if (centerlineGeoms.Count == 0)
            {
                _logger.LogDebug("No centerline geometries found to generate road surface buffer.");
                return;
            }

            try
            {
                // Step 2: Merge geometries representing the continuous Centerline
                NtsGeometry.Geometry centerlineGeometry = centerlineGeoms.Count == 1
                    ? centerlineGeoms[0]
                    : ntsFactory.CreateMultiLineString(centerlineGeoms.Cast<NtsGeometry.LineString>().ToArray());

                // Step 3: Apply the NTS Buffer operation (exact buffered shape following the curve of the centerline)
                var roadPolygon = centerlineGeometry.Buffer(bufferDistance);

                if (roadPolygon is not null && !roadPolygon.IsEmpty)
                {
                    void AddPolygonFeature(NtsPolygon ntsPoly)
                    {
                        if (ntsPoly.IsEmpty) return;

                        var shellCoords = ntsPoly.ExteriorRing.Coordinates
                            .Select(c => (GjsIPos)new GjsPosition(c.Y, c.X, double.IsNaN(c.Z) ? 0.0 : c.Z))
                            .ToList();

                        var rings = new List<GjsLineStr> { new GjsLineStr(shellCoords) };

                        for (int i = 0; i < ntsPoly.NumInteriorRings; i++)
                        {
                            var hole = ntsPoly.GetInteriorRingN(i);
                            var holeCoords = hole.Coordinates
                                .Select(c => (GjsIPos)new GjsPosition(c.Y, c.X, double.IsNaN(c.Z) ? 0.0 : c.Z))
                                .ToList();
                            rings.Add(new GjsLineStr(holeCoords));
                        }

                        var gjsPoly = new GjsPolygon(rings);

                        var bufferProps = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["layerType"]         = "RoadSurface",
                            ["layerName"]         = "ROAD_SURFACE_2D",
                            ["isGeneratedBuffer"] = true,
                            ["roadWidth"]         = roadWidth,
                            ["material"]          = "asphalt",
                            ["layerColor"]        = "#334155",
                            ["layerLineweight"]   = "default",
                            ["layerLinetype"]     = "CONTINUOUS",
                            ["layerIsVisible"]    = true,
                            ["layerIsFrozen"]     = false,
                            ["entityType"]        = "Polygon",
                            ["entityHandle"]      = "GEN_ROAD_SURFACE_2D"
                        };

                        bufferFeatures.Add(new Feature(gjsPoly, bufferProps));
                    }

                    if (roadPolygon is NtsPolygon singlePoly)
                    {
                        AddPolygonFeature(singlePoly);
                    }
                    else if (roadPolygon is NtsMultiPolygon multiPoly)
                    {
                        for (int i = 0; i < multiPoly.NumGeometries; i++)
                        {
                            if (multiPoly.GetGeometryN(i) is NtsPolygon partPoly)
                            {
                                AddPolygonFeature(partPoly);
                            }
                        }
                    }

                    if (bufferFeatures.Count > 0)
                    {
                        features.AddRange(bufferFeatures);
                        featuresByLayer["ROAD_SURFACE_2D"] = featuresByLayer.TryGetValue("ROAD_SURFACE_2D", out var cur)
                            ? cur + bufferFeatures.Count
                            : bufferFeatures.Count;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate road surface buffer from centerline geometry.");
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
                            geom = ntsFactory.CreatePoint(new CoordinateZ(pt.Coordinates.Longitude, pt.Coordinates.Latitude, pt.Coordinates.Altitude ?? 0.0));
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
