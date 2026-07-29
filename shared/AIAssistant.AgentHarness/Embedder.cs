using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AIAssistant.Harness;

/// <summary>Turns text into a vector for semantic shortlisting (Memory v2).</summary>
public interface IEmbedder
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}

/// <summary>Vector math.</summary>
public static class Vec
{
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || b.Length == 0 || a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

/// <summary>Chooses the embedder from the environment: Azure AI Foundry embeddings if
/// <c>AGENT_EMBED_BASE_URL</c> is set, else a deterministic offline hash embedder (no network, good enough
/// to rank by word overlap for dev + the A/B harness).</summary>
public static class Embeddings
{
    public static IEmbedder FromEnvironment()
    {
        var url = Environment.GetEnvironmentVariable("AGENT_EMBED_BASE_URL");
        return string.IsNullOrWhiteSpace(url) ? new HashEmbedder() : new FoundryEmbedder(url);
    }
}

/// <summary>Deterministic, offline bag-of-words hash embedding (FNV-1a → fixed dims, L2-normalized).
/// Not semantic like a real model, but similar text → similar vectors, so shortlist ranking works with no
/// endpoint. Swapped for <see cref="FoundryEmbedder"/> when embeddings are configured.</summary>
public sealed class HashEmbedder : IEmbedder
{
    public const int Dim = 1024;
    private static readonly Regex Tok = new(@"[A-Za-z0-9]{3,}", RegexOptions.Compiled);
    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
        { "the", "and", "for", "was", "that", "with", "not", "its", "this", "were", "are", "has", "had" };

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var v = new float[Dim];
        var toks = Tok.Matches(text ?? "").Select(m => m.Value.ToLowerInvariant()).Where(t => !Stop.Contains(t)).ToList();
        foreach (var t in toks) v[(int)(Fnv(t) % Dim)] += 1f;                                  // unigrams
        for (var i = 0; i < toks.Count - 1; i++) v[(int)(Fnv(toks[i] + "_" + toks[i + 1]) % Dim)] += 1f; // bigrams
        double n = 0; foreach (var x in v) n += x * x; n = Math.Sqrt(n);
        if (n > 0) for (var i = 0; i < Dim; i++) v[i] = (float)(v[i] / n);
        return Task.FromResult(v);
    }

    private static uint Fnv(string s)
    {
        uint h = 2166136261;
        foreach (var c in s) { h ^= c; h *= 16777619; }
        return h;
    }
}

/// <summary>Azure AI Foundry / OpenAI-compatible embeddings (POST {base}/embeddings). Same auth switch as
/// the chat model: bearer by default, <c>AGENT_EMBED_AUTH=api-key</c> for classic Azure OpenAI.</summary>
public sealed class FoundryEmbedder : IEmbedder
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _baseUrl, _model, _auth;
    private readonly string? _key, _apiVersion;

    public FoundryEmbedder(string baseUrl)
    {
        _baseUrl = baseUrl;
        _key = Environment.GetEnvironmentVariable("AGENT_EMBED_API_KEY") ?? Environment.GetEnvironmentVariable("AGENT_LLM_API_KEY");
        _model = Environment.GetEnvironmentVariable("AGENT_EMBED_MODEL") is { Length: > 0 } m ? m : "text-embedding-3-small";
        _auth = (Environment.GetEnvironmentVariable("AGENT_EMBED_AUTH") ?? "bearer").ToLowerInvariant();
        _apiVersion = Environment.GetEnvironmentVariable("AGENT_EMBED_API_VERSION");
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var url = _baseUrl.TrimEnd('/') + "/embeddings";
        if (!string.IsNullOrWhiteSpace(_apiVersion)) url += (url.Contains('?') ? "&" : "?") + "api-version=" + _apiVersion;
        var payload = new JsonObject { ["model"] = _model, ["input"] = new JsonArray(text ?? "") };
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json") };
        if (!string.IsNullOrWhiteSpace(_key))
        {
            if (_auth == "api-key") req.Headers.Add("api-key", _key);
            else req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        }
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var arr = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))?["data"]?[0]?["embedding"]?.AsArray()
                  ?? throw new InvalidOperationException("no embedding in response");
        return arr.Select(n => (float)n!.GetValue<double>()).ToArray();
    }
}
