using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace RoadGuard.CadParser.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CadDrawings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ParsedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Srid = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CadDrawings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CadGeometryFeatures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CadDrawingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LayerName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    LayerColor = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Geometry = table.Column<Geometry>(type: "geometry", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CadGeometryFeatures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CadGeometryFeatures_CadDrawings_CadDrawingId",
                        column: x => x.CadDrawingId,
                        principalTable: "CadDrawings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CadGeometryFeatures_CadDrawingId",
                table: "CadGeometryFeatures",
                column: "CadDrawingId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CadGeometryFeatures");

            migrationBuilder.DropTable(
                name: "CadDrawings");
        }
    }
}
