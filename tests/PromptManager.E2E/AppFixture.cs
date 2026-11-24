using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using PromptManager.Tests.Infrastructure;

namespace PromptManager.E2E;

/// <summary>
/// The real app on a free local port (Kestrel, not the in-memory test
/// server, so a browser can reach it), with its own PostgreSQL database,
/// and one headless Chromium for the whole test class.
/// </summary>
public sealed class AppFixture : IAsyncLifetime
{
    private readonly PromptManagerFactory _factory = new();
    private IPlaywright? _playwright;

    public IBrowser Browser { get; private set; } = null!;
    public string BaseUrl { get; private set; } = "";

    public async Task InitializeAsync()
    {
        _factory.UseKestrel(0);
        _factory.StartServer();
        BaseUrl = _factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.First().Replace("[::]", "localhost").Replace("127.0.0.1", "localhost").TrimEnd('/');

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async Task DisposeAsync()
    {
        await Browser.DisposeAsync();
        _playwright?.Dispose();
        await _factory.DisposeAsync();
    }

    /// <summary>
    /// A fresh browser context (own cookies and storage) whose pages fail the
    /// test on any console error, which includes CSP violations.
    /// </summary>
    public async Task<(IBrowserContext Context, IPage Page, List<string> Errors)> NewPageAsync(ViewportSize? viewport = null)
    {
        var context = await Browser.NewContextAsync(new() { BaseURL = BaseUrl, ViewportSize = viewport ?? new() { Width = 1366, Height = 900 } });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.Console += (_, msg) => { if (msg.Type == "error") errors.Add(msg.Text); };
        page.PageError += (_, error) => errors.Add(error);
        return (context, page, errors);
    }
}
