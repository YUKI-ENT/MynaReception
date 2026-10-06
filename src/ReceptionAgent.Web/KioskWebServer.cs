using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ReceptionAgent.Kiosk;

namespace ReceptionAgent.Web;

public sealed record StartKioskRequest(string CommandId, KioskAnswers? Answers = null);
public sealed class KioskWebServer(KioskSessions sessions, Func<bool> available) : IAsyncDisposable
{
    private WebApplication? app;
    public string Url { get; private set; } = "";
    public async Task StartAsync(int port, CancellationToken token = default)
    {
        if (app is not null) throw new InvalidOperationException("Webサーバーは起動済みです。");
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); // Do not emit patient requests to terminal/default request logs.
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:" + port);
        builder.WebHost.UseSetting("Kestrel:Limits:MaxRequestBodySize", "8192");
        var web = builder.Build();
        web.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; frame-ancestors 'none'; form-action 'self'; base-uri 'none'";
            if (context.Request.Host.Port != port || context.Request.Host.Host is not ("127.0.0.1" or "localhost") ||
                context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
            { context.Response.StatusCode = 403; return; }
            if (context.Request.Headers.TryGetValue("Origin", out var origin) && origin != "http://" + context.Request.Host ||
                context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
            { context.Response.StatusCode = 403; return; }
            if (HttpMethods.IsPost(context.Request.Method) && context.Request.Headers["X-Kiosk-Request"] != "1")
            { context.Response.StatusCode = 403; return; }
            try { await next(context); }
            catch (KeyNotFoundException) { context.Response.StatusCode = 404; await context.Response.WriteAsJsonAsync(new { message = "セッションが見つかりません。" }); }
            catch (ArgumentException ex) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { message = ex.Message }); }
            catch (InvalidOperationException ex) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { message = ex.Message }); }
            catch (Exception) { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { message = "受付へお声掛けください。接続先で確認が必要です。" }); }
        });
        string Device(HttpContext context)
        {
            if (context.Request.Cookies.TryGetValue("kiosk-device", out var value) && Guid.TryParseExact(value, "N", out _)) return value;
            throw new ArgumentException("開始画面から操作してください。");
        }
        object View(KioskSession session) => new { session.Id, state = session.State.ToString(), session.Version,
            session.Title, session.Message, category = session.Category.ToString(), session.ExpiresAt,
            needsInput = session.MonthDayOnly && session.MonthDayAnswers is null && session.State == KioskSessionState.WaitingForXml,
            confirmedName = session.ConfirmedName,
            page = session.MonthDayOnly && session.MonthDayAnswers is null && session.State == KioskSessionState.WaitingForXml
                ? session.Options.Flow.Page(session.FlowPageId ?? session.Options.Flow.FirstPageId) : null,
            waiting = session.State is KioskSessionState.WaitingForXml or KioskSessionState.LookingUp };
        web.MapGet("/", (HttpContext context) =>
        {
            if (!context.Request.Cookies.TryGetValue("kiosk-device", out var value) || !Guid.TryParseExact(value, "N", out _))
                context.Response.Cookies.Append("kiosk-device", Guid.NewGuid().ToString("N"), new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true });
            return Asset("index.html", "text/html; charset=utf-8");
        });
        web.MapGet("/kiosk.js", () => Asset("kiosk.js", "text/javascript; charset=utf-8"));
        web.MapGet("/kiosk.css", () => Asset("kiosk.css", "text/css; charset=utf-8"));
        web.MapGet("/api/health", () => Results.Ok(new { available = available(), testMode = true }));
        web.MapGet("/api/display", () => { var flow = sessions.DisplayOptions().Flow; return Results.Ok(new { flow.StartTitle, flow.StartMessage, flow.StartButton }); });
        web.MapGet("/api/current", (HttpContext context) => Results.Ok(new { session = sessions.Current(Device(context)) is { } current ? View(current) : null }));
        web.MapPost("/api/sessions", (HttpContext context, StartKioskRequest request) => Results.Ok(View(sessions.Start(Device(context), request.CommandId, request.Answers))));
        web.MapPost("/api/sessions/{id}/answers", (HttpContext context, string id, KioskMonthDayAnswers answers) => Results.Ok(View(sessions.SubmitAnswers(id, Device(context), answers))));
        web.MapPost("/api/sessions/{id}/page", (HttpContext context, string id, KioskFlowAnswer answer) => Results.Ok(View(sessions.AnswerPage(id, Device(context), answer))));
        web.MapGet("/api/sessions/{id}", (HttpContext context, string id) => Results.Ok(View(sessions.Get(id, Device(context)))));
        web.MapPost("/api/sessions/{id}/cancel", (HttpContext context, string id) => Results.Ok(View(sessions.End(id, Device(context), true))));
        web.MapPost("/api/sessions/{id}/acknowledge", (HttpContext context, string id) => Results.Ok(View(sessions.End(id, Device(context), false))));
        try { await web.StartAsync(token); app = web; Url = "http://127.0.0.1:" + port + "/"; }
        catch { await web.DisposeAsync(); throw; }
    }
    private static IResult Asset(string name, string contentType)
    {
        using var input = typeof(KioskWebServer).Assembly.GetManifestResourceStream("ReceptionAgent.Web.Assets." + name)!;
        using var reader = new StreamReader(input); return Results.Text(reader.ReadToEnd(), contentType);
    }
    public async ValueTask DisposeAsync()
    {
        var server = app;
        app = null; Url = "";
        if (server is null) return;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await server.StopAsync(stop.Token).ConfigureAwait(false); }
        finally { await server.DisposeAsync().ConfigureAwait(false); }
    }
}
