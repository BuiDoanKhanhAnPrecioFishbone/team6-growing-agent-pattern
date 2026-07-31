namespace AIAssistant.Harness;

/// <summary>
/// Token-usage accounting for chat calls — the cost ledger. Every completion the harness makes records its
/// <c>usage</c> here; a caller does <see cref="Reset"/> before a run and <see cref="Snapshot"/> after to
/// measure that run's tokens, then applies per-model prices (which vary by model/region, so pricing lives
/// with the caller, not here). This is what turns "the harness is cheaper" into a number.
/// </summary>
public static class CostLedger
{
    private static long _prompt, _completion;
    private static readonly object _lock = new();

    public static void Add(long promptTokens, long completionTokens)
    {
        lock (_lock) { _prompt += promptTokens; _completion += completionTokens; }
    }

    public static void Reset() { lock (_lock) { _prompt = 0; _completion = 0; } }

    public static (long Prompt, long Completion) Snapshot()
    {
        lock (_lock) { return (_prompt, _completion); }
    }
}
