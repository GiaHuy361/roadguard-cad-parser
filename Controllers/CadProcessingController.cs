using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
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
    }

    /// <summary>
    /// HTTP API surface for the RoadGuard CAD processing module.
    ///
    /// Routes:
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
        ///   Multipart form-data model containing the DXF file, target SRID, and tessellation options.
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
                "ParseDxf request received: file={Name}, size={Size}B, srid={Srid}",
                file.FileName, file.Length, srid);

            try
            {
                var result = await _parserService.ParseDxfAsync(
                    file, srid, tessellationSegments, cancellationToken).ConfigureAwait(false);

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
