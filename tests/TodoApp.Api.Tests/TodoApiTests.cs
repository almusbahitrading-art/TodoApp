using System.Net;
using System.Net.Http.Json;

namespace TodoApp.Api.Tests;

public class TodoApiTests
{
    private record TodoDto(int Id, string Title, bool IsDone);

    [Fact]
    public async Task GetTodos_WhenEmpty_ReturnsEmptyList()
    {
        await using var factory = new TodoApiFactory();
        var client = factory.CreateClient();

        var todos = await client.GetFromJsonAsync<List<TodoDto>>("/api/todos");

        Assert.NotNull(todos);
        Assert.Empty(todos);
    }

    [Fact]
    public async Task PostTodo_WithTitle_CreatesTodo()
    {
        await using var factory = new TodoApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/todos", new { title = "Buy milk" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<TodoDto>();
        Assert.NotNull(created);
        Assert.Equal("Buy milk", created.Title);
        Assert.False(created.IsDone);
    }

    [Fact]
    public async Task PostTodo_WithBlankTitle_ReturnsBadRequest()
    {
        await using var factory = new TodoApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/todos", new { title = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ToggleTodo_FlipsIsDone()
    {
        await using var factory = new TodoApiFactory();
        var client = factory.CreateClient();
        var created = await (await client.PostAsJsonAsync("/api/todos", new { title = "Task" }))
            .Content.ReadFromJsonAsync<TodoDto>();

        var response = await client.PutAsync($"/api/todos/{created!.Id}/toggle", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var toggled = await response.Content.ReadFromJsonAsync<TodoDto>();
        Assert.True(toggled!.IsDone);
    }

    [Fact]
    public async Task DeleteTodo_RemovesIt()
    {
        await using var factory = new TodoApiFactory();
        var client = factory.CreateClient();
        var created = await (await client.PostAsJsonAsync("/api/todos", new { title = "Task" }))
            .Content.ReadFromJsonAsync<TodoDto>();

        var response = await client.DeleteAsync($"/api/todos/{created!.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var todos = await client.GetFromJsonAsync<List<TodoDto>>("/api/todos");
        Assert.Empty(todos!);
    }

    [Fact]
    public async Task DeleteTodo_Unknown_ReturnsNotFound()
    {
        await using var factory = new TodoApiFactory();
        var client = factory.CreateClient();

        var response = await client.DeleteAsync("/api/todos/999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}