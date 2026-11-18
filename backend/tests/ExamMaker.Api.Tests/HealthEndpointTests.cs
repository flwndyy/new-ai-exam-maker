using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ExamMaker.Api.Tests;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task HealthReturnsOk()
    {
        using var app = new WebApplicationFactory<Program>();
        using var client = app.CreateClient();

        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}