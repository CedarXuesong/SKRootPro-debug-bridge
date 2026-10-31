using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SkpdbDesktop;

public sealed record AvailableUpdate(string Version, Uri ReleaseUrl);

public sealed class UpdateChecker
{
    public const string Repository = "CedarXuesong/SKRootPro-debug-bridge";
    public const string ReleasePrefix = "https://github.com/" + Repository + "/releases/tag/";
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(12),
        MaxResponseContentBufferSize = 128 * 1024
    };
    private readonly HttpClient _client;

    public UpdateChecker(HttpClient? client = null) => _client = client ?? Client;

    public static Uri FeedUrl(string channel) => channel switch
    {
        "Canary" => new($"https://github.com/{Repository}/releases/download/updates/canary.json"),
        "Release" => new($"https://github.com/{Repository}/releases/download/updates/release.json"),
        _ => throw new ArgumentException("Unknown update channel", nameof(channel))
    };

    public async Task<AvailableUpdate?> CheckAsync(string channel, string currentVersion,
        string currentCommit, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, FeedUrl(channel));
        request.Headers.UserAgent.ParseAdd("SKPDB-UpdateCheck/1.0");
        using var response = await _client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var feed = JsonSerializer.Deserialize<UpdateFeed>(
            await response.Content.ReadAsStringAsync(cancellationToken))
            ?? throw new InvalidDataException("Empty update feed");
        if (feed.Channel != channel || !IsCommit(feed.Commit) || !IsCommit(currentCommit) ||
            !IsReleaseUrl(feed.ReleaseUrl, channel, feed.Commit))
            throw new InvalidDataException("Invalid update channel or source");
        if (!TryVersion(feed.DesktopVersion, channel, feed.Commit, out var latest) ||
            !TryVersion(currentVersion, channel, currentCommit, out var current))
            throw new InvalidDataException("Invalid update version");
        if (latest < current || feed.Commit.Equals(currentCommit, StringComparison.OrdinalIgnoreCase)) return null;
        if (latest == current)
        {
            // Commit hashes have no ordering; only an actual descendant is an update.
            using var compare = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{Repository}/compare/{currentCommit}...{feed.Commit}");
            compare.Headers.UserAgent.ParseAdd("SKPDB-UpdateCheck/1.0");
            using var comparison = await _client.SendAsync(compare, cancellationToken);
            comparison.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await comparison.Content.ReadAsStringAsync(cancellationToken));
            if (json.RootElement.GetProperty("status").GetString() != "ahead") return null;
        }
        return new AvailableUpdate(feed.DesktopVersion, new Uri(feed.ReleaseUrl));
    }

    private static bool IsCommit(string? value) => value is not null && Regex.IsMatch(value, "^[0-9a-f]{40}$");

    private static bool IsReleaseUrl(string url, string channel, string commit) =>
        url == ReleasePrefix + channel.ToLowerInvariant() + "-" + commit[..12];

    private static bool TryVersion(string text, string channel, string commit, out Version value)
    {
        value = new Version();
        if (string.IsNullOrEmpty(text)) return false;
        var suffix = channel == "Canary" ? "-canary" : "";
        var parts = text.Split('+');
        return parts.Length == 2 && parts[1] == commit[..12] &&
               parts[0].EndsWith(suffix, StringComparison.Ordinal) &&
               Regex.IsMatch(parts[0], @"^\d+\.\d+\.\d+" + Regex.Escape(suffix) + "$") &&
               Version.TryParse(parts[0][..(parts[0].Length - suffix.Length)], out value!);
    }

    private sealed record UpdateFeed(
        [property: JsonPropertyName("channel")] string Channel,
        [property: JsonPropertyName("desktopVersion")] string DesktopVersion,
        [property: JsonPropertyName("commit")] string Commit,
        [property: JsonPropertyName("releaseUrl")] string ReleaseUrl);
}
