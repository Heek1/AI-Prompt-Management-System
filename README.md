# Prompt Manager

[![CI](https://github.com/Heek1/AI-Prompt-Management-System/actions/workflows/ci.yml/badge.svg)](https://github.com/Heek1/AI-Prompt-Management-System/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A small web app for keeping a team's AI prompts in one place: write a prompt,
improve it in versions, rate it, record when it was used, and find it again by
search and filters.

Built with ASP.NET Core 10 MVC, EF Core and SQLite.

| Library | Prompt details |
|---|---|
| ![Prompt library](docs/images/library.png) | ![Prompt details](docs/images/details.png) |

## What it does

- **Library** with search over title, description and prompt text,
  filters by category and AI tool, and sorting by rating, date, title or version.
- **Versions.** Each edit creates a new numbered version with a change note.
  The full history stays visible.
- **Ratings** from 1 to 5 with an optional comment, one per user name per prompt.
- **Archive.** Archived prompts can no longer be edited or used.
- **Usage history** (optional). "Use prompt" events can be written to
  Azure Table Storage and are listed on the History page.
- **`/health`** returns a JSON status with prompt and usage counts.

## What it does not do (yet)

Known limitations:

- **It does not run prompts against a model.** The app stores and organises
  prompt text; it does not call any AI API.
- **There is no authentication.** Author and user names are free text from the
  form, so "one rating per user" is per typed name, and anyone can archive any
  prompt.
- Categories and AI tools are fixed lists in
  [`Constants.cs`](src/PromptManager/Infrastructure/Constants.cs).
- No editing of title or description, no unarchive or delete, no pagination.
- Usage history is stored only if an Azure Storage connection string is set;
  the Azure path is not covered by automated tests.

## Run it

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project src/PromptManager
```

Open http://localhost:5139. On first start the SQLite database
(`src/PromptManager/promptmanager.db`) is created and migrated, and in
Development three sample prompts are added.

### Optional: usage history in Azure Table Storage

Set the connection string with user secrets (never in `appsettings.json`):

```bash
dotnet user-secrets --project src/PromptManager set "Azure:StorageConnectionString" "<connection string>"
```

For local development, [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite)
works with `UseDevelopmentStorage=true`.

## Tests

```bash
dotnet test
```

57 tests in [`tests/PromptManager.Tests`](tests/PromptManager.Tests):

- **Service tests** run `PromptService` against an in-memory SQLite database
  (real SQLite, so unique indexes and transactions behave as in the app).
- **Concurrency tests** inject a competing write between the service's read and
  its save, to check version numbering, duplicate ratings and archive-vs-edit.
- **Validation tests** for every form DTO.
- **Integration tests** start the app with `WebApplicationFactory` and post real
  forms with antiforgery tokens.

CI ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) builds with
`-warnaserror` and runs the tests on every pull request.

## How it is built

```
src/PromptManager/
  Controllers/      Prompts, History, Health, Home (redirect + error page)
  Services/         PromptService: prompts, versions, ratings, archive, usage
  Data/             AppDbContext, DevDataSeeder
  Models/           EF entities and the Azure Table entity
  DTOs/             Form and view DTOs with validation attributes
  Infrastructure/   Category and tool lists, field length limits
  Migrations/       EF Core migrations (applied automatically at startup)
  Views/            Razor views, Bootstrap 5
tests/PromptManager.Tests/
```

### Design decisions

- **SQLite** keeps setup to zero: no database server is needed to run or test
  the app.
- **Concurrency is handled by the database, not by checks in code.** A unique
  index on `(PromptId, VersionNumber)` and an optimistic concurrency token on
  `Prompt` make simultaneous edits produce consecutive versions (the service
  retries on conflict). A unique index on `(PromptId, UserName)` is the only
  guard against double ratings, so there is no check-then-insert window.
- **One source of truth for field limits.**
  [`FieldLimits`](src/PromptManager/Infrastructure/FieldLimits.cs) is used by
  both the form validation and the EF model.
- **Warnings are errors**, including NuGet vulnerability audits of transitive
  packages ([`Directory.Build.props`](Directory.Build.props)).

## Roadmap

Next steps, roughly in order: running a prompt version against a model and
recording the result, template variables, a diff between versions, evaluation
sets per prompt, sign-in, Docker and a hosted demo.

## License

[MIT](LICENSE)
