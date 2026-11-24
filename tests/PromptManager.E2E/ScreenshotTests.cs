using Microsoft.Playwright;
using PromptManager.Tests.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace PromptManager.E2E;

/// <summary>
/// Regenerates the README screenshots (and a short screen recording) from a
/// seeded demo. Skipped unless PM_SCREENSHOTS names an output folder:
///   PM_SCREENSHOTS=docs/images dotnet test tests/PromptManager.E2E --filter Screenshots
/// </summary>
public sealed class ScreenshotTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly string? OutputDir = Environment.GetEnvironmentVariable("PM_SCREENSHOTS");

    private const string Password = "Demo-password-1";

    [SkippableFactAttribute]
    public async Task Screenshots()
    {
        var dir = Path.GetFullPath(OutputDir!, FindRepoRoot());
        Directory.CreateDirectory(dir);
        var video = Path.Combine(Path.GetTempPath(), "pm-demo-video");
        if (Directory.Exists(video)) Directory.Delete(video, true);

        var context = await app.Browser.NewContextAsync(new()
        {
            BaseURL = app.BaseUrl,
            ViewportSize = new() { Width = 1440, Height = 900 },
            DeviceScaleFactor = 1,
            ColorScheme = ColorScheme.Light,
            RecordVideoDir = video,
            RecordVideoSize = new() { Width = 1440, Height = 900 }
        });
        var page = await context.NewPageAsync();
        page.Dialog += (_, d) => d.AcceptAsync();

        async Task Shot(string name, bool full = false)
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            await page.EvaluateAsync("document.fonts.ready");
            await page.ScreenshotAsync(new() { Path = Path.Combine(dir, name + ".png"), FullPage = full });
        }

        // An admin, so the Costs page is reachable.
        await page.GotoAsync("/Account/Register");
        await Shot("sign-in");
        await page.GetByLabel("E-mail").FillAsync(PromptManagerFactory.AdminEmail);
        await page.GetByLabel("Display name").FillAsync("Olena");
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
        await page.GetByLabel("Confirm password").FillAsync(Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();

        async Task<string> Create(string title, string description, string category, string tool, string tags, string text)
        {
            await page.GotoAsync("/Prompts/Create");
            await page.GetByLabel("Title").FillAsync(title);
            await page.GetByLabel("Description").FillAsync(description);
            await page.GetByLabel("Prompt text").FillAsync(text);
            await page.GetByLabel("Category").SelectOptionAsync(category);
            await page.GetByLabel("Tool").SelectOptionAsync(tool);
            await page.GetByLabel("Tags").FillAsync(tags);
            await page.GetByRole(AriaRole.Button, new() { Name = "Create prompt" }).ClickAsync();
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = title, Level = 1 })).ToBeVisibleAsync();
            return page.Url;
        }

        await Create("Release notes from commits", "Turns a list of commits into notes for users.", "Documentation", "Claude", "writing, release",
            "Write release notes for {{product}} from these commits:\n{{commits}}\n\nGroup them under New, Improved and Fixed.\nLeave out refactoring and test-only commits.");
        await Create("Explain an error message", "For juniors on support duty.", "Bug Fixing", "Gemini", "support, learning",
            "Explain what this error means in plain language, list the two most likely causes and how to check each:\n{{error}}");
        var support = await Create("Support reply", "First answer to a customer e-mail, reviewed by a person before sending.", "Business Analysis", "Claude", "support, email, en",
            "You answer customers of {{shop}}.\nReply to {{question}} politely,\nin under 120 words, and never\npromise a refund you cannot give.");
        var id = support.Split('/').Last();

        // Two runs with saved values.
        await page.GetByLabel("{{shop}}").FillAsync("Northwind Books");
        await page.GetByLabel("{{question}}").FillAsync("Where is my order #4471? It is a week late.");
        await page.GetByLabel("Save values as").FillAsync("late order");
        await page.GetByRole(AriaRole.Button, new() { Name = "Run", Exact = true }).ClickAsync();
        await Expect(page.GetByTestId("run-output")).ToBeVisibleAsync();
        await Shot("run-details");

        // Eval cases: one that echo passes, and the injection pack.
        await page.GotoAsync($"/Evals/Case/{id}");
        await page.GetByLabel("Name", new() { Exact = true }).FillAsync("Names the shop");
        await page.GetByLabel("{{shop}}").FillAsync("Northwind Books");
        await page.GetByLabel("{{question}}").FillAsync("Can I return a book I did not like?");
        await page.GetByLabel("Must contain, one per line").FillAsync("Northwind");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save case" }).ClickAsync();
        await page.Locator("#inj-var").SelectOptionAsync("question");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add attacks" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Run 7 cases" }).ClickAsync();
        await Expect(page.GetByTestId("pass-rate")).ToBeVisibleAsync(new() { Timeout = 20_000 });
        await Shot("eval-run");

        // A second version, its diff, and a comment on it.
        await page.GotoAsync($"/Prompts/CreateVersion/{id}");
        await page.GetByLabel("New text").FillAsync(
            "You answer customers of {{shop}}.\nReply to {{question}} warmly,\nin under 80 words, and never\npromise a refund you cannot give.\nAsk for the order number if it is missing.");
        await page.GetByLabel("What changed").FillAsync("Shorter, warmer, asks for the order number");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save as draft" }).ClickAsync();
        await page.Locator("#v2 summary").ClickAsync();
        await page.Locator("#v2 textarea").FillAsync("Better. Check the tone on the refund case before we promote it.");
        await page.Locator("#v2").GetByRole(AriaRole.Button, new() { Name = "Comment on v2" }).ClickAsync();
        await page.GotoAsync($"/Prompts/Details/{id}");
        await Shot("details");
        await page.GotoAsync($"/Prompts/Compare/{id}");
        await Shot("compare-versions");

        await page.GotoAsync("/");
        await Shot("library");
        await page.GotoAsync("/?search=refund");
        await Shot("search");
        await page.GotoAsync("/Prompts/Import");
        await Shot("import");
        await page.GotoAsync("/Costs");
        await Shot("costs");

        // The same prompt page in the dark theme.
        await page.GetByRole(AriaRole.Button, new() { Name = "Dark" }).ClickAsync();
        await page.GotoAsync($"/Prompts/Details/{id}");
        await Shot("details-dark");
        await page.GotoAsync("/");
        await Shot("library-dark");
        await page.GetByRole(AriaRole.Button, new() { Name = "Auto" }).ClickAsync();

        // A phone.
        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync($"/Prompts/Details/{id}");
        await Shot("phone");

        var recording = page.Video;
        await context.CloseAsync();
        if (recording is not null)
            File.Copy(await recording.PathAsync(), Path.Combine(dir, "demo.webm"), overwrite: true);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PromptManager.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    /// <summary>A fact that only runs when PM_SCREENSHOTS is set.</summary>
    private sealed class SkippableFactAttribute : FactAttribute
    {
        public SkippableFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(OutputDir)) Skip = "Set PM_SCREENSHOTS to an output folder to regenerate the README screenshots.";
        }
    }
}
