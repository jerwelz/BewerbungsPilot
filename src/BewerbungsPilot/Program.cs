using System.Text.Json.Serialization;
using BewerbungsPilot.Data;
using BewerbungsPilot.Endpoints;
using BewerbungsPilot.Logic;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- Database: one SQLite file inside the (git-ignored) storage directory ---
var dataDirectory = Path.Combine(builder.Environment.ContentRootPath,
    builder.Configuration["Storage:DataDirectory"] ?? "storage");
Directory.CreateDirectory(dataDirectory);
var databasePath = Path.Combine(dataDirectory, "bewerbungspilot.db");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={databasePath}"));

// --- Services (business logic) ---
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ApplicationService>();

// Send enums as text ("Beworben") instead of numbers in JSON.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

// Create or upgrade the database schema on startup (applies pending migrations).
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();
}

// --- Frontend: static files from wwwroot ---
app.MapGet("/", () => Results.Redirect("/bewerbungen.html"));
app.UseStaticFiles();

// --- API ---
app.MapApplicationEndpoints();

app.Run();
