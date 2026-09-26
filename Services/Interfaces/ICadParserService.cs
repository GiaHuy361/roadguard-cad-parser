using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using RoadGuard.CadParser.DTOs;

namespace RoadGuard.CadParser.Services.Interfaces
{
    /// <summary>
    /// Defines the contract for parsing AutoCAD DXF files into GIS representations.
    ///
    /// Design principles (ISP / DIP):
    ///   - Callers depend only on this abstraction, never on the concrete implementation.
    ///   - All I/O is async to support high-throughput Web API scenarios.
    ///   - The service is stateless; each call is fully isolated.
    /// </summary>
    public interface ICadParserService
    {
        /// <summary>
        /// Quickly scans an uploaded DXF file and extracts all distinct layer names.
        /// </summary>
        /// <param name="file">The uploaded DXF file.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A list of unique layer names found in the DXF file.</returns>
        Task<List<string>> GetLayersAsync(
            IFormFile file,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Parses an AutoCAD DXF file received as an <see cref="IFormFile"/> and
        /// returns a <see cref="GeoJsonResponse"/> containing a GeoJSON FeatureCollection
        /// with every graphic entity mapped to a Feature, layer metadata embedded in
        /// each Feature's Properties, and all NTS Geometry objects stamped with the
        /// requested SRID.
        /// </summary>
        /// <param name="file">
        ///   The uploaded DXF file. Must not be null or empty.
        ///   Only <c>.dxf</c> extension is supported (DWG requires a separate converter).
        /// </param>
        /// <param name="srid">
        ///   Spatial Reference ID to assign to all parsed geometries.
        ///   Supported values: 4326 (WGS-84), 32648 (UTM 48N), 32649 (UTM 49N).
        ///   Defaults to 4326 when not specified.
        /// </param>
        /// <param name="tessellationSegments">
        ///   Number of line segments used to approximate a full circle when tessellating
        ///   curves (bulge arcs, Arc entities, Circle entities).
        ///   Defaults to 72 (5 deg per segment). Minimum value is 8.
        /// </param>
        /// <param name="segmentLength">
        ///   Segment length in metres for road segmentation.
        ///   Defaults to 100.0 m.
        /// </param>
        /// <param name="roadWidth">
        ///   Road width in metres for surface area calculation and polygon buffering.
        ///   Defaults to 3.5 m.
        /// </param>
        /// <param name="slabLength">
        ///   Length in metres per concrete slab (TCVN 10380:2014).
        ///   Defaults to 4.0 m.
        /// </param>
        /// <param name="centerlineLayerName">
        ///   Optional name of the CAD layer representing the road centerline.
        ///   When specified, geometry extraction is dynamically filtered to this layer.
        /// </param>
        /// <param name="cancellationToken">Propagates cancellation from the HTTP pipeline.</param>
        /// <returns>
        ///   A populated <see cref="GeoJsonResponse"/> on success.
        ///   Non-fatal issues are reported via <see cref="GeoJsonResponse.Warnings"/>.
        /// </returns>
        Task<GeoJsonResponse> ParseDxfAsync(
            IFormFile file,
            int srid = 4326,
            int tessellationSegments = 72,
            double segmentLength = 100.0,
            double roadWidth = 3.5,
            double slabLength = 4.0,
            string? centerlineLayerName = null,
            CancellationToken cancellationToken = default);
    }
}
