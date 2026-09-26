using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using netDxf;
using netDxf.Entities;
using netDxf.Tables;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using RoadGuard.CadParser.DTOs;
using RoadGuard.CadParser.Services.Interfaces;

namespace RoadGuard.CadParser.Controllers
{
    /// <summary>
    /// Form-data request model for the Get Layers endpoint.
    /// Supports both 'dxfFile' and 'file' field names.
    /// </summary>
    public sealed class CadGetLayersRequest
    {
        /// <summary>
        /// Uploaded DXF file field named 'dxfFile'.
        /// </summary>
        public IFormFile? DxfFile { get; set; }

        /// <summary>
        /// Uploaded DXF file field named 'file'.
        /// </summary>
        public IFormFile? File { get; set; }
    }

    /// <summary>
    /// Form-data request model for the DXF parsing endpoint.
    /// Encapsulates the uploaded file and parsing options for Swashbuckle compatibility.
    /// </summary>
    public sealed class CadParseRequest
    {
        /// <summary>
        /// Multipart form-data file field named 'file'. Accepts .dxf files (max 100 MB).
        /// </summary>
        public IFormFile File { get; set; } = null!;

        /// <summary>
        /// Target Spatial Reference ID for all output geometries.
        /// Supported: 4326 (WGS-84, default), 32648 (UTM 48N), 32649 (UTM 49N).
        /// </summary>
        public int TargetSrid { get; set; } = 4326;

        /// <summary>
        /// Number of line segments per full circle when approximating curves.
        /// Range: 8 - 360. Default: 72.
        /// </summary>
        public int TessellationSegments { get; set; } = 72;

        public double SegmentLength { get; set; } = 100.0;

        public double RoadWidth { get; set; } = 3.5;

        public double SlabLength { get; set; } = 4.0;

        /// <summary>
        /// Name of the CAD layer representing the road centerline (passed from the frontend).
        /// When provided, geometry extraction is dynamically filtered to this layer.
        /// </summary>
        public string? CenterlineLayerName { get; set; }
    }

    /// <summary>
    /// HTTP API surface for the RoadGuard CAD processing module.
    ///
    /// Routes:
    ///   POST /api/cad/get-layers  - Upload a DXF file, receive a list of layer names.
    ///   POST /api/cad/parse-dxf   - Upload a DXF file, receive GeoJSON.
    ///   GET  /api/cad/health      - Liveness probe.
    /// </summary>
    [ApiController]
    [Route("api/cad")]
    [Produces("application/json")]
    public sealed class CadProcessingController : ControllerBase
    {
        private readonly ICadParserService         _parserService;
        private readonly ILogger<CadProcessingController> _logger;

        // Shared Newtonsoft serialiser settings (camelCase, nulls omitted)
        private static readonly JsonSerializerSettings _jsonSettings = new()
        {
            ContractResolver     = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling    = NullValueHandling.Ignore,
            Formatting           = Formatting.None,
        };

        public CadProcessingController(
            ICadParserService parserService,
            ILogger<CadProcessingController> logger)
        {
            _parserService = parserService ?? throw new ArgumentNullException(nameof(parserService));
            _logger        = logger        ?? throw new ArgumentNullException(nameof(logger));
        }

        // ================================================================== //
        //  POST /api/cad/get-layers                                           //
        // ================================================================== //

        /// <summary>
        /// Scans an uploaded DXF file and extracts all distinct layer names.
        /// </summary>
        /// <param name="request">Multipart form-data containing the DXF file in 'dxfFile' or 'file'.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <response code="200">Returns a list of layer names in the uploaded DXF.</response>
        /// <response code="400">File is missing or empty.</response>
        /// <response code="415">Unsupported file type.</response>
        /// <response code="422">DXF file is corrupted or unparseable.</response>
        /// <response code="500">Internal server error.</response>
        [HttpPost("get-layers")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(104_857_600)]   // 100 MB
        [RequestFormLimits(MultipartBodyLengthLimit = 104_857_600)]
        [ProducesResponseType(typeof(List<string>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(CadParserErrorResponse), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(CadParserErrorResponse), StatusCodes.Status415UnsupportedMediaType)]
        [ProducesResponseType(typeof(CadParserErrorResponse), StatusCodes.Status422UnprocessableEntity)]
        [ProducesResponseType(typeof(CadParserErrorResponse), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetLayers(
            [FromForm] CadGetLayersRequest request,
            CancellationToken cancellationToken = default)
        {
            var file = request?.DxfFile ?? request?.File ?? Request.Form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return BadRequest(new CadParserErrorResponse
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Error      = "MissingFile",
                    Detail     = "Request must include a non-empty file in the 'dxfFile' or 'file' form-data field."
                });
            }

            try
            {
                var layers = await _parserService.GetLayersAsync(file, cancellationToken).ConfigureAwait(false);
                return Ok(layers);
            }
            catch (ArgumentException ex) when (ex.Message.Contains("empty") || ex.Message.Contains("exceeds"))
            {
                _logger.LogWarning(ex, "Bad file argument in GetLayers.");
                return BadRequest(new CadParserErrorResponse
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Error      = "InvalidFile",
                    Detail     = ex.Message
                });
            }
            catch (NotSupportedException ex)
            {
                _logger.LogWarning(ex, "Unsupported file extension in GetLayers.");
                return StatusCode(StatusCodes.Status415UnsupportedMediaType,
                    new CadParserErrorResponse
                    {
                        StatusCode = (int)HttpStatusCode.UnsupportedMediaType,
                        Error      = "UnsupportedFileType",
                        Detail     = ex.Message
                    });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "DXF scan failed in GetLayers (unprocessable content).");
                return UnprocessableEntity(new CadParserErrorResponse
                {
                    StatusCode = (int)HttpStatusCode.UnprocessableEntity,
                    Error      = "ParseFailed",
                    Detail     = ex.Message
                });
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("GetLayers request was cancelled.");
                return StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during GetLayers scan.");
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new CadParserErrorResponse
                    {
                        StatusCode = (int)HttpStatusCode.InternalServerError,
                        Error      = "InternalServerError",
                        Detail     = "An unexpected error occurred. Please contact the system administrator."
                    });
            }
        }

        // ================================================================== //
        //  POST /api/cad/parse-dxf                                            //
        // ================================================================== //

        /// <summary>
        /// Parses an uploaded AutoCAD DXF file and returns a GeoJSON FeatureCollection.
        ///
        /// The file is processed entirely in memory; no temporary files are written.
        /// Each GeoJSON Feature carries the originating layer name, color, and lineweight
        /// in its Properties dictionary.
        /// </summary>
        /// <param name="request">
        ///   Multipart form-data model containing the DXF file, target SRID, tessellation options, and optional centerlineLayerName.
        /// </param>
        /// <param name="cancellationToken">Propagated from the HTTP pipeline.</param>
        /// <response code="200">Parsing succeeded; body contains <see cref="GeoJsonResponse"/>.</response>
        /// <response code="400">File is missing, empty, too large, or has a wrong extension.</response>
        /// <response code="415">File format is not a supported DXF variant.</response>
        /// <response code="422">File is a valid DXF container but the content is corrupt or unparseable.</response>
        /// <response code="500">Unexpected server-side failure.</response>
        [HttpPost("parse-dxf")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(104_857_600)]   // 100 MB
        [RequestFormLimits(MultipartBodyLengthLimit = 104_857_600)]
        [ProducesResponseType(typeof(GeoJsonResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(CadParserErrorResponse), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(CadParserErrorResponse), StatusCodes.Status415UnsupportedMediaType)]
        [ProducesResponseType(typeof(CadParserErrorResponse), StatusCodes.Status422UnprocessableEntity)]
        [ProducesResponseType(typeof(CadParserErrorResponse), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> ParseDxf(
            [FromForm] CadParseRequest request,
            CancellationToken cancellationToken = default)
        {
            // Guard: request & file presence
            if (request is null || request.File is null || request.File.Length == 0)
            {
                return BadRequest(new CadParserErrorResponse
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Error      = "MissingFile",
                    Detail     = "Request must include a non-empty file in the 'file' form-data field."
                });
            }

            var file = request.File;
            var srid = request.TargetSrid;
            var tessellationSegments = request.TessellationSegments;

            // Guard: SRID
            if (srid is not (4326 or 32648 or 32649))
            {
                return BadRequest(new CadParserErrorResponse
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Error      = "InvalidSrid",
                    Detail     = $"SRID '{srid}' is not supported. Use 4326, 32648, or 32649."
                });
            }

            // Guard: tessellation range
            if (tessellationSegments is < 8 or > 360)
            {
                return BadRequest(new CadParserErrorResponse
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Error      = "InvalidTessellation",
                    Detail     = $"tessellationSegments must be between 8 and 360. Got: {tessellationSegments}."
                });
            }

            _logger.LogInformation(
                "ParseDxf request received: file={Name}, size={Size}B, srid={Srid}, centerlineLayer={CenterlineLayer}",
                file.FileName, file.Length, srid, request.CenterlineLayerName ?? "(auto-detect)");

            try
            {
                var result = await _parserService.ParseDxfAsync(
                    file,
                    srid,
                    tessellationSegments,
                    request.SegmentLength,
                    request.RoadWidth,
                    request.SlabLength,
                    request.CenterlineLayerName,
                    cancellationToken).ConfigureAwait(false);

                return Ok(result);
            }
            catch (ArgumentException ex) when (
                ex.Message.Contains("empty") || ex.Message.Contains("exceeds"))
            {
                _logger.LogWarning(ex, "Bad file argument.");
                return BadRequest(new CadParserErrorResponse
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Error      = "InvalidFile",
                    Detail     = ex.Message
                });
            }
            catch (NotSupportedException ex)
            {
                _logger.LogWarning(ex, "Unsupported file type.");
                return StatusCode(StatusCodes.Status415UnsupportedMediaType,
                    new CadParserErrorResponse
                    {
                        StatusCode = (int)HttpStatusCode.UnsupportedMediaType,
                        Error      = "UnsupportedFileType",
                        Detail     = ex.Message
                    });
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "DXF parse failed (unprocessable content).");
                return UnprocessableEntity(new CadParserErrorResponse
                {
                    StatusCode = (int)HttpStatusCode.UnprocessableEntity,
                    Error      = "ParseFailed",
                    Detail     = ex.Message
                });
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("ParseDxf request was cancelled.");
                return StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during DXF parsing.");
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new CadParserErrorResponse
                    {
                        StatusCode = (int)HttpStatusCode.InternalServerError,
                        Error      = "InternalServerError",
                        Detail     = "An unexpected error occurred. Please contact the system administrator."
                    });
            }
        }


        // ================================================================== //
        //  POST /api/cad/parse-road                                           //
        // ================================================================== //

        /// <summary>
        /// Extracts GPS coordinates for the main road centerline and creates a 2D road corridor polygon
        /// with width roadWidth, filtered from clutter and formatted for GIS / Leaflet mapping.
        /// </summary>
        /// <response code="200">Returns GPS coordinates for centerline, road surface polygon, and bounds.</response>
        /// <response code="400">If the uploaded file is missing or invalid.</response>
        [HttpPost("parse-road")]
        [Consumes("multipart/form-data")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(CadParseRoadResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(CadParserErrorResponse), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> ParseRoad(
            [FromForm] CadParseRoadRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request is null || request.File is null || request.File.Length == 0)
            {
                return BadRequest(new CadParserErrorResponse
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Error      = "MissingFile",
                    Detail     = "Request must include a non-empty file in the 'file' form-data field."
                });
            }

            try
            {
                var result = await _parserService.ParseRoadAsync(
                    request.File,
                    request.RoadWidth > 0 ? request.RoadWidth : 7.0,
                    request.CenterlineLayerName,
                    request.CentralMeridian,
                    cancellationToken).ConfigureAwait(false);

                if (!result.Success)
                {
                    return StatusCode((int)HttpStatusCode.UnprocessableEntity, new CadParserErrorResponse
                    {
                        StatusCode = (int)HttpStatusCode.UnprocessableEntity,
                        Error      = result.Error ?? "ParseRoadFailed",
                        Detail     = result.Detail ?? "Unable to extract road geometry from CAD drawing."
                    });
                }

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception during ParseRoad.");
                return StatusCode((int)HttpStatusCode.InternalServerError, new CadParserErrorResponse
                {
                    StatusCode = (int)HttpStatusCode.InternalServerError,
                    Error      = "InternalServerError",
                    Detail     = ex.Message
                });
            }
        }

        // ================================================================== //
        //  POST /api/cad/render-overlay                                       //
        // ================================================================== //

        /// <summary>
        /// Renders an uploaded CAD DXF drawing into a transparent 2D PNG raster overlay
        /// styled like a realistic concrete road (grey road ribbon, yellow centerline, white edges),
        /// returning WGS-84 Leaflet bounds and base64 PNG data.
        /// </summary>
        /// <response code="200">Returns WGS-84 Leaflet bounds and base64 PNG data.</response>
        /// <response code="400">If the uploaded file is missing or invalid.</response>
        [HttpPost("render-overlay")]
        [Consumes("multipart/form-data")]
        [Produces("application/json")]
        [ProducesResponseType(typeof(CadRenderOverlayResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(CadParserErrorResponse), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> RenderOverlay(
            [FromForm] CadRenderOverlayRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request is null || request.File is null || request.File.Length == 0)
            {
                return BadRequest(new CadParserErrorResponse
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Error      = "MissingFile",
                    Detail     = "Request must include a non-empty file in the 'file' form-data field."
                });
            }

            _logger.LogInformation(
                "RenderOverlay request received: file={Name}, size={Size}B, layer={Layer}, width={Width}m",
                request.File.FileName, request.File.Length, request.CenterlineLayerName ?? "(auto-detect)", request.RoadWidth);

            try
            {
                var result = await _parserService.RenderOverlayAsync(
                    request.File,
                    request.CenterlineLayerName,
                    request.RoadWidth,
                    request.OutputSizePx,
                    cancellationToken).ConfigureAwait(false);

                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during DXF render overlay.");
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new CadParserErrorResponse
                    {
                        StatusCode = (int)HttpStatusCode.InternalServerError,
                        Error      = "InternalServerError",
                        Detail     = ex.Message
                    });
            }
        }


        // ================================================================== //
        //  GET /api/cad/download-mock-dxf                                     //
        // ================================================================== //

        /// <summary>
        /// Generates and downloads a guaranteed-valid DXF mock file containing a
        /// rectangle around coordinates (106.700, 10.800) representing a location in Vietnam.
        /// </summary>
        /// <response code="200">Returns the generated DXF file as an attachment.</response>
        [HttpGet("download-mock-dxf")]
        [Produces("application/octet-stream")]
        [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
        public IActionResult DownloadMockDxf()
        {
            var doc = new DxfDocument();

            // Simple rectangle around GPS location in Vietnam (106.700, 10.800)
            var vertices = new[]
            {
                new LwPolylineVertex(106.700, 10.800),
                new LwPolylineVertex(106.710, 10.800),
                new LwPolylineVertex(106.710, 10.810),
                new LwPolylineVertex(106.700, 10.810)
            };
            var rect = new LwPolyline(vertices, isClosed: true)
            {
                Layer = new Layer("ROAD_MOCK_LAYER")
            };
            doc.AddEntity(rect);

            rect.Elevation = 10.5;

            var centerline = new Line(
                new netDxf.Vector3(106.700, 10.805, 12.0),
                new netDxf.Vector3(106.710, 10.805, 15.0))
            {
                Layer = new Layer("ROAD_CENTERLINE")
            };
            doc.AddEntity(centerline);

            // Arc with 3D elevation
            var arc = new Arc(new netDxf.Vector3(106.705, 10.808, 14.0), 0.002, 0.0, 180.0)
            {
                Layer = new Layer("ROAD_CURVE")
            };
            doc.AddEntity(arc);

            // Spline with 3D control points
            var splineControlPoints = new System.Collections.Generic.List<SplineVertex>
            {
                new SplineVertex(new netDxf.Vector3(106.700, 10.802, 11.0)),
                new SplineVertex(new netDxf.Vector3(106.703, 10.803, 11.8)),
                new SplineVertex(new netDxf.Vector3(106.707, 10.802, 12.4)),
                new SplineVertex(new netDxf.Vector3(106.710, 10.803, 13.0))
            };
            var spline = new Spline(splineControlPoints)
            {
                Layer = new Layer("ROAD_SPLINE")
            };
            doc.AddEntity(spline);

            var stream = new MemoryStream();
            doc.Save(stream);
            stream.Position = 0;

            return File(stream, "application/octet-stream", "mock_valid.dxf");
        }

        // ================================================================== //
        //  GET /api/cad/health                                                //
        // ================================================================== //

        /// <summary>
        /// Lightweight liveness probe for load-balancer and k8s health checks.
        /// </summary>
        [HttpGet("health")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult Health()
            => Ok(new { status = "healthy", service = "RoadGuard.CadParser", utc = DateTime.UtcNow });
    }
}
