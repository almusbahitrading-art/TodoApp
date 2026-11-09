var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var todos = new List<TodoItem>();
var nextId = 1;

app.MapGet("/api/todos", () => todos);

app.MapPost("/api/todos", (CreateTodo input) =>
{
    if (string.IsNullOrWhiteSpace(input.Title))
        return Results.BadRequest("Title is required");

    var item = new TodoItem(nextId++, input.Title.Trim(), false);
    todos.Add(item);
    return Results.Created($"/api/todos/{item.Id}", item);
});

app.MapPut("/api/todos/{id}/toggle", (int id) =>
{
    var index = todos.FindIndex(t => t.Id == id);
    if (index < 0) return Results.NotFound();

    todos[index] = todos[index] with { IsDone = !todos[index].IsDone };
    return Results.Ok(todos[index]);
});

app.MapDelete("/api/todos/{id}", (int id) =>
{
    var removed = todos.RemoveAll(t => t.Id == id);
    return removed == 0 ? Results.NotFound() : Results.NoContent();
});

app.Run();

record TodoItem(int Id, string Title, bool IsDone);
record CreateTodo(string Title);