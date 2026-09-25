using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;

namespace RoadGuard.CadParser.Helpers
{
    /// <summary>
    /// Pure-math helpers for tessellating AutoCAD curved entities into
    /// sequences of <see cref="Coordinate"/> points suitable for NTS geometries.
    ///
    /// Supported curve types:
    ///   - LwPolyline bulge segments (chord-based arc interpolation)
    ///   - Standalone Arc entities (center / radius / start-end angle)
    ///   - Circle entities (full 360° arc)
    ///
    /// NO AI / image processing is used. All interpolation is pure trigonometry.
    /// </summary>
    public static class CurveTessellationHelper
    {
        // ------------------------------------------------------------------ //
        //  Constants                                                           //
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Default number of line segments used to approximate a full circle.
        /// Arc segments use a proportional fraction of this value.
        /// Increase for smoother curves at the cost of more vertices.
        /// </summary>
        public const int DefaultSegmentsPerCircle = 72; // 5° per segment

        /// <summary>Bulge values smaller than this are treated as straight lines.</summary>
        private const double BulgeEpsilon = 1e-9;

        // ------------------------------------------------------------------ //
        //  Public API — Bulge / LwPolyline                                    //
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Tessellates a single bulge segment between two consecutive
        /// LwPolyline vertices.
        ///
        /// <para>
        /// A <b>bulge</b> value encodes a circular arc as the tangent of one
        /// quarter of the included angle:
        ///   bulge = tan(θ/4)   where θ = included (central) angle of the arc.
        /// Sign convention: positive bulge → counter-clockwise arc.
        /// </para>
        /// </summary>
        /// <param name="startX">X coordinate of the start vertex.</param>
        /// <param name="startY">Y coordinate of the start vertex.</param>
        /// <param name="endX">X coordinate of the end vertex.</param>
        /// <param name="endY">Y coordinate of the end vertex.</param>
        /// <param name="bulge">Bulge value from the LwPolyline vertex.</param>
        /// <param name="segmentsPerCircle">
        ///   Controls tessellation density. The arc gets a proportional share.
        /// </param>
        /// <returns>
        ///   An ordered list of coordinates forming the arc, including the
        ///   start point but EXCLUDING the end point (caller appends it after
        ///   the last segment to avoid duplication).
        /// </returns>
        public static List<Coordinate> TessellateBulgeSegment(
            double startX, double startY,
            double endX,   double endY,
            double bulge,
            int segmentsPerCircle = DefaultSegmentsPerCircle)
        {
            var points = new List<Coordinate>();

            // Treat near-zero bulge as a straight line
            if (Math.Abs(bulge) < BulgeEpsilon)
            {
                points.Add(new Coordinate(startX, startY));
                return points;
            }

            // ── Arc geometry from bulge ──────────────────────────────────── //
            // Chord length
            double dx     = endX - startX;
            double dy     = endY - startY;
            double chord  = Math.Sqrt(dx * dx + dy * dy);

            // Sagitta (height of the arc) = (chord/2) * |bulge|
            // Radius from bulge: r = chord * (1 + bulge²) / (4 * |bulge|)
            double absBulge = Math.Abs(bulge);
            double radius   = chord * (1.0 + bulge * bulge) / (4.0 * absBulge);

            // Half-included angle: α = 2 * atan(|bulge|)
            double halfAngle    = 2.0 * Math.Atan(absBulge);
            double includedAngle = 4.0 * Math.Atan(absBulge); // = 2 * halfAngle

            // Midpoint of the chord
            double midX = (startX + endX) / 2.0;
            double midY = (startY + endY) / 2.0;

            // Unit vector perpendicular to the chord
            double chordAngle = Math.Atan2(dy, dx);
            double perpAngle  = chordAngle + Math.PI / 2.0;

            // Distance from chord midpoint to arc centre
            double distToCenter = Math.Sqrt(radius * radius - (chord / 2.0) * (chord / 2.0));

            // Negative bulge → CW → center on the right of the direction vector
            double centerX, centerY;
            if (bulge > 0) // CCW → centre on the left
            {
                centerX = midX + distToCenter * Math.Cos(perpAngle);
                centerY = midY + distToCenter * Math.Sin(perpAngle);
            }
            else           // CW  → centre on the right
            {
                centerX = midX - distToCenter * Math.Cos(perpAngle);
                centerY = midY - distToCenter * Math.Sin(perpAngle);
            }

            // Angle from center to start vertex
            double startAngle = Math.Atan2(startY - centerY, startX - centerX);

            // Number of interpolation segments proportional to arc fraction
            double arcFraction = includedAngle / (2.0 * Math.PI);
            int segments = Math.Max(1, (int)Math.Ceiling(segmentsPerCircle * arcFraction));

            // Angular step: positive for CCW, negative for CW
            double angleStep = (bulge > 0 ? includedAngle : -includedAngle) / segments;

            for (int i = 0; i < segments; i++)
            {
                double angle = startAngle + i * angleStep;
                points.Add(new Coordinate(
                    centerX + radius * Math.Cos(angle),
                    centerY + radius * Math.Sin(angle)));
            }

            return points;
        }

        // ------------------------------------------------------------------ //
        //  Public API — Arc entity                                             //
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Tessellates a standalone AutoCAD <c>Arc</c> entity into an ordered
        /// list of coordinates.
        /// </summary>
        /// <param name="centerX">Arc center X.</param>
        /// <param name="centerY">Arc center Y.</param>
        /// <param name="radius">Arc radius.</param>
        /// <param name="startAngleDeg">Start angle in degrees (AutoCAD convention, CCW from +X).</param>
        /// <param name="endAngleDeg">End angle in degrees.</param>
        /// <param name="isCcw">True for counter-clockwise (AutoCAD default).</param>
        /// <param name="segmentsPerCircle">Tessellation density.</param>
        /// <returns>Ordered coordinate list from start to end of arc, inclusive.</returns>
        public static List<Coordinate> TessellateArc(
            double centerX, double centerY,
            double radius,
            double startAngleDeg, double endAngleDeg,
            bool isCcw = true,
            int segmentsPerCircle = DefaultSegmentsPerCircle)
        {
            double startRad = ToRadians(startAngleDeg);
            double endRad   = ToRadians(endAngleDeg);

            // Normalise so the sweep is always positive
            double sweep;
            if (isCcw)
            {
                sweep = endRad - startRad;
                if (sweep <= 0) sweep += 2.0 * Math.PI;
            }
            else
            {
                sweep = startRad - endRad;
                if (sweep <= 0) sweep += 2.0 * Math.PI;
                sweep = -sweep; // negative for CW
            }

            double arcFraction = Math.Abs(sweep) / (2.0 * Math.PI);
            int segments = Math.Max(1, (int)Math.Ceiling(segmentsPerCircle * arcFraction));
            double step  = sweep / segments;

            var points = new List<Coordinate>(segments + 1);
            for (int i = 0; i <= segments; i++)
            {
                double angle = startRad + i * step;
                points.Add(new Coordinate(
                    centerX + radius * Math.Cos(angle),
                    centerY + radius * Math.Sin(angle)));
            }

            return points;
        }

        // ------------------------------------------------------------------ //
        //  Public API — Circle entity                                          //
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Tessellates a full AutoCAD <c>Circle</c> entity into a closed
        /// ring of coordinates (first and last points are identical).
        /// </summary>
        /// <param name="centerX">Circle center X.</param>
        /// <param name="centerY">Circle center Y.</param>
        /// <param name="radius">Circle radius.</param>
        /// <param name="segmentsPerCircle">Number of line segments approximating the full circle.</param>
        /// <returns>
        ///   Closed coordinate ring where <c>result[0] == result[last]</c>,
        ///   suitable for NTS <see cref="Polygon"/> construction.
        /// </returns>
        public static List<Coordinate> TessellateCircle(
            double centerX, double centerY,
            double radius,
            int segmentsPerCircle = DefaultSegmentsPerCircle)
        {
            var points = new List<Coordinate>(segmentsPerCircle + 1);
            double step = 2.0 * Math.PI / segmentsPerCircle;

            for (int i = 0; i <= segmentsPerCircle; i++)
            {
                double angle = i * step;
                points.Add(new Coordinate(
                    centerX + radius * Math.Cos(angle),
                    centerY + radius * Math.Sin(angle)));
            }

            // Ensure ring is closed
            points[segmentsPerCircle] = new Coordinate(points[0].X, points[0].Y);
            return points;
        }

        // ------------------------------------------------------------------ //
        //  Utility                                                             //
        // ------------------------------------------------------------------ //

        /// <summary>Converts degrees to radians.</summary>
        public static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

        /// <summary>
        /// Ensures a coordinate list has at least <paramref name="minimum"/> points.
        /// Returns false if the list is too short for a valid geometry.
        /// </summary>
        public static bool HasMinimumPoints(IList<Coordinate> coords, int minimum = 2)
            => coords != null && coords.Count >= minimum;
    }
}
