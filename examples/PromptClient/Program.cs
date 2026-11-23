// Fetches a prompt from Prompt Manager instead of hard-coding it.
//
//   PROMPT_MANAGER_URL=https://prompt-manager-bt5r.onrender.com \
//   PROMPT_MANAGER_KEY=pm_... \
//   dotnet run --project examples/PromptClient -- support-reply-en customer=Olena "topic=a refund"
//
// The key comes from the environment, never from the command line or code.
using PromptClient;

var baseUrl = Environment.GetEnvironmentVariable("PROMPT_MANAGER_URL") ?? "http://localhost:5139";
var key = Environment.GetEnvironmentVariable("PROMPT_MANAGER_KEY");
if (string.IsNullOrWhiteSpace(key) || args.Length == 0)
{
    Console.Error.WriteLine("Usage: set PROMPT_MANAGER_URL and PROMPT_MANAGER_KEY, then: PromptClient <slug> [name=value ...]");
    return 2;
}

var slug = args[0];
var variables = args.Skip(1)
    .Select(a => a.Split('=', 2))
    .Where(p => p.Length == 2)
    .ToDictionary(p => p[0], p => p[1]);

using var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
var client = new PromptManagerClient(http, key);

try
{
    var prompt = await client.GetAsync(slug);
    Console.WriteLine($"{prompt.Title} - v{prompt.Version} ({prompt.Status})");
    Console.WriteLine($"Variables: {(prompt.Variables.Count == 0 ? "none" : string.Join(", ", prompt.Variables))}");

    // A second request is answered with 304 and served from the client's cache.
    await client.GetAsync(slug);
    Console.WriteLine($"Revalidated with 304: {client.NotModifiedCount == 1}");

    if (prompt.Variables.Count > 0)
    {
        var rendered = await client.RenderAsync(slug, variables);
        Console.WriteLine();
        Console.WriteLine(rendered.Text);
    }
    else
    {
        Console.WriteLine();
        Console.WriteLine(prompt.Text);
    }
    return 0;
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
