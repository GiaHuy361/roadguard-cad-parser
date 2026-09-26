using Microsoft.EntityFrameworkCore;
using RoadGuard.CadParser.Entities;

namespace RoadGuard.CadParser.Data
{
    /// <summary>
    /// EF Core DbContext for the RoadGuard CAD Parser module.
    ///
    /// Supports:
    ///   - SQL Server with NetTopologySuite spatial extension (geometry column type).
    ///   - Fluent API configuration for all entity relationships and column constraints.
    /// </summary>
    public sealed class RoadGuardDbContext : DbContext
    {
        public RoadGuardDbContext(DbContextOptions<RoadGuardDbContext> options)
            : base(options) { }

        // ------------------------------------------------------------------ //
        //  DbSets                                                              //
        // ------------------------------------------------------------------ //

        /// <summary>Parsed CAD drawings (one per uploaded DXF file).</summary>
        public DbSet<CadDrawing> CadDrawings => Set<CadDrawing>();

        /// <summary>Individual geometry features extracted from each drawing.</summary>
        public DbSet<CadGeometryFeature> CadGeometryFeatures => Set<CadGeometryFeature>();

        // ------------------------------------------------------------------ //
        //  Model configuration                                                 //
        // ------------------------------------------------------------------ //

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // ── CadDrawing ── //
            modelBuilder.Entity<CadDrawing>(e =>
            {
                e.ToTable("CadDrawings");
                e.HasKey(d => d.Id);
                e.Property(d => d.Id).ValueGeneratedNever(); // App-generated Guid
                e.Property(d => d.FileName).IsRequired().HasMaxLength(512);
                e.Property(d => d.ParsedAtUtc).IsRequired();
                e.Property(d => d.Srid).IsRequired();

                // 1-to-many: one drawing → many geometry features
                e.HasMany(d => d.GeometryFeatures)
                 .WithOne(f => f.CadDrawing)
                 .HasForeignKey(f => f.CadDrawingId)
                 .OnDelete(DeleteBehavior.Cascade);
            });

            // ── CadGeometryFeature ── //
            modelBuilder.Entity<CadGeometryFeature>(e =>
            {
                e.ToTable("CadGeometryFeatures");
                e.HasKey(f => f.Id);
                e.Property(f => f.Id).ValueGeneratedNever();
                e.Property(f => f.LayerName).IsRequired().HasMaxLength(255);
                e.Property(f => f.LayerColor).HasMaxLength(64);

                // NTS Geometry column: stored as SQL Server geometry type
                e.Property(f => f.Geometry)
                 .IsRequired()
                 .HasColumnType("geometry");

                // Index for faster spatial queries per drawing
                e.HasIndex(f => f.CadDrawingId).HasDatabaseName("IX_CadGeometryFeatures_CadDrawingId");
            });
        }
    }
}
