using System.Net;
using System.Text;
using System.Text.Json;
using SkpdbDesktop;

internal static class UpdateTests
{
    public static async Task RunAsync()
    {
        var current = new string('a', 40);
        var next = new string('b', 40);
        foreach (var channel in new[] { "Release", "Canary" })
        {
            var suffix = channel == "Canary" ? "-canary" : "";
            var version = $"0.12.43{suffix}+{current[..12]}";
            foreach (var status in new[] { "ahead", "behind", "identical", "diverged" })
            {
                var handler = new Handler(channel, next, $"0.12.43{suffix}+{next[..12]}", status);
                using var client = new HttpClient(handler);
                var result = await new UpdateChecker(client).CheckAsync(channel, version, current);
                Require((result is not null) == (status == "ahead"), $"{channel} ancestry {status}");
                Require(handler.Requests == 2, "compare same-version commits");
            }
            foreach (var target in new[] { current, next })
            {
                var handler = new Handler(channel, target, $"0.12.44{suffix}+{target[..12]}", "ahead");
                using var client = new HttpClient(handler);
                var result = await new UpdateChecker(client).CheckAsync(channel, version, current);
                Require((result is not null) == (target != current), "new version / same commit");
                Require(handler.Requests == 1, "no unnecessary compare");
            }
            using (var client = new HttpClient(new Handler(channel, next, $"0.12.42{suffix}+{next[..12]}", "ahead")))
                Require(await new UpdateChecker(client).CheckAsync(channel, version, current) is null, "no downgrade");
            foreach (var invalid in new[] { "channel", "url", "hash", "version" })
            {
                var handler = new Handler(channel, next, $"0.12.44{suffix}+{next[..12]}", "ahead") { Invalid = invalid };
                using var client = new HttpClient(handler);
                try
                {
                    await new UpdateChecker(client).CheckAsync(channel, version, current);
                    throw new Exception($"Accepted invalid {invalid}");
                }
                catch (InvalidDataException) { }
            }
            using (var client = new HttpClient(new Handler(channel, next, "", "") { Missing = true }))
                Require(await new UpdateChecker(client).CheckAsync(channel, version, current) is null, "unpublished channel");
        }
        Console.WriteLine("UPDATE_TEST_PASS");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Handler(string channel, string commit, string version, string status) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public string Invalid { get; init; } = "";
        public bool Missing { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (Requests == 1)
            {
                Require(request.RequestUri == UpdateChecker.FeedUrl(channel), "channel-specific feed");
                if (Missing) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                var json = JsonSerializer.Serialize(new
                {
                    channel = Invalid == "channel" ? (channel == "Canary" ? "Release" : "Canary") : channel,
                    desktopVersion = Invalid == "version" ? "1.0.0+invalid" : version,
                    commit = Invalid == "hash" ? "invalid" : commit,
                    releaseUrl = Invalid == "url" ? "https://example.com/download" :
                        UpdateChecker.ReleasePrefix + channel.ToLowerInvariant() + "-" + commit[..12]
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
            Require(request.RequestUri?.Host == "api.github.com", "compare on GitHub");
            Require(request.RequestUri!.AbsolutePath.EndsWith("..." + commit), "compare direction");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(new { status })) });
        }
    }
}
