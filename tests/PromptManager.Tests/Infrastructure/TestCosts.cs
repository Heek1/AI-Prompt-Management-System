using Microsoft.Extensions.Options;
using PromptManager.Llm;
using PromptManager.Services;

namespace PromptManager.Tests.Infrastructure;

/// <summary>A clock fixed at a chosen moment.</summary>
public sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

public static class TestCosts
{
    /// <summary>A cost service on the test database; no budget unless given.</summary>
    public static CostService For(TestDatabase database, decimal monthlyBudget = 0, DateTimeOffset? now = null) =>
        new(database.CreateContext(),
            Options.Create(new LlmOptions { MonthlyBudgetUsd = monthlyBudget }),
            now is { } at ? new FixedTime(at) : TimeProvider.System);
}
