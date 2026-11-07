using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

[assembly: InternalsVisibleTo("RoadGuard.CadParser.ResilienceTests")]

namespace RoadGuard.CadParser.Services.Implementations
{
    /// <summary>
    /// Minimal seam over the Python child process so the kill / confirm-exit / quarantine logic can be
    /// exercised deterministically (including a process that refuses to die) without OS-level tricks.
    /// </summary>
    internal interface IChildProcess : IDisposable
    {
        int Id { get; }
        bool HasExited { get; }
        int ExitCode { get; }
        StreamReader StandardOutput { get; }
        StreamReader StandardError { get; }
        void Start();
        void Kill();
        Task WaitForExitAsync(CancellationToken cancellationToken);
    }

    internal sealed class SystemChildProcess : IChildProcess
    {
        private readonly Process _process;

        public SystemChildProcess(ProcessStartInfo startInfo)
        {
            _process = new Process { StartInfo = startInfo };
        }

        public int Id => _process.Id;
        public bool HasExited => _process.HasExited;
        public int ExitCode => _process.ExitCode;
        public StreamReader StandardOutput => _process.StandardOutput;
        public StreamReader StandardError => _process.StandardError;

        public void Start() => _process.Start();

        public void Kill() => _process.Kill(entireProcessTree: true);

        public Task WaitForExitAsync(CancellationToken cancellationToken) => _process.WaitForExitAsync(cancellationToken);

        public void Dispose() => _process.Dispose();
    }

    /// <summary>
    /// Instance-wide admission control for requests that carry a large DXF upload. The ticket is taken
    /// by a resource filter BEFORE the multipart form is read, so requests beyond
    /// <c>concurrency + queue</c> never buffer their upload into /tmp.
    /// </summary>
    public static class HeavyRequestAdmission
    {
        private static readonly int _capacity =
            CadParserService.MaxHeavyParseConcurrency + CadParserService.MaxHeavyQueueWaiters;
        private static readonly SemaphoreSlim _tickets = new(_capacity, _capacity);

        public static int Capacity => _capacity;
        public static int InFlight => _capacity - _tickets.CurrentCount;

        /// <summary>Returns a ticket, or null when the instance is already at capacity.</summary>
        public static IDisposable? TryEnter()
            => _tickets.Wait(0) ? new Ticket() : null;

        private sealed class Ticket : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _tickets.Release();
                }
            }
        }
    }
}
