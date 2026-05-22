using FancilPhones.Components;
using FancilPhones.Data;
using FancilPhones.Services;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

var connStr = builder.Configuration.GetConnectionString("Default")
              ?? "Data Source=fancilphones.db";

builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(connStr));

builder.Services.AddScoped<PhoneSyncService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    using var db = dbf.CreateDbContext();
    db.Database.EnsureCreated();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();

app.MapGet("/api/phonebook.csv", async (IDbContextFactory<AppDbContext> dbf) =>
{
    await using var db = await dbf.CreateDbContextAsync();
    var contacts = await db.Contacts.OrderBy(c => c.DisplayName).ToListAsync();
    var bytes = PhonebookCsv.Build(contacts);
    return Results.File(bytes, "text/csv", "phonebook.csv");
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
