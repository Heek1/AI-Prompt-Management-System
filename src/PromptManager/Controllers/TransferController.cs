using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PromptManager.Services.Transfer;

namespace PromptManager.Controllers;

/// <summary>Export and import of prompts as YAML or JSON (C5).</summary>
public class TransferController : Controller
{
    private readonly PromptTransferService _transfer;

    public TransferController(PromptTransferService transfer)
    {
        _transfer = transfer;
    }

    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    // Reading is open to everyone, like the library, so export is too.

    // GET /Prompts/Export?format=yaml|json
    [HttpGet("/Prompts/Export")]
    public async Task<IActionResult> ExportAll(string format = "yaml")
    {
        var kind = ParseFormat(format);
        var file = new PromptFile { ExportedAt = DateTime.UtcNow, Prompts = await _transfer.ExportAsync() };
        foreach (var p in file.Prompts) p.Format = null; // the file says it once
        return Download(PromptSerializer.Write(file, kind), $"prompts-{DateTime.UtcNow:yyyy-MM-dd}", kind);
    }

    // GET /Prompts/Export/5?format=yaml|json
    [HttpGet("/Prompts/Export/{id:int}")]
    public async Task<IActionResult> ExportOne(int id, string format = "yaml")
    {
        var prompt = (await _transfer.ExportAsync(id)).SingleOrDefault();
        if (prompt is null) return NotFound();
        var kind = ParseFormat(format);
        return Download(PromptSerializer.Write(prompt, kind), prompt.Slug!, kind);
    }

    // GET /Prompts/ExportFolder: a zip with prompts/{slug}.yaml, for a git repository.
    [HttpGet("/Prompts/ExportFolder")]
    public async Task<IActionResult> ExportFolder() =>
        File(PromptTransferService.Zip(await _transfer.ExportAsync()), "application/zip", $"prompts-{DateTime.UtcNow:yyyy-MM-dd}.zip");

    // GET /Prompts/Import
    [Authorize]
    [HttpGet("/Prompts/Import")]
    public IActionResult Import() => View(new ImportViewModel());

    // POST /Prompts/Import
    [Authorize]
    [HttpPost("/Prompts/Import")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(PromptTransferService.MaxUploadBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = PromptTransferService.MaxUploadBytes + 64 * 1024)]
    public async Task<IActionResult> Import(List<IFormFile> files)
    {
        var model = new ImportViewModel();
        files = files.Where(f => f.Length > 0).ToList();
        if (files.Count == 0)
        {
            model.Errors.Add("Choose at least one file.");
            return View(model);
        }
        if (files.Sum(f => f.Length) > PromptTransferService.MaxUploadBytes)
        {
            model.Errors.Add($"The files add up to more than {PromptTransferService.MaxUploadBytes / 1024 / 1024} MB.");
            return View(model);
        }

        var streams = files.Select(f => new ImportUpload(f.FileName, f.OpenReadStream())).ToList();
        try
        {
            var (prompts, errors) = PromptTransferService.Read(streams);
            model.Errors.AddRange(errors);
            if (prompts.Count == 0 && errors.Count == 0)
                model.Errors.Add("No prompts found in the files.");
            model.Lines = await _transfer.ImportAsync(prompts);
        }
        finally
        {
            foreach (var s in streams) await s.Content.DisposeAsync();
        }
        return View(model);
    }

    private static TransferFormat ParseFormat(string format) =>
        format.Equals("json", StringComparison.OrdinalIgnoreCase) ? TransferFormat.Json : TransferFormat.Yaml;

    private FileContentResult Download(string text, string name, TransferFormat format) =>
        format == TransferFormat.Json
            ? File(Utf8.GetBytes(text), "application/json", name + ".json")
            : File(Utf8.GetBytes(text), "application/yaml", name + ".yaml");
}

public class ImportViewModel
{
    public List<string> Errors { get; set; } = new();
    public List<ImportLine> Lines { get; set; } = new();
}
