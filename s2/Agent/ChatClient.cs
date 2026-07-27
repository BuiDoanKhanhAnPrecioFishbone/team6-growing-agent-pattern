using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace AIAssistant.Agent;

public sealed record ChatMessage(string Role, string Content);

/// <summary>
/// Agent-layer config from the environment. <see cref="Enabled"/> is false when no base URL is set —
/// then S2 runs the deterministic <c>MockMoatModel</c> so the harness (and its lesson memory) demos
/// fully offline. Point <c>S2_LLM_BASE_URL</c> at any OpenAI-compatible Azure AI Foundry deployment to
/// swap in a real model; after ART training the served checkpoint is OpenAI-compatible too, so only
/// these vars change.
/// </summary>
public sealed record S2AgentOptions(
    string? BaseUrl, string? ApiKey, string Model, double Temperature,
    string? ApiVersion, string AuthStyle)
{
    public bool Enabled => !string.IsNullOrWhiteSpace(BaseUrl);

    // Each var is read per-agent first (S2_LLM_*), then falls back to a shared value (AGENT_LLM_*) so
    // the team can point all agents at one Foundry deployment, or override a single agent.
    public static S2AgentOptions FromEnvironment()
    {
        static string? Get(params string[] keys) => keys
            .Select(Environment.GetEnvironmentVariable)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        static double GetD(double d, params string[] keys) =>
            double.TryParse(Get(keys), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;

        return new S2AgentOptions(
            BaseUrl: Get("S2_LLM_BASE_URL", "AGENT_LLM_BASE_URL"),
            ApiKey: Get("S2_LLM_API_KEY", "AGENT_LLM_API_KEY"),
            Model: Get("S2_LLM_MODEL", "AGENT_LLM_MODEL") is { Length: > 0 } m ? m : "gpt-4o-mini",
            Temperature: GetD(0.4, "S2_LLM_TEMPERATURE", "AGENT_LLM_TEMPERATURE"),
            ApiVersion: Get("S2_LLM_API_VERSION", "AGENT_LLM_API_VERSION"),
            // "bearer" (OpenAI-compatible & Foundry) or "api-key" (classic Azure OpenAI). Default bearer.
            AuthStyle: (Get("S2_LLM_AUTH", "AGENT_LLM_AUTH") ?? "bearer").ToLowerInvariant());
    }
}

/// <summary>Minimal OpenAI-compatible chat client (POST {BaseUrl}/chat/completions), JSON-mode on.</summary>
public sealed class ChatClient
{
    private readonly HttpClient _http;
    private readonly S2AgentOptions _options;

    public ChatClient(HttpClient http, S2AgentOptions options)
    {
        _http = http;
        _options = options;
    }

    public S2AgentOptions Options => _options;

    public async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("S2_LLM_BASE_URL is not set — the live model is disabled.");

        var url = _options.BaseUrl!.TrimEnd('/') + "/chat/completions";
        if (!string.IsNullOrWhiteSpace(_options.ApiVersion)) // classic Azure OpenAI needs ?api-version=...
            url += (url.Contains('?') ? "&" : "?") + "api-version=" + _options.ApiVersion;

        var payload = new JsonObject
        {
            ["model"] = _options.Model, // Azure: the deployment name (ignored on classic per-deployment URLs)
            ["temperature"] = _options.Temperature,
            ["max_tokens"] = 1200,
            ["response_format"] = new JsonObject { ["type"] = "json_object" },
            ["messages"] = new JsonArray(messages
                .Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content })
                .ToArray()),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            if (_options.AuthStyle == "api-key")
                request.Headers.Add("api-key", _options.ApiKey); // classic Azure OpenAI
            else
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey); // OpenAI-compatible / Foundry
        }

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        var node = JsonNode.Parse(body);
        return node?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
               ?? throw new InvalidOperationException("chat completion had no message content");
    }
}
