using LidarrCompanion.Helpers;
using LidarrCompanion.Models;
using LidarrCompanion.Services;
using LidarrCompanion.Web.Components;
using LidarrCompanion.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// Resolve the data directory (settings JSON + logs) before anything else touches AppSettings.
// Defaults to a "data" subfolder for local dev; Docker deployment overrides this to an absolute
// mounted path via the DataDirectory configuration key (env var DataDirectory=/data etc).
var configuredDataDirectory = builder.Configuration["DataDirectory"] ?? "data";
var dataDirectory = Path.IsPathRooted(configuredDataDirectory)
    ? configuredDataDirectory
    : Path.Combine(builder.Environment.ContentRootPath, configuredDataDirectory);
Directory.CreateDirectory(dataDirectory);
AppSettings.DataDirectory = dataDirectory;
AppSettings.Load();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Without this, ASP.NET Core's Data Protection keys (which the auth cookie is encrypted/signed
// with) default to living on the container's own ephemeral filesystem - every container
// restart/redeploy would generate a fresh key and silently sign everyone's login cookie out.
// Persisting them into the same mounted data directory as settings/logs keeps sessions valid
// across restarts, matching the 30-day sliding-expiration cookie configured below.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization(options =>
{
    // Everything requires sign-in by default; only pages/endpoints marked [AllowAnonymous]
    // (just the login page and its own sign-in/out endpoints) are reachable without it.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddSingleton<ILogService, LogService>();
// Singleton so TriageService/SiftService (also Singletons) can constructor-inject it directly.
builder.Services.AddSingleton<StatusService>();
builder.Services.AddSingleton<ThemeService>();
builder.Services.AddScoped<IPlaybackService, PlaybackService>();
// Singleton so both the triage page (via TriageService, also a Singleton) and the /cover-art
// page - potentially a different browser tab/circuit - see the same pending-gate state.
builder.Services.AddSingleton<CoverArtGateService>();
// Registered as both a singleton (TriageService injects it directly to enqueue jobs and read the
// cache) and a hosted service (so ASP.NET Core actually starts its background ExecuteAsync loop).
builder.Services.AddSingleton<PrefetchService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PrefetchService>());
// Same singleton + hosted service shape as PrefetchService, and for the same reason: TriageService
// injects it directly to enqueue post-import verification jobs and subscribe to their completion.
builder.Services.AddSingleton<VerifyImportService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<VerifyImportService>());
// Singleton, not Scoped: see the comment on TriageService itself - the /audio/stream endpoint
// below runs in its own per-request DI scope, so it needs to resolve the same instance the
// Blazor circuit is using, which only a Singleton registration guarantees.
builder.Services.AddSingleton<TriageService>();
// Same cross-scope reasoning as TriageService - /audio/sift-stream and /audio/sift-cover below
// need to resolve the same instance the Sift page is using.
builder.Services.AddSingleton<SiftService>();

var app = builder.Build();

// Resolve ILogService now so it starts capturing from app startup, not just whenever the Logs
// page first happens to be visited.
app.Services.GetRequiredService<ILogService>();
Logger.Log("LidarrCompanion.Web started", LogSeverity.Low, new { DataDirectory = dataDirectory });

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

// AllowAnonymous is required here despite the FallbackPolicy already existing: static assets have
// no [AllowAnonymous] attribute of their own, so without this every CSS/JS request - including
// _framework/blazor.web.js, which the login page itself needs just to become interactive - gets
// redirected to /login instead of served. That left the login page permanently unstyled and
// non-interactive for anyone without a valid cookie already, i.e. everyone on their first visit.
app.MapStaticAssets().AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

static string AudioContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".mp3" => "audio/mpeg",
    ".flac" => "audio/flac",
    ".m4a" or ".aac" => "audio/mp4",
    ".ogg" or ".opus" => "audio/ogg",
    ".wma" => "audio/x-ms-wma",
    ".wav" => "audio/wav",
    _ => "application/octet-stream"
};

// Serves the resolved audio file for a given manual-import-file id. Results.File's
// enableRangeProcessing gives the browser <audio> element seeking/scrubbing for free - no
// hand-rolled Range-header handling needed. Used by the main triage screen's track preview
// (Alt+P).
app.MapGet("/audio/stream/{id:int}", (int id, TriageService triage) =>
{
    var file = triage.ManualImportFiles.FirstOrDefault(f => f.Id == id);
    if (file is null) return Results.NotFound();

    var resolved = FileOperationsHelper.ResolveMappedPathAnyKnown(file.Path, true);
    if (!File.Exists(resolved)) return Results.NotFound();

    return Results.File(resolved, AudioContentType(resolved), enableRangeProcessing: true);
})
.RequireAuthorization();

// Sift's own file listing is unrelated to the triage screen's manual-import-file ids, so it gets
// its own id space and its own stream/cover endpoints against SiftService.
app.MapGet("/audio/sift-stream/{id:int}", (int id, SiftService sift) =>
{
    var track = sift.AllTracks.FirstOrDefault(t => t.Id == id);
    if (track is null || !File.Exists(track.FilePath)) return Results.NotFound();

    return Results.File(track.FilePath, AudioContentType(track.FilePath), enableRangeProcessing: true);
})
.RequireAuthorization();

app.MapGet("/audio/sift-cover/{id:int}", (int id, SiftService sift) =>
{
    var track = sift.AllTracks.FirstOrDefault(t => t.Id == id);
    if (track is null) return Results.NotFound();

    var cover = FileAndAudioService.ExtractCoverArt(track.FilePath);
    if (cover is null) return Results.NotFound();

    return Results.File(cover.Value.Data, cover.Value.MimeType);
})
.RequireAuthorization();

app.MapGet("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

app.Run();
