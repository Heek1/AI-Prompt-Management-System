using Microsoft.EntityFrameworkCore;
using AI_Prompt_Management_System.Data;
using AI_Prompt_Management_System.Middleware;
using AI_Prompt_Management_System.Services;

var builder = WebApplication.CreateBuilder(args);

// Services
builder.Services.AddControllersWithViews();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<IPromptService, PromptService>();

var app = builder.Build();

// Auto-migrate on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

// Custom Middleware
app.UseRequestLogging();
app.UseAiUsageLogging();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Prompts}/{action=Index}/{id?}");

app.Run();
