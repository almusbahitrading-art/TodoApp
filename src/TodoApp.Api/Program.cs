using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Connection string 'Default' is missing");

builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/todos", async (AppDbContext db) =>
    await db.Todos.OrderBy(t => t.Id).ToListAsync());

app.MapPost("/api/todos", async (CreateTodo input, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(input.Title))
        return Results.BadRequest("Title is required");

    var item = new TodoItem { Title = input.Title.Trim() };
    db.Todos.Add(item);
    await db.SaveChangesAsync();
    return Results.Created($"/api/todos/{item.Id}", item);
});

app.MapPut("/api/todos/{id}/toggle", async (int id, AppDbContext db) =>
{
    var item = await db.Todos.FindAsync(id);
    if (item is null) return Results.NotFound();

    item.IsDone = !item.IsDone;
    await db.SaveChangesAsync();
    return Results.Ok(item);
});

app.MapDelete("/api/todos/{id}", async (int id, AppDbContext db) =>
{
    var item = await db.Todos.FindAsync(id);
    if (item is null) return Results.NotFound();

    db.Todos.Remove(item);
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.Run();

record CreateTodo(string Title);

class TodoItem
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public bool IsDone { get; set; }
}

class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<TodoItem> Todos => Set<TodoItem>();
}