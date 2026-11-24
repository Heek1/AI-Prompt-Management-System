using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace PromptManager.E2E;

/// <summary>
/// D9: the main scenarios in a real browser, with the real JavaScript, CSS
/// and security headers. The integration tests check the server's HTML; these
/// check what a person actually sees and clicks.
/// </summary>
public sealed class MainFlowTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static async Task SignUpAsync(IPage page, string email, string name)
    {
        await page.GotoAsync("/Account/Register");
        await page.GetByLabel("E-mail").FillAsync(email);
        await page.GetByLabel("Display name").FillAsync(name);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync("E2e-password-1");
        await page.GetByLabel("Confirm password").FillAsync("E2e-password-1");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create account" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Library", Exact = true })).ToBeVisibleAsync();
    }

    private static async Task<string> CreatePromptAsync(IPage page, string title)
    {
        await page.GotoAsync("/Prompts/Create");
        await page.GetByLabel("Title").FillAsync(title);
        await page.GetByLabel("Prompt text").FillAsync("Say hello to {{name}} politely.\nKeep it short.");
        await page.GetByLabel("Category").SelectOptionAsync("Learning");
        await page.GetByLabel("Tool").SelectOptionAsync("Claude");
        await page.GetByLabel("Tags").FillAsync("greeting, e2e");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create prompt" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = title, Level = 1 })).ToBeVisibleAsync();
        return page.Url;
    }

    [Fact]
    public async Task Write_run_test_and_release_a_prompt()
    {
        var (context, page, errors) = await app.NewPageAsync();
        await using var _ = context;
        await SignUpAsync(page, "writer@e2e.test", "Writer");
        var promptUrl = await CreatePromptAsync(page, "Greeter E2E");

        // The sheet numbers the lines and marks the variable.
        await Expect(page.Locator("#promptText .pm-line")).ToHaveCountAsync(2);
        await Expect(page.Locator("#promptText .pm-var")).ToHaveTextAsync("{{name}}");

        // Run on the offline echo model.
        await page.GetByLabel("{{name}}").FillAsync("Olena");
        await page.GetByRole(AriaRole.Button, new() { Name = "Run", Exact = true }).ClickAsync();
        await Expect(page.GetByTestId("run-output")).ToContainTextAsync("Say hello to Olena politely.");

        // An eval case that echo passes, then the eval.
        await page.GotoAsync(promptUrl.Replace("/Prompts/Details/", "/Evals/Case/"));
        await page.GetByLabel("Name", new() { Exact = true }).FillAsync("names the person");
        await page.GetByLabel("{{name}}").FillAsync("Olena");
        await page.GetByLabel("Must contain, one per line").FillAsync("Olena");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save case" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Run 1 case" }).ClickAsync();
        await Expect(page.GetByTestId("pass-rate")).ToContainTextAsync("1 / 1", new() { Timeout = 15_000 });
        await Expect(page.Locator(".pm-signal-lg .is-pass")).ToHaveCountAsync(1);

        // A second version, compared with the first, then promoted.
        await page.GotoAsync(promptUrl);
        await page.GetByRole(AriaRole.Link, new() { Name = "New version" }).ClickAsync();
        await page.GetByLabel("New text").FillAsync("Say hello to {{name}} warmly.\nKeep it short.");
        await page.GetByLabel("What changed").FillAsync("Warmer");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save as draft" }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Compare versions" }).ClickAsync();
        await Expect(page.Locator("mark", new() { HasText = "warmly" })).ToBeVisibleAsync();

        // Promotion needs a passing eval of the new version first.
        await page.GotoAsync(promptUrl.Replace("/Prompts/Details/", "/Evals/Index/"));
        await page.GetByRole(AriaRole.Button, new() { Name = "Run 1 case" }).ClickAsync();
        await Expect(page.GetByTestId("pass-rate")).ToContainTextAsync("1 / 1", new() { Timeout = 15_000 });

        await page.GotoAsync(promptUrl);
        page.Dialog += (_, dialog) => dialog.AcceptAsync();
        await page.Locator("#v2").GetByRole(AriaRole.Button, new() { Name = "Promote" }).ClickAsync();
        await Expect(page.GetByTestId("production-version")).ToHaveTextAsync("v2");

        Assert.Empty(errors);
    }

    [Fact]
    public async Task Search_tags_and_collections_find_the_prompt()
    {
        var (context, page, errors) = await app.NewPageAsync();
        await using var _ = context;
        await SignUpAsync(page, "finder@e2e.test", "Finder");
        await CreatePromptAsync(page, "Findable greeter");

        await page.GotoAsync("/");
        await page.GetByRole(AriaRole.Navigation, new() { Name = "Collections" }).GetByRole(AriaRole.Link, new() { NameRegex = new Regex("^#greeting") }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "#greeting" })).ToBeVisibleAsync();

        await page.GotoAsync("/");
        await page.GetByLabel("Search").FillAsync("politely hello");
        await page.GetByRole(AriaRole.Button, new() { Name = "Filter" }).ClickAsync();
        await Expect(page.Locator(".pm-row", new() { HasText = "Findable greeter" }).Locator("mark").First).ToBeVisibleAsync();

        Assert.Empty(errors);
    }

    [Fact]
    public async Task Theme_choice_survives_a_reload_and_the_menu_opens_on_a_phone()
    {
        var (context, page, errors) = await app.NewPageAsync(new() { Width = 390, Height = 844 });
        await using var _ = context;
        await page.GotoAsync("/");

        // On a phone the navigation is behind the menu button.
        var library = page.GetByRole(AriaRole.Navigation, new() { Name = "Main" }).GetByRole(AriaRole.Link, new() { Name = "Library" });
        await Expect(library).ToBeHiddenAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Menu" }).ClickAsync();
        await Expect(library).ToBeVisibleAsync();

        await page.GetByRole(AriaRole.Button, new() { Name = "Dark" }).ClickAsync();
        await page.ReloadAsync();
        await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
        var background = await page.EvaluateAsync<string>("getComputedStyle(document.body).backgroundColor");
        Assert.Equal("rgb(12, 17, 32)", background);

        Assert.Empty(errors);
    }

    [Fact]
    public async Task Copy_button_and_confirm_dialogs_work_without_inline_scripts()
    {
        var (context, page, errors) = await app.NewPageAsync();
        await using var _ = context;
        await context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"]);
        await SignUpAsync(page, "copier@e2e.test", "Copier");
        await CreatePromptAsync(page, "Copyable");

        await page.GetByRole(AriaRole.Button, new() { Name = "Copy text" }).ClickAsync();
        var copied = await page.EvaluateAsync<string>("navigator.clipboard.readText()");
        Assert.Equal("Say hello to {{name}} politely.\nKeep it short.", copied.Replace("\r\n", "\n").Replace("​", "").Trim());

        // Dismissing the confirm keeps the prompt.
        page.Dialog += (_, dialog) => dialog.DismissAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Archive" }).ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Copyable", Level = 1 })).ToBeVisibleAsync();

        Assert.Empty(errors);
    }
}
