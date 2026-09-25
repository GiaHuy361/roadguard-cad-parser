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
        /// Parses an AutoCAD DXF file received as an <see cref="IFormFile"/> and
        /// returns a <see cref="GeoJsonResponse"/> containing a GeoJSON FeatureCollection
        /// with every graphic entity mapped to a Feature, layer metadata embedded in
        /// each Feature''s Properties, and all NTS Geometry objects stamped with the
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
        ///   Defaults to 72 (5° per segment). Minimum value is 8.
        /// </param>
        /// <param name="cancellationToken">Propagates cancellation from the HTTP pipeline.</param>
        /// <returns>
        ///   A populated <see cref="GeoJsonResponse"/> on success.
        ///   Non-fatal issues are reported via <see cref="GeoJsonResponse.Warnings"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="file"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        ///   Thrown for fatal parsing failures (corrupted file, unsupported DXF version, etc.).
        /// </exception>
        Task<GeoJsonResponse> ParseDxfAsync(
            IFormFile file,
            int srid = 4326,
            int tessellationSegments = 72,
            CancellationToken cancellationToken = default);
    }
}
