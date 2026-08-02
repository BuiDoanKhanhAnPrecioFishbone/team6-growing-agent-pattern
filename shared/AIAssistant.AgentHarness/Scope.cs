namespace AIAssistant.Harness;

// ─────────────────────────────────────────────────────────────────────────────
// Memory SCOPE — the axis that gives you BOTH org-wide compounding AND per-user
// personalization from one mechanism. A lesson has an Owner:
//   ""            → GLOBAL  (everyone benefits — the shared "diamond mine")
//   "tenant:acme" → TENANT  (a team's conventions)
//   "user:alice"  → USER    (this person's preferences & corrections — personalization)
// Retrieval merges all scopes that apply to the request and ranks the MOST SPECIFIC
// first, so a user's own lesson wins over a global one at equal relevance. Personalization
// isn't a separate system — it's a scope. Set AgentFeatures.User and the same free-data
// loop that makes the agent smarter also makes it yours.
// ─────────────────────────────────────────────────────────────────────────────
public static class Scope
{
    public static string Global => "";
    public static string User(string id) => string.IsNullOrEmpty(id) ? "" : $"user:{id}";
    public static string Tenant(string id) => string.IsNullOrEmpty(id) ? "" : $"tenant:{id}";

    /// <summary>The most-specific scope a request writes into: personal &gt; team &gt; global.</summary>
    public static string Of(AgentFeatures f) =>
        !string.IsNullOrEmpty(f.User) ? User(f.User) : !string.IsNullOrEmpty(f.Tenant) ? Tenant(f.Tenant) : Global;

    /// <summary>Does a lesson with this owner apply to this request? Global always; user/tenant if it matches.</summary>
    public static bool Matches(string owner, AgentFeatures f) =>
        string.IsNullOrEmpty(owner) || owner == User(f.User) || owner == Tenant(f.Tenant);

    /// <summary>Specificity for ranking: user (2) &gt; tenant (1) &gt; global (0).</summary>
    public static int Rank(string owner) =>
        owner.StartsWith("user:", StringComparison.Ordinal) ? 2
        : owner.StartsWith("tenant:", StringComparison.Ordinal) ? 1 : 0;
}
