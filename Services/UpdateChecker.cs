using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BrightFlyout.Services;

/// <summary>
/// Checks GitHub Releases for a newer version once per day (first check 30 s after
/// startup) and shows a single tray notification per version - never nags twice for
/// the same release. Detection only; no auto-update.
/// </summary>
internal sealed class UpdateChecker : IDisposable
{
    private const string ReleasesLatestUrl =
        "https://api.github.com/repos/pawdy3/brightness-flyout/releases/latest";
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private readonly SettingsService _settings;
    private readonly HttpClient _http = new();
    private readonly CancellationTokenSource _cts = new();

    /// <summary>How to surface a notification (tray balloon). Set before Start().</summary>
    public Action<string, string>? Notify { get; set; }

    // Failures are logged at most once per run - a private repo or being offline
    // is not an app error, and a daily check must not spam the log.
    private bool _failureLogged;

    private void LogFailureOnce(string reason)
    {
        if (!_failureLogged)
        {
            _failureLogged = true;
            Log.Write($"Update check unavailable: {reason}");
        }
    }

    public UpdateChecker(SettingsService settings)
    {
        _settings = settings;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"BrightFlyout/{version}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public void Start() => _ = RunAsync(_cts.Token);

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(FirstCheckDelay, token);

            while (!token.IsCancellationRequested)
            {
                await CheckOnceAsync(token);
                await Task.Delay(CheckInterval, token);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task CheckOnceAsync(CancellationToken token)
    {
        // Update checks are best-effort by design: a private/deleted repo, being
        // offline, rate limiting - none of these may surface errors to the user.
        try
        {
            using var response = await _http.GetAsync(ReleasesLatestUrl, token);
            if (!response.IsSuccessStatusCode)
            {
                LogFailureOnce($"HTTP {(int)response.StatusCode}");
                return; // 404 (no releases / private repo), 403 (rate limit), etc.
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var tag = doc.RootElement.GetProperty("tag_name").GetString();
            if (string.IsNullOrWhiteSpace(tag))
            {
                return;
            }

            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            {
                return;
            }

            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            bool isNewer = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0))
                > new Version(current.Major, current.Minor, Math.Max(current.Build, 0));

            if (isNewer && _settings.LastNotifiedVersion != tag)
            {
                _settings.LastNotifiedVersion = tag;
                Notify?.Invoke(
                    "BrightFlyout update available",
                    $"Version {tag.TrimStart('v', 'V')} is available on GitHub.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogFailureOnce(ex.Message);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _http.Dispose();
    }
}
