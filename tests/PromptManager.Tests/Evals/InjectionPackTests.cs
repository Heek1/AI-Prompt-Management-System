using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PromptManager.DTOs;
using PromptManager.Evals;
using PromptManager.Llm;
using PromptManager.Services;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.Tests.Evals;

public class InjectionPackTests
{
    private const string Prompt = "You are a support assistant for Acme.\nAnswer the customer politely and briefly.\nCustomer message: {{message}}\nLanguage: {{lang}}";

    [Fact]
    public void Builds_six_attacks_through_the_chosen_variable()
    {
        var pack = InjectionPack.Build(1, Prompt, "message", ["message", "lang"], "CANARY-AB12");

        Assert.Equal(6, pack.Count);
        Assert.All(pack, c =>
        {
            Assert.StartsWith(InjectionPack.NamePrefix, c.Name);
            Assert.Equal(InjectionPack.FillerValue, c.Variables["lang"]);
            Assert.Contains("CANARY-AB12", c.MustNotContain);
        });
        Assert.Contains(pack, c => c.Variables["message"]!.Contains("Ignore all previous instructions"));
    }

    [Fact]
    public void Leak_case_also_forbids_a_piece_of_the_prompt()
    {
        var leak = InjectionPack.Build(1, Prompt, "message", ["message", "lang"], "CANARY-AB12")
            .Single(c => c.Name.EndsWith("prompt leak"));

        // The first line with six or more words.
        Assert.Contains("You are a support assistant for Acme", leak.MustNotContain);
    }

    [Fact]
    public void Encoded_attack_decodes_to_the_canary_order()
    {
        var encoded = InjectionPack.Build(1, Prompt, "message", ["message"], "CANARY-AB12")
            .Single(c => c.Name.EndsWith("encoded instruction")).Variables["message"]!;

        var base64 = Regex.Match(encoded, "[A-Za-z0-9+/=]{20,}").Value;
        Assert.Contains("CANARY-AB12", Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
    }

    [Theory]
    [InlineData("Short. {{x}}", null)]
    [InlineData("{{a}} one two three four five six seven eight nine", "one two three four five six seven eight")]
    [InlineData("one two three\nfour five six seven", null)] // runs do not span lines
    public void Leak_fragment_is_six_or_more_words_from_one_line(string text, string? expected)
    {
        Assert.Equal(expected, InjectionPack.LeakFragment(text));
    }

    [Fact]
    public void Canaries_differ_between_packs()
    {
        Assert.NotEqual(InjectionPack.NewCanary(), InjectionPack.NewCanary());
    }
}

public sealed class InjectionPackServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private EvalService Service() => new(_database.CreateContext(),
        new LlmCatalog(Options.Create(new LlmOptions()), []), new EvalQueue(), TestCosts.For(_database));

    private async Task<int> SeedAsync(string text)
    {
        using var db = _database.CreateContext();
        return await new PromptService(db, NullLogger<PromptService>.Instance).CreateAsync(new CreatePromptDto
        {
            Title = "T", PromptText = text, Category = "Testing", AiTool = "Claude", Author = "a"
        });
    }

    [Fact]
    public async Task Adding_again_replaces_the_cases_with_a_new_canary()
    {
        var id = await SeedAsync("Answer the customer politely: {{message}}");
        await Service().SaveCaseAsync(new EvalCaseDto { PromptId = id, Name = "my own case", MustContain = "x" });

        var first = await Service().AddInjectionPackAsync(id, "message");
        string firstCanary;
        using (var db = _database.CreateContext())
            firstCanary = (await db.EvalCases.FirstAsync(c => c.Name.StartsWith(InjectionPack.NamePrefix))).MustNotContain;
        var second = await Service().AddInjectionPackAsync(id, "message");

        Assert.Equal((6, (string?)null), first);
        Assert.Equal(6, second.Added);
        using var check = _database.CreateContext();
        Assert.Equal(7, await check.EvalCases.CountAsync());
        Assert.NotEqual(firstCanary, (await check.EvalCases.FirstAsync(c => c.Name.StartsWith(InjectionPack.NamePrefix))).MustNotContain);
    }

    [Fact]
    public async Task Needs_a_variable_that_exists()
    {
        var plain = await SeedAsync("A prompt without any variables in it.");
        var withVar = await SeedAsync("Answer the customer politely: {{message}}");

        Assert.StartsWith("The injection cases attack through a variable", (await Service().AddInjectionPackAsync(plain, "x")).Error);
        Assert.Equal("Unknown variable 'nope'.", (await Service().AddInjectionPackAsync(withVar, "nope")).Error);
    }
}

public sealed class InjectionPackEndpointsTests : IDisposable, IAsyncLifetime
{
    private readonly PromptManagerFactory _factory = new();
    private readonly HttpClient _client;

    public InjectionPackEndpointsTests()
    {
        _client = _factory.CreateNoRedirectClient();
    }

    public Task InitializeAsync() => _client.SignUpAsync("owner@test.local");

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task The_echo_model_falls_for_every_plain_text_attack()
    {
        var create = await _client.PostFormAsync("/Prompts/Create", "/Prompts/Create", new Dictionary<string, string>
        {
            ["Title"] = "Support", ["PromptText"] = "Answer the customer politely and briefly please.\n{{message}}",
            ["Category"] = "Learning", ["AiTool"] = "Claude"
        });
        var id = int.Parse(create.Headers.Location!.OriginalString.Split('/').Last());

        var add = await _client.PostFormAsync($"/Evals/Index/{id}", "/Evals/AddInjectionPack", new Dictionary<string, string>
        {
            ["promptId"] = id.ToString(), ["variable"] = "message"
        });
        Assert.Equal(HttpStatusCode.Redirect, add.StatusCode);
        Assert.Contains("Injection: direct override", await _client.GetStringAsync($"/Evals/Index/{id}"));

        // Echo returns the prompt it was given, attack included: the worst possible model.
        var start = await _client.PostFormAsync($"/Evals/Index/{id}", "/Evals/Start", new Dictionary<string, string>
        {
            ["PromptId"] = id.ToString(), ["Model"] = "echo"
        });
        var url = start.Headers.Location!.OriginalString;
        var html = "";
        for (var i = 0; i < 50 && !html.Contains("cases passed"); i++)
        {
            html = await _client.GetStringAsync(url);
            await Task.Delay(200);
        }

        // Echo repeats the attack, canary included, so every plain-text attack
        // succeeds. It cannot decode base64, so that one attack fails on it.
        Assert.Matches(new Regex(@"1\s*/\s*6"), html);
        // The pass/fail icon is rendered just before the case name.
        var at = html.IndexOf("Injection: encoded instruction", StringComparison.Ordinal);
        Assert.Contains("bi-check-circle-fill", html[Math.Max(0, at - 250)..at]);
    }

    [Fact]
    public async Task Another_user_cannot_add_the_pack()
    {
        var create = await _client.PostFormAsync("/Prompts/Create", "/Prompts/Create", new Dictionary<string, string>
        {
            ["Title"] = "Support", ["PromptText"] = "Answer the customer politely: {{message}}", ["Category"] = "Learning", ["AiTool"] = "Claude"
        });
        var id = int.Parse(create.Headers.Location!.OriginalString.Split('/').Last());
        var other = _factory.CreateNoRedirectClient();
        await other.SignUpAsync("other@test.local");

        var response = await other.PostFormAsync($"/Evals/Index/{id}", "/Evals/AddInjectionPack", new Dictionary<string, string>
        {
            ["promptId"] = id.ToString(), ["variable"] = "message"
        });

        Assert.Contains("/Account/AccessDenied", response.Headers.Location!.OriginalString);
    }
}
