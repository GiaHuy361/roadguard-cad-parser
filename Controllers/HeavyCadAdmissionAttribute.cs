using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using RoadGuard.CadParser.DTOs;
using RoadGuard.CadParser.Services.Implementations;

namespace RoadGuard.CadParser.Controllers
{
    /// <summary>
    /// Resource filter (runs before model binding, i.e. before the multipart form is read) that admits at most
    /// <c>MAX_HEAVY_PARSE_CONCURRENCY + MAX_HEAVY_PARSE_QUEUE</c> DXF-carrying requests at a time.
    /// Excess requests receive a retryable 503 without buffering their upload to /tmp.
    /// The ticket is released from <see cref="HttpResponse.OnCompleted(Func{Task})"/>, which runs after the
    /// buffered form / temp files registered later have been disposed (callbacks run in LIFO order).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class HeavyCadAdmissionAttribute : Attribute, IAsyncResourceFilter
    {
        private static readonly JsonSerializerSettings _json = new()
        {
            ContractResolver  = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
        };

        public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
        {
            var http = context.HttpContext;

            if (CadParserService.QuarantinedHeavyTaskCount >= CadParserService.MaxHeavyParseConcurrency)
            {
                await RejectAsync(http,
                    "Máy chủ đang dọn tiến trình xử lý CAD cũ chưa thoát hẳn. Vui lòng thử lại sau ít giây.", 30)
                    .ConfigureAwait(false);
                context.Result = new EmptyResult();
                return;
            }

            var ticket = HeavyRequestAdmission.TryEnter();
            if (ticket is null)
            {
                await RejectAsync(http,
                    $"Máy chủ đang xử lý tối đa số bản vẽ CAD cùng lúc ({HeavyRequestAdmission.Capacity}). Vui lòng thử lại sau ít giây.", 10)
                    .ConfigureAwait(false);
                context.Result = new EmptyResult();
                return;
            }

            // Registered BEFORE the form is read => runs AFTER the form's temp files are disposed.
            http.Response.OnCompleted(() =>
            {
                ticket.Dispose();
                return Task.CompletedTask;
            });

            await next().ConfigureAwait(false);
        }

        private static async Task RejectAsync(HttpContext http, string detail, int retryAfterSeconds)
        {
            http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            http.Response.Headers["Retry-After"] = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            http.Response.ContentType = "application/json";
            var body = JsonConvert.SerializeObject(new CadParserErrorResponse
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable,
                Error      = "ServerBusy",
                Detail     = detail,
            }, _json);
            await http.Response.WriteAsync(body).ConfigureAwait(false);
            await http.Response.CompleteAsync().ConfigureAwait(false);

            // Discard the unread upload WITHOUT buffering it (no form parsing, no temp file, no RAM),
            // so the client receives the 503 instead of a connection reset mid-upload.
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await http.Request.Body.CopyToAsync(Stream.Null, cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // Client went away or drain timed out; nothing buffered either way.
            }
        }
    }
}
