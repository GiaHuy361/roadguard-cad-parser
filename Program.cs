using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using RoadGuard.CadParser.Data;
using RoadGuard.CadParser.Services.Implementations;
using RoadGuard.CadParser.Services.Interfaces;

// ============================================================================
//  RoadGuard.CadParser — ASP.NET Core 8 entry point
//
//  Service registrations:
//    • ICadParserService  → CadParserService  (Scoped — one instance per request)
//
//  Key configuration:
//    • Multipart form-data body limit raised to 100 MB for DXF uploads.
//    • Newtonsoft.Json used for GeoJSON serialization (System.Text.Json does
//      not support GeoJSON.Net''s polymorphic IGeometryObject hierarchy).
//    • Swagger/OpenAPI enabled in Development for API exploration.
// ============================================================================

var builder = WebApplication.CreateBuilder(args);

// ── Logging ─────────────────────────────────────────────────────────────── //
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// ── CAD Parser services (core DI registrations) ──────────────────────────── //
// Scoped: CadParserService is stateless per-request; safe for concurrent use.
builder.Services.AddScoped<ICadParserService, CadParserService>();

// EF Core + SQL Server with NetTopologySuite spatial extension //
builder.Services.AddDbContext<RoadGuardDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("Default"),
        sqlOptions => sqlOptions.UseNetTopologySuite()
    )
);

// ── ASP.NET Core MVC + Newtonsoft.Json ──────────────────────────────────── //
// Newtonsoft.Json is required because GeoJSON.Net uses interface-typed geometry
// properties that System.Text.Json cannot polymorphically serialise without
// significant custom converter plumbing.
builder.Services
    .AddControllers()
    .AddNewtonsoftJson(options =>
    {
        options.SerializerSettings.ContractResolver =
            new CamelCasePropertyNamesContractResolver();
        options.SerializerSettings.NullValueHandling =
            Newtonsoft.Json.NullValueHandling.Ignore;
        options.SerializerSettings.Formatting =
            Newtonsoft.Json.Formatting.None;
    });

// ── Multipart / form-data body size limits ──────────────────────────────── //
// Raise the Kestrel and IIS Express limits so 100 MB DXF files can be uploaded.
builder.Services.Configure<FormOptions>(opt =>
{
    opt.MultipartBodyLengthLimit = 104_857_600; // 100 MB
    opt.ValueLengthLimit         = int.MaxValue;
    opt.MultipartHeadersLengthLimit = int.MaxValue;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 104_857_600; // 100 MB
});

// ── OpenAPI / Swagger ────────────────────────────────────────────────────── //
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title       = "RoadGuard CAD Parser API",
        Version     = "v1",
        Description = "Parses AutoCAD DXF files into GeoJSON / SQL Server Spatial geometries " +
                      "for the RoadGuard infrastructure management system. " +
                      "All parsed geometries support SRID 4326, 32648, and 32649."
    });
    // Include XML doc comments if generated (add <GenerateDocumentationFile> to csproj)
    var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (System.IO.File.Exists(xmlPath))
        c.IncludeXmlComments(xmlPath);
});

// ── CORS (configurable for production) ──────────────────────────────────── //
builder.Services.AddCors(options =>
{
    options.AddPolicy("CadParserCorsPolicy", policy =>
    {
        policy
            .AllowAnyOrigin()   // Tighten to specific origins in production
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

// ── Health checks ────────────────────────────────────────────────────────── //
builder.Services.AddHealthChecks();

// ============================================================================
//  Build & configure the HTTP pipeline
// ============================================================================
var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "RoadGuard CAD Parser v1");
        c.RoutePrefix = string.Empty; // Serve Swagger at root
    });
    app.UseDeveloperExceptionPage();
}
else
{
    // Production: generic error page, HSTS
    app.UseExceptionHandler("/error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseCors("AllowAll");
app.UseRouting();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

app.Run();
