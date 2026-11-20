using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ExamMaker.Api.Tests;

public sealed class ForwardedHeadersTests
{
    [Fact]
    public async Task Uses_the_normalized_client_ip_from_a_single_forwarded_hop()
    {
        using var app = CreateApp();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.10");

        using var response = await client.GetAsync("/_forwarded-tests/remote-ip");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("203.0.113.10", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Does_not_treat_the_rightmost_entry_of_a_two_proxy_chain_as_the_client()
    {
        using var app = CreateApp();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.10, 198.51.100.20");

        using var response = await client.GetAsync("/_forwarded-tests/remote-ip");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("198.51.100.20", await response.Content.ReadAsStringAsync());
    }

    private static WebApplicationFactory<Program> CreateApp() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddControllers().AddApplicationPart(typeof(ForwardedHeadersProbeController).Assembly)));
}

[ApiController]
[Route("_forwarded-tests")]
public sealed class ForwardedHeadersProbeController : ControllerBase
{
    [HttpGet("remote-ip")]
    public ActionResult<string> RemoteIp() =>
        Ok(HttpContext.Connection.RemoteIpAddress?.ToString());
}
