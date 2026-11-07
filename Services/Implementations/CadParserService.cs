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
using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;
using System.Security.Cryptography;
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
        private readonly IMemoryCache?              _cache;
        private readonly RoadGuardDbContext?        _db;

        private static readonly TimeSpan DefaultSlidingTtl = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan DefaultAbsoluteTtl = TimeSpan.FromHours(1);

        private static readonly long MaxDiskCacheBytes =
            Math.Clamp(ReadEnvLong("MAX_DISK_CACHE_MB", 64L), 16L, 1024L) * 1024L * 1024L;
        private static readonly long TargetDiskCacheBytes =
            Math.Min(
                MaxDiskCacheBytes,
                Math.Clamp(ReadEnvLong("TARGET_DISK_CACHE_MB", 40L), 8L, 1024L) * 1024L * 1024L);
        private static readonly TimeSpan DiskCacheFileTtl = TimeSpan.FromMinutes(30);
        private static long _lastDiskCleanupTicks;

        // ── Instance-wide heavy CAD execution & bounded wait-queue gate ───── //
        internal static readonly int MaxHeavyParseConcurrency =
            (int)Math.Clamp(ReadEnvLong("MAX_HEAVY_PARSE_CONCURRENCY", 1L), 1L, 16L);
        internal static readonly int MaxHeavyQueueWaiters =
            (int)Math.Clamp(ReadEnvLong("MAX_HEAVY_PARSE_QUEUE", 3L), 0L, 64L);
        private static readonly TimeSpan HeavyQueueWaitTimeout =
            ReadEnvDuration("HEAVY_PARSE_QUEUE_TIMEOUT_MS", "HEAVY_PARSE_QUEUE_TIMEOUT_SECONDS", 45_000L);
        // Per-Python-process execution budget. The clock starts only AFTER the heavy slot is held and the
        // process is about to start: it does NOT include upload, hashing, queue wait or post-processing.
        private static readonly TimeSpan HeavyExecutionTimeout =
            ReadEnvDuration("HEAVY_PARSE_EXECUTION_TIMEOUT_MS", "HEAVY_PARSE_EXECUTION_TIMEOUT_SECONDS", 180_000L);
        // How long we keep killing / polling before declaring the child "not confirmed dead" (slot is then quarantined).
        private static readonly TimeSpan KillConfirmTimeout =
            ReadEnvDuration("HEAVY_PARSE_KILL_CONFIRM_TIMEOUT_MS", "HEAVY_PARSE_KILL_CONFIRM_TIMEOUT_SECONDS", 10_000L);

        private static readonly SemaphoreSlim _heavyExecutionGate =
            new(MaxHeavyParseConcurrency, MaxHeavyParseConcurrency);
        private static int _activeHeavyTasks;
        private static int _queuedHeavyWaiters;
        private static int _quarantinedHeavyTasks;

        public static int ActiveHeavyTaskCount => Volatile.Read(ref _activeHeavyTasks);
        public static int QueuedHeavyWaiterCount => Volatile.Read(ref _queuedHeavyWaiters);
        public static int QuarantinedHeavyTaskCount => Volatile.Read(ref _quarantinedHeavyTasks);

        /// <summary>Factory for the Python child process (replaceable in resilience tests only).</summary>
        internal Func<ProcessStartInfo, IChildProcess> ProcessFactory { get; set; } = si => new SystemChildProcess(si);

        // Bounded striped locks (64 stripes) to prevent dictionary memory leaks
        private static readonly SemaphoreSlim[] _diskStripeLocks = Enumerable.Range(0, 64)
            .Select(_ => new SemaphoreSlim(1, 1))
            .ToArray();

        // Active file reference tracking to prevent deleting files in-use by Python/netDxf
        private static readonly ConcurrentDictionary<string, int> _activeFileLeases = new(StringComparer.OrdinalIgnoreCase);

        public static int ActiveLeaseCount => _activeFileLeases.Count;

        static CadParserService()
        {
            // Proactively clean up any stale staging files on application startup
            SweepOrphanStagingFiles();
        }

        private static long ReadEnvLong(string name, long defaultValue)
        {
            var raw = Environment.GetEnvironmentVariable(name);
            return !string.IsNullOrWhiteSpace(raw) && long.TryParse(raw.Trim(), out var parsed)
                ? parsed
                : defaultValue;
        }

        private static TimeSpan ReadEnvDuration(string msVarName, string secVarName, long defaultMs)
        {
            var rawMs = Environment.GetEnvironmentVariable(msVarName);
            if (!string.IsNullOrWhiteSpace(rawMs) && long.TryParse(rawMs.Trim(), out var ms) && ms > 0)
            {
                return TimeSpan.FromMilliseconds(Math.Clamp(ms, 100L, 600_000L));
            }
            var rawSec = Environment.GetEnvironmentVariable(secVarName);
            if (!string.IsNullOrWhiteSpace(rawSec) && long.TryParse(rawSec.Trim(), out var sec) && sec > 0)
            {
                return TimeSpan.FromSeconds(Math.Clamp(sec, 1L, 600L));
            }
            return TimeSpan.FromMilliseconds(defaultMs);
        }

        /// <summary>
        /// Holds one heavy-execution slot. States: 0 = held, 1 = quarantined (the Python child could not be
        /// confirmed dead, so the slot must NOT be reused), 2 = released. <see cref="Dispose"/> only releases
        /// a slot that is still in state 0, so an outer <c>using</c> can never free a quarantined slot.
        /// </summary>
        internal sealed class HeavyParseSlotLease : IDisposable
        {
            private int _state;

            public bool IsQuarantined => Volatile.Read(ref _state) == 1;

            public void Dispose()
            {
                if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
                {
                    Interlocked.Decrement(ref _activeHeavyTasks);
                    _heavyExecutionGate.Release();
                }
            }

            internal bool TryQuarantine()
            {
                if (Interlocked.CompareExchange(ref _state, 1, 0) == 0)
                {
                    Interlocked.Increment(ref _quarantinedHeavyTasks);
                    return true;
                }
                return false;
            }

            internal void ReleaseAfterChildExit()
            {
                if (Interlocked.CompareExchange(ref _state, 2, 1) == 1)
                {
                    Interlocked.Decrement(ref _quarantinedHeavyTasks);
                    Interlocked.Decrement(ref _activeHeavyTasks);
                    _heavyExecutionGate.Release();
                }
            }
        }

        internal static async Task<HeavyParseSlotLease> AcquireHeavyParseSlotAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // Every slot is blocked by a child process that could not be confirmed dead: fail fast instead of
            // letting requests sit in the queue (and hold their upload on disk) for the whole wait timeout.
            if (QuarantinedHeavyTaskCount >= MaxHeavyParseConcurrency)
            {
                throw new CadServerBusyException(
                    "Máy chủ đang dọn tiến trình xử lý CAD cũ chưa thoát hẳn; chưa nhận tác vụ mới. Vui lòng thử lại sau ít giây.",
                    retryAfterSeconds: 30);
            }

            if (_heavyExecutionGate.Wait(0))
            {
                Interlocked.Increment(ref _activeHeavyTasks);
                return new HeavyParseSlotLease();
            }

            var queuedNow = Interlocked.Increment(ref _queuedHeavyWaiters);
            if (queuedNow > MaxHeavyQueueWaiters)
            {
                Interlocked.Decrement(ref _queuedHeavyWaiters);
                throw new CadServerBusyException(
                    $"Máy chủ đang xử lý tối đa tác vụ CAD nặng ({queuedNow - 1}/{MaxHeavyQueueWaiters} yêu cầu đang chờ). Vui lòng thử lại sau ít giây.",
                    retryAfterSeconds: 10);
            }

            bool entered = false;
            try
            {
                entered = await _heavyExecutionGate.WaitAsync(HeavyQueueWaitTimeout, ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _queuedHeavyWaiters);
            }

            if (!entered)
            {
                throw new CadServerBusyException(
                    $"Yêu cầu chờ slot xử lý CAD vượt quá {(int)Math.Ceiling(HeavyQueueWaitTimeout.TotalSeconds)} giây do máy chủ đang bận. Vui lòng thử lại.",
                    retryAfterSeconds: 10);
            }

            Interlocked.Increment(ref _activeHeavyTasks);
            return new HeavyParseSlotLease();
        }

        // ------------------------------------------------------------------ //
        //  Constructor                                                         //
        // ------------------------------------------------------------------ //
        public CadParserService(ILogger<CadParserService> logger, IMemoryCache? cache = null, RoadGuardDbContext? db = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cache  = cache;
            _db     = db;   // null when no database is configured (persistence is optional)
        }

        private static async Task<string> ComputeFileSha256Async(IFormFile file, CancellationToken ct)
        {
            using var sha = SHA256.Create();
            await using var s = file.OpenReadStream();
            var hashBytes = await sha.ComputeHashAsync(s, ct).ConfigureAwait(false);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        private static SemaphoreSlim GetStripeLock(string fileHash)
        {
            var idx = (uint)fileHash.GetHashCode() % (uint)_diskStripeLocks.Length;
            return _diskStripeLocks[idx];
        }

        /// <summary>
        /// RAII lease ensuring disk file is never deleted while actively referenced by an in-flight operation.
        /// </summary>
        public sealed class FileDiskLease : IDisposable
        {
            public string FilePath { get; }
            private readonly string _fileHash;
            private int _disposed;

            public FileDiskLease(string path, string fileHash)
            {
                FilePath = path;
                _fileHash = fileHash;
                try
                {
                    var now = DateTime.UtcNow;
                    File.SetLastAccessTimeUtc(path, now);
                    File.SetLastWriteTimeUtc(path, now);
                }
                catch { }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    ReleaseLease(FilePath, _fileHash);
                }
            }
        }

        private static void ReleaseLease(string path, string fileHash)
        {
            bool releasedLast = false;
            var lockObj = GetStripeLock(fileHash);
            lockObj.Wait();
            try
            {
                if (_activeFileLeases.TryGetValue(path, out var count))
                {
                    if (count <= 1)
                    {
                        _activeFileLeases.TryRemove(path, out _);
                        releasedLast = true;
                    }
                    else
                    {
                        _activeFileLeases[path] = count - 1;
                    }
                }
            }
            finally
            {
                lockObj.Release();
            }

            if (releasedLast)
            {
                _ = Task.Run(() => EnforceDiskCacheCapacity(force: false, bypassThrottle: true));
            }
        }

        private static void SweepOrphanStagingFiles()
        {
            try
            {
                var tempDir = Path.GetTempPath();
                foreach (var f in Directory.GetFiles(tempDir, "staging_*.dxf"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            catch { }
        }

        public static void EnforceDiskCacheCapacity(bool force = false, bool bypassThrottle = false)
        {
            if (!force && !bypassThrottle)
            {
                var nowTicks = Environment.TickCount64;
                var last = Interlocked.Read(ref _lastDiskCleanupTicks);
                if (nowTicks - last < 5_000) return; // at most once every 5 seconds
                Interlocked.Exchange(ref _lastDiskCleanupTicks, nowTicks);
            }

            try
            {
                var tempDir = Path.GetTempPath();
                var files = Directory.GetFiles(tempDir, "rg_*.dxf");
                if (files.Length == 0) return;

                var fileInfos = new List<FileInfo>();
                long totalBytes = 0;
                var now = DateTime.UtcNow;

                foreach (var f in files)
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        if (fi.Exists)
                        {
                            totalBytes += fi.Length;
                            fileInfos.Add(fi);
                        }
                    }
                    catch { }
                }

                bool exceedsSize = totalBytes > MaxDiskCacheBytes;
                bool hasExpired = fileInfos.Any(fi => (now - (fi.LastWriteTimeUtc > fi.LastAccessTimeUtc ? fi.LastWriteTimeUtc : fi.LastAccessTimeUtc)) > DiskCacheFileTtl);
                if (!force && !exceedsSize && !hasExpired) return;

                fileInfos.Sort((a, b) =>
                {
                    var aTime = a.LastWriteTimeUtc > a.LastAccessTimeUtc ? a.LastWriteTimeUtc : a.LastAccessTimeUtc;
                    var bTime = b.LastWriteTimeUtc > b.LastAccessTimeUtc ? b.LastWriteTimeUtc : b.LastAccessTimeUtc;
                    return aTime.CompareTo(bTime);
                });

                foreach (var fi in fileInfos)
                {
                    var fileName = fi.Name;
                    var fileHash = (fileName.StartsWith("rg_") && fileName.EndsWith(".dxf") && fileName.Length == 71)
                        ? fileName.Substring(3, 64)
                        : fileName;

                    var lockObj = GetStripeLock(fileHash);
                    lockObj.Wait();
                    try
                    {
                        // Double check active lease status under stripe lock
                        if (_activeFileLeases.TryGetValue(fi.FullName, out var inUse) && inUse > 0)
                        {
                            continue;
                        }

                        fi.Refresh();
                        if (!fi.Exists) continue;

                        var lastActivity = fi.LastWriteTimeUtc > fi.LastAccessTimeUtc ? fi.LastWriteTimeUtc : fi.LastAccessTimeUtc;
                        bool isOld = (now - lastActivity) > DiskCacheFileTtl;
                        if (force || isOld || totalBytes > TargetDiskCacheBytes)
                        {
                            try
                            {
                                var len = fi.Length;
                                fi.Delete();
                                totalBytes -= len;
                                _activeFileLeases.TryRemove(fi.FullName, out _);
                            }
                            catch { }
                        }
                    }
                    finally
                    {
                        lockObj.Release();
                    }
                }
            }
            catch { }
        }

        private async Task<FileDiskLease> AcquireDxfOnDiskLeaseAsync(IFormFile file, string fileHash, CancellationToken ct)
        {
            var targetPath = Path.Combine(Path.GetTempPath(), $"rg_{fileHash}.dxf");
            var lockObj = GetStripeLock(fileHash);
            await lockObj.WaitAsync(ct).ConfigureAwait(false);
            string? stagingPath = null;
            bool wroteNewFile = false;
            FileDiskLease lease;
            try
            {
                if (!File.Exists(targetPath) || new FileInfo(targetPath).Length != file.Length)
                {
                    stagingPath = Path.Combine(Path.GetTempPath(), $"staging_{Guid.NewGuid():N}.dxf");
                    try
                    {
                        await using (var fs = new FileStream(stagingPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                        {
                            await file.CopyToAsync(fs, ct).ConfigureAwait(false);
                        }
                        File.Move(stagingPath, targetPath, overwrite: true);
                        stagingPath = null;
                        wroteNewFile = true;
                    }
                    finally
                    {
                        if (stagingPath != null && File.Exists(stagingPath))
                        {
                            try { File.Delete(stagingPath); } catch { }
                        }
                    }
                }

                // Increment active ref-count atomically under stripe lock
                _activeFileLeases.AddOrUpdate(targetPath, 1, (_, c) => c + 1);
                lease = new FileDiskLease(targetPath, fileHash);
            }
            finally
            {
                lockObj.Release();
            }

            // Non-blocking background capacity check (bypass 5s throttle when a new DXF entered /tmp)
            _ = Task.Run(() => EnforceDiskCacheCapacity(force: false, bypassThrottle: wroteNewFile));
            return lease;
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

            var fileHash = await ComputeFileSha256Async(file, cancellationToken).ConfigureAwait(false);
            var cacheKey = $"LAYERS_{fileHash}";
            if (_cache != null && _cache.TryGetValue(cacheKey, out List<string>? cachedLayers) && cachedLayers != null)
            {
                _logger.LogInformation("MemoryCache HIT for GetLayers (Hash={Hash}, Count={Count})", fileHash, cachedLayers.Count);
                return cachedLayers;
            }

            // Take the heavy slot BEFORE copying the upload into /tmp: queued requests then hold only their
            // (ASP.NET-buffered) form file, not a second rg_ copy.
            using var heavySlot = await AcquireHeavyParseSlotAsync(cancellationToken).ConfigureAwait(false);
            if (_cache != null && _cache.TryGetValue(cacheKey, out List<string>? cachedLayersAfterWait) && cachedLayersAfterWait != null)
            {
                return cachedLayersAfterWait;
            }

            using var lease = await AcquireDxfOnDiskLeaseAsync(file, fileHash, cancellationToken).ConfigureAwait(false);
            var tempFile = lease.FilePath;
            try
            {

                try
                {
                    var json = await RunPythonBridgeAsync($"get-layers \"{tempFile}\"", cancellationToken, heavySlot).ConfigureAwait(false);
                    var pyRes = Newtonsoft.Json.JsonConvert.DeserializeObject<PythonLayersResult>(json);
                    if (pyRes is not null && pyRes.Success && pyRes.Layers.Count > 0)
                    {
                        if (_cache != null)
                        {
                            _cache.Set(cacheKey, pyRes.Layers, new MemoryCacheEntryOptions
                            {
                                SlidingExpiration = DefaultSlidingTtl,
                                AbsoluteExpirationRelativeToNow = DefaultAbsoluteTtl,
                                Size = 1
                            });
                        }
                        return pyRes.Layers;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (CadServerBusyException)
                {
                    throw;
                }
                catch (CadSubprocessTerminatedException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Python bridge get-layers unavailable; falling back to netDxf.");
                }

                // Fallback to netDxf (same heavy slot as above, streaming directly from the disk file without MemoryStream copy)
                return await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    using var fs = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var dxfDoc = LoadDxfDocument(fs, file.FileName);
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

                    var sortedList = layerNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                    if (_cache != null && sortedList.Count > 0)
                    {
                        _cache.Set(cacheKey, sortedList, new MemoryCacheEntryOptions
                        {
                            SlidingExpiration = DefaultSlidingTtl,
                            AbsoluteExpirationRelativeToNow = DefaultAbsoluteTtl,
                            Size = 1
                        });
                    }
                    return sortedList;
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
            }
        }


        /// <inheritdoc/>
        public async Task<CadParseRoadResponse> ParseRoadAsync(
            IFormFile file,
            double roadWidth = 7.0,
            string? centerlineLayerName = null,
            double? centralMeridian = null,
            double? scaleFactor = null,
            string? axisOrder = null,
            CancellationToken cancellationToken = default)
        {
            ValidateFile(file);

            var fileHash = await ComputeFileSha256Async(file, cancellationToken).ConfigureAwait(false);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var normLayer = string.IsNullOrWhiteSpace(centerlineLayerName) ? "AUTO" : centerlineLayerName.Trim().ToUpperInvariant();
            var normAxis = string.IsNullOrWhiteSpace(axisOrder) ? "AUTO" : axisOrder.Trim().ToUpperInvariant();
            var cmStr = centralMeridian.HasValue && centralMeridian.Value > 0 ? centralMeridian.Value.ToString("F4", inv) : "AUTO";
            var k0Str = scaleFactor.HasValue && scaleFactor.Value > 0 ? scaleFactor.Value.ToString("F6", inv) : "AUTO";
            var cacheKey = $"ROAD_{fileHash}_{roadWidth.ToString("F4", inv)}_{normLayer}_{cmStr}_{k0Str}_{normAxis}";

            if (_cache != null && _cache.TryGetValue(cacheKey, out CadParseRoadResponse? cachedRoad) && cachedRoad != null)
            {
                _logger.LogInformation("MemoryCache HIT for ParseRoad (Key={Key})", cacheKey);
                return cachedRoad;
            }

            using var heavySlot = await AcquireHeavyParseSlotAsync(cancellationToken).ConfigureAwait(false);
            if (_cache != null && _cache.TryGetValue(cacheKey, out CadParseRoadResponse? cachedRoadAfterWait) && cachedRoadAfterWait != null)
            {
                _logger.LogInformation("MemoryCache HIT for ParseRoad after queue wait (Key={Key})", cacheKey);
                return cachedRoadAfterWait;
            }

            using var lease = await AcquireDxfOnDiskLeaseAsync(file, fileHash, cancellationToken).ConfigureAwait(false);
            var tempFile = lease.FilePath;
            try
            {
                var bridgeArgs = $"parse-road \"{tempFile}\" --width {roadWidth.ToString(inv)}";
                if (!string.IsNullOrWhiteSpace(centerlineLayerName))
                {
                    bridgeArgs += $" --layer \"{centerlineLayerName}\"";
                }
                if (centralMeridian.HasValue && centralMeridian.Value > 0)
                {
                    bridgeArgs += $" --cm {centralMeridian.Value.ToString(inv)}";
                }
                if (scaleFactor.HasValue && scaleFactor.Value > 0)
                {
                    bridgeArgs += $" --k0 {scaleFactor.Value.ToString(inv)}";
                }
                if (!string.IsNullOrWhiteSpace(axisOrder))
                {
                    bridgeArgs += $" --axis-order \"{axisOrder.Trim()}\"";
                }

                var json = await RunPythonBridgeAsync(bridgeArgs, cancellationToken, heavySlot).ConfigureAwait(false);
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

                if (_cache != null && result != null && result.Success)
                {
                    var roadWeight = Math.Clamp((int)Math.Ceiling(((result.Centerline?.Count ?? 0) + (result.RoadSurfacePolygon?.Count ?? 0)) / 1000.0), 2, 10);
                    _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
                    {
                        SlidingExpiration = DefaultSlidingTtl,
                        AbsoluteExpirationRelativeToNow = DefaultAbsoluteTtl,
                        Size = roadWeight
                    });
                }
                return result;
            }
            finally
            {
            }
        }

        /// <inheritdoc/>
        public async Task<CadParseRoadResponse> ParseCoordsAsync(
            string coordinatesText,
            double roadWidth = 7.0,
            double? centralMeridian = null,
            double? scaleFactor = null,
            string? axisOrder = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(coordinatesText))
            {
                return new CadParseRoadResponse
                {
                    Success = false,
                    Error   = "EmptyCoordinates",
                    Detail  = "Vui lòng nhập danh sách tọa độ VN-2000."
                };
            }

            var tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
            try
            {
                var payload = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    text = coordinatesText,
                    scaleFactor = scaleFactor,
                    axisOrder = axisOrder,
                    centralMeridian = centralMeridian
                });
                await File.WriteAllTextAsync(tempFile, payload, new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var bridgeArgs = $"parse-coords \"{tempFile}\" --width {roadWidth.ToString(inv)}";
                if (centralMeridian.HasValue && centralMeridian.Value > 0)
                {
                    bridgeArgs += $" --cm {centralMeridian.Value.ToString(inv)}";
                }
                if (scaleFactor.HasValue && scaleFactor.Value > 0)
                {
                    bridgeArgs += $" --k0 {scaleFactor.Value.ToString(inv)}";
                }
                if (!string.IsNullOrWhiteSpace(axisOrder))
                {
                    bridgeArgs += $" --axis-order \"{axisOrder.Trim()}\"";
                }

                var json = await RunPythonBridgeAsync(bridgeArgs, cancellationToken).ConfigureAwait(false);
                var result = Newtonsoft.Json.JsonConvert.DeserializeObject<CadParseRoadResponse>(json);
                return result ?? new CadParseRoadResponse
                {
                    Success = false,
                    Error   = "ParseCoordsFailed",
                    Detail  = "Không thể dựng tuyến từ danh sách tọa độ."
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

        /// <inheritdoc/>
        public async Task<string> TransformCrsAsync(
            string payloadJson,
            CancellationToken cancellationToken = default)
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
            try
            {
                await File.WriteAllTextAsync(tempFile, payloadJson, new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                var bridgeArgs = $"transform-crs \"{tempFile}\"";
                return await RunPythonBridgeAsync(bridgeArgs, cancellationToken).ConfigureAwait(false);
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
        public async Task<string> ExportRoadGuardDraftAsync(
            string payloadJson,
            CancellationToken cancellationToken = default)
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
            try
            {
                await File.WriteAllTextAsync(tempFile, payloadJson, new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                var bridgeArgs = $"export-roadguard-draft \"{tempFile}\"";
                return await RunPythonBridgeAsync(bridgeArgs, cancellationToken).ConfigureAwait(false);
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
        public async Task<CadRenderOverlayResponse> RenderOverlayAsync(
            IFormFile file,
            string? centerlineLayerName = null,
            double roadWidth = 7.0,
            int outputSizePx = 2048,
            CancellationToken cancellationToken = default)
        {
            ValidateFile(file);

            var fileHash = await ComputeFileSha256Async(file, cancellationToken).ConfigureAwait(false);
            using var heavySlot = await AcquireHeavyParseSlotAsync(cancellationToken).ConfigureAwait(false);
            using var lease = await AcquireDxfOnDiskLeaseAsync(file, fileHash, cancellationToken).ConfigureAwait(false);
            var tempFile = lease.FilePath;

            var bridgeArgs = $"render \"{tempFile}\" --size {outputSizePx} --width {roadWidth}";
            if (!string.IsNullOrWhiteSpace(centerlineLayerName))
            {
                bridgeArgs += $" --layer \"{centerlineLayerName}\"";
            }

            var json = await RunPythonBridgeAsync(bridgeArgs, cancellationToken, heavySlot).ConfigureAwait(false);
            var result = Newtonsoft.Json.JsonConvert.DeserializeObject<CadRenderOverlayResponse>(json);
            if (result is null || !result.Success)
            {
                throw new InvalidOperationException(result?.Detail ?? result?.Error ?? "Raster render failed in Python bridge.");
            }

            return result;
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
            double? scaleFactor      = null,
            string? axisOrder        = null,
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
                "Starting DXF parse: file={FileName}, size={Size}B, srid={Srid}, tessSegs={Segs}, layer={Layer}, cm={Cm}, k0={K0}, axis={Axis}",
                file.FileName, file.Length, srid, tessellationSegments, centerlineLayerName ?? "(auto-detect)", centralMeridian, scaleFactor, axisOrder);

            var fileHash = await ComputeFileSha256Async(file, cancellationToken).ConfigureAwait(false);
            var invDxf = System.Globalization.CultureInfo.InvariantCulture;
            var normLayer = string.IsNullOrWhiteSpace(centerlineLayerName) ? "AUTO" : centerlineLayerName.Trim().ToUpperInvariant();
            var normAxis = string.IsNullOrWhiteSpace(axisOrder) ? "AUTO" : axisOrder.Trim().ToUpperInvariant();
            var cmStr = centralMeridian.HasValue && centralMeridian.Value > 0 ? centralMeridian.Value.ToString("F4", invDxf) : "AUTO";
            var k0Str = scaleFactor.HasValue && scaleFactor.Value > 0 ? scaleFactor.Value.ToString("F6", invDxf) : "AUTO";
            var cacheKey = $"DXF_{fileHash}_{srid}_{tessellationSegments}_{segmentLength.ToString("F2", invDxf)}_{roadWidth.ToString("F2", invDxf)}_{slabLength.ToString("F2", invDxf)}_{normLayer}_{cmStr}_{k0Str}_{normAxis}";

            if (_cache != null && _cache.TryGetValue(cacheKey, out GeoJsonResponse? cachedDxf) && cachedDxf != null)
            {
                _logger.LogInformation("MemoryCache HIT for ParseDxf (Key={Key})", cacheKey);
                return cachedDxf;
            }

            bool isGeoreferencedRequest =
                srid == 4326 &&
                (centralMeridian.HasValue || scaleFactor.HasValue || !string.IsNullOrWhiteSpace(axisOrder));

            using var heavySlot = await AcquireHeavyParseSlotAsync(cancellationToken).ConfigureAwait(false);
            if (_cache != null && _cache.TryGetValue(cacheKey, out GeoJsonResponse? cachedDxfAfterWait) && cachedDxfAfterWait != null)
            {
                _logger.LogInformation("MemoryCache HIT for ParseDxf after queue wait (Key={Key})", cacheKey);
                return cachedDxfAfterWait;
            }

            using var lease = await AcquireDxfOnDiskLeaseAsync(file, fileHash, cancellationToken).ConfigureAwait(false);
            var tempFile = lease.FilePath;
            try
            {

                // ?? Try Python Bridge first for heavy & modern CAD ingestion ? //
                PythonParseResult? pyParseResult = null;
                try
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    var bridgeArgs = $"parse \"{tempFile}\" --srid {srid}";
                    if (!string.IsNullOrWhiteSpace(centerlineLayerName))
                    {
                        bridgeArgs += $" --layer \"{centerlineLayerName}\"";
                    }
                    if (centralMeridian.HasValue && centralMeridian.Value > 0)
                    {
                        bridgeArgs += $" --cm {centralMeridian.Value.ToString(inv)}";
                    }
                    if (scaleFactor.HasValue && scaleFactor.Value > 0)
                    {
                        bridgeArgs += $" --k0 {scaleFactor.Value.ToString(inv)}";
                    }
                    if (!string.IsNullOrWhiteSpace(axisOrder))
                    {
                        bridgeArgs += $" --axis-order \"{axisOrder.Trim()}\"";
                    }

                    var json = await RunPythonBridgeAsync(bridgeArgs, cancellationToken, heavySlot).ConfigureAwait(false);
                    pyParseResult = Newtonsoft.Json.JsonConvert.DeserializeObject<PythonParseResult>(json);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (CadServerBusyException)
                {
                    throw;
                }
                catch (CadSubprocessTerminatedException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var msg = ex.Message;
                    if (isGeoreferencedRequest ||
                        msg.Contains("pyproj", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("ScaleFactor", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("CRS", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("projection", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("geodetic", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("Hệ số co giãn", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("InvalidScaleFactor", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogError(ex, "Python bridge geodetic/CRS operation failed. Fail-closed: refusing netDxf fallback.");
                        if (ex is InvalidOperationException)
                        {
                            throw;
                        }
                        throw new InvalidOperationException(
                            "Không thể định vị GIS WGS-84/VN-2000 bằng bộ phân tích dự phòng netDxf do thiếu thư viện trắc địa Python Bridge. Yêu cầu Python Bridge để chuyển đổi tọa độ VN-2000.",
                            ex);
                    }

                    _logger.LogWarning(ex, "Python bridge unavailable for non-georeferenced request; attempting netDxf fallback for Local CAD 2D only.");
                }

                if (pyParseResult is not null)
                {
                    if (!pyParseResult.Success || !string.IsNullOrEmpty(pyParseResult.Error))
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

                    // In netDxf fallback, coordinates are strictly local CAD planar units; never multiply by MetersPerDegree
                    double totalMeters  = rawLen;
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

                    var pyResponse = new GeoJsonResponse
                    {
                        SourceFileName      = file.FileName,
                        Srid                = srid,
                        TotalFeatureCount   = feats.Count,
                        FeatureCountByLayer = byLayer,
                        FeatureCollection   = new FeatureCollection(feats),
                        Warnings            = warns,
                        Analytics           = engAnalytics
                    };
                    if (_cache != null)
                    {
                        var dxfWeight = Math.Clamp((int)Math.Ceiling(feats.Count / 500.0), 3, 35);
                        _cache.Set(cacheKey, pyResponse, new MemoryCacheEntryOptions
                        {
                            SlidingExpiration = DefaultSlidingTtl,
                            AbsoluteExpirationRelativeToNow = DefaultAbsoluteTtl,
                            Size = dxfWeight
                        });
                    }
                    return pyResponse;
                }

                // ── Fail-closed georeferenced guard BEFORE any stream copy or netDxf load ── //
                if (isGeoreferencedRequest)
                {
                    throw new InvalidOperationException(
                        "Không thể định vị GIS WGS-84/VN-2000 bằng bộ phân tích dự phòng netDxf do thiếu thư viện trắc địa Python Bridge. Yêu cầu Python Bridge để chuyển đổi tọa độ VN-2000.");
                }

                // ── Fallback to legacy netDxf parsing (Local CAD 2D non-georeferenced only) ── //
                // (runs under the same heavy slot acquired at the top of this method)

                var (features, warnings, featuresByLayer, analytics) = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    using var fs = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var dxfDoc = LoadDxfDocument(fs, file.FileName);
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

                    warns.Add("Bản vẽ được nạp bằng bộ phân tích dự phòng netDxf ở chế độ tọa độ phẳng cục bộ (Local CAD 2D) không có định vị GIS quốc gia VN-2000.");

                    // Step 3: Complex entities (Arc, Spline, Polyline with bulges)
                    ProcessAllEntities(dxfDoc, feats, warns, byLayer, tessellationSegments, srid, centerlineLayerName);

                    foreach (var f in feats)
                    {
                        f.Properties["isGeoreferenced"] = false;
                        f.Properties["crsState"] = "LOCAL_CAD_NON_GEOREFERENCED";
                    }

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

                var netDxfResponse = new GeoJsonResponse
                {
                    SourceFileName      = file.FileName,
                    Srid                = srid,
                    TotalFeatureCount   = features.Count,
                    FeatureCountByLayer = featuresByLayer,
                    FeatureCollection   = new FeatureCollection(features),
                    Warnings            = warnings,
                    Analytics           = analytics
                };
                if (_cache != null)
                {
                    var dxfWeight = Math.Clamp((int)Math.Ceiling(features.Count / 500.0), 3, 35);
                    _cache.Set(cacheKey, netDxfResponse, new MemoryCacheEntryOptions
                    {
                        SlidingExpiration = DefaultSlidingTtl,
                        AbsoluteExpirationRelativeToNow = DefaultAbsoluteTtl,
                        Size = dxfWeight
                    });
                }
                return netDxfResponse;
            }
            finally
            {
            }
        }

        private static bool TryHasExited(IChildProcess process, ILogger logger)
        {
            try
            {
                return process.HasExited;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not read HasExited for Python child process; treating it as NOT exited.");
                return false;
            }
        }

        /// <summary>
        /// Kills the child and keeps polling until the OS reports it has exited, or <paramref name="timeout"/> elapses.
        /// Returns true ONLY when exit is confirmed. Kill failures / wait failures never count as success.
        /// </summary>
        internal static async Task<bool> ConfirmChildExitedAsync(IChildProcess process, TimeSpan timeout, ILogger logger)
        {
            var sw = Stopwatch.StartNew();
            var attempt = 0;
            while (true)
            {
                if (TryHasExited(process, logger)) return true;

                attempt++;
                try
                {
                    process.Kill();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Kill of Python child failed (attempt {Attempt}).", attempt);
                }

                var remaining = timeout - sw.Elapsed;
                var wait = remaining <= TimeSpan.Zero
                    ? TimeSpan.FromMilliseconds(50)
                    : TimeSpan.FromMilliseconds(Math.Min(200, remaining.TotalMilliseconds));
                using (var cts = new CancellationTokenSource(wait))
                {
                    try
                    {
                        await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "WaitForExit on Python child failed (attempt {Attempt}).", attempt);
                        await Task.Delay(50).ConfigureAwait(false);
                    }
                }

                if (TryHasExited(process, logger)) return true;
                if (sw.Elapsed >= timeout) return false;
            }
        }

        /// <summary>
        /// The child could not be confirmed dead: keep the slot locked (state = quarantined) and keep killing in the
        /// background. The slot is returned to the gate ONLY after the OS confirms the process exited.
        /// </summary>
        private void QuarantineSlotUntilChildExits(IChildProcess process, HeavyParseSlotLease slot)
        {
            if (!slot.TryQuarantine()) return;

            _logger.LogCritical(
                "Python child process (pid {Pid}) could not be confirmed dead. Heavy slot is QUARANTINED; new work is refused until it exits.",
                SafePid(process));

            _ = Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        if (await ConfirmChildExitedAsync(process, TimeSpan.FromSeconds(5), _logger).ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Quarantine reaper iteration failed; retrying.");
                    }
                    await Task.Delay(1000).ConfigureAwait(false);
                }

                try { process.Dispose(); } catch { }
                slot.ReleaseAfterChildExit();
                _logger.LogWarning("Quarantined Python child exited; heavy slot released.");
            });
        }

        private static int SafePid(IChildProcess process)
        {
            try { return process.Id; } catch { return -1; }
        }

        private static void Observe(Task task)
        {
            _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <summary>
        /// Runs the Python bridge. If <paramref name="heldSlot"/> is supplied the caller already owns the heavy slot
        /// (and disposes it); otherwise a slot is acquired here. Either way the slot is never reusable until the child
        /// process has been confirmed exited (otherwise it is quarantined, see <see cref="QuarantineSlotUntilChildExits"/>).
        /// </summary>
        internal async Task<string> RunPythonBridgeAsync(
            string arguments,
            CancellationToken cancellationToken,
            HeavyParseSlotLease? heldSlot = null)
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

            HeavyParseSlotLease? ownSlot = null;
            var slot = heldSlot;
            if (slot is null)
            {
                ownSlot = await AcquireHeavyParseSlotAsync(cancellationToken).ConfigureAwait(false);
                slot = ownSlot;
            }

            try
            {
                return await RunPythonProcessAsync(scriptPath, arguments, slot, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ownSlot?.Dispose(); // no-op when the slot was quarantined
            }
        }

        private async Task<string> RunPythonProcessAsync(
            string scriptPath,
            string arguments,
            HeavyParseSlotLease slot,
            CancellationToken cancellationToken)
        {
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

            // Execution budget for THIS Python process only (starts now, after the slot is held).
            using var execTimeoutCts = new CancellationTokenSource(HeavyExecutionTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, execTimeoutCts.Token);

            IChildProcess? process = null;
            var started = false;
            var handedToReaper = false;
            try
            {
                process = ProcessFactory(startInfo);
                process.Start();
                started = true;

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                Observe(outputTask);
                Observe(errorTask);

                try
                {
                    await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    var callerCancelled = cancellationToken.IsCancellationRequested;
                    var childGone = await ConfirmChildExitedAsync(process, KillConfirmTimeout, _logger).ConfigureAwait(false);
                    if (!childGone)
                    {
                        QuarantineSlotUntilChildExits(process, slot);
                        handedToReaper = true;
                    }

                    if (callerCancelled)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    throw new CadSubprocessTerminatedException(
                        childGone
                            ? $"Tiến trình xử lý CAD Python vượt quá giới hạn thời gian ({HeavyExecutionTimeout.TotalSeconds:0.#} giây) và đã bị dừng an toàn."
                            : $"Tiến trình xử lý CAD Python vượt quá giới hạn thời gian ({HeavyExecutionTimeout.TotalSeconds:0.#} giây) nhưng CHƯA xác nhận đã thoát; máy chủ khóa slot xử lý cho tới khi tiến trình thoát hẳn.",
                        isTimeout: true);
                }

                var output = await outputTask.ConfigureAwait(false);
                var error = await errorTask.ConfigureAwait(false);

                if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
                {
                    var errSummary = string.IsNullOrWhiteSpace(error)
                        ? $"exit code {process.ExitCode} (tiến trình con bị dừng bất thường / SIGKILL / hết RAM)"
                        : error.Trim();
                    throw new CadSubprocessTerminatedException(
                        $"Tiến trình xử lý CAD Python kết thúc bất thường: {errSummary}",
                        isTimeout: false,
                        exitCode: process.ExitCode);
                }

                return output;
            }
            finally
            {
                if (!handedToReaper)
                {
                    // Every other exit path (success, error, exception): the slot may be reused only once the
                    // child is confirmed gone. If it is not, quarantine instead of releasing.
                    var childGone = true;
                    if (process is not null && started)
                    {
                        childGone = await ConfirmChildExitedAsync(process, KillConfirmTimeout, _logger).ConfigureAwait(false);
                    }

                    if (childGone)
                    {
                        try { process?.Dispose(); } catch { }
                    }
                    else
                    {
                        QuarantineSlotUntilChildExits(process!, slot);
                    }
                }
            }
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
            if (_db is null) return;   // persistence disabled (no connection string configured)

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
