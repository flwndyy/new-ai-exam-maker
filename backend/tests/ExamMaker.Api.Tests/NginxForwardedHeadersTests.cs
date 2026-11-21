using Xunit;

namespace ExamMaker.Api.Tests;

public sealed class NginxForwardedHeadersTests
{
    [Fact]
    public void Normalizes_cloudfront_viewer_address_before_forwarding_to_kestrel()
    {
        var config = File.ReadAllText(FindRepositoryFile("infra", "nginx", "exammaker.conf.example"));

        Assert.Contains("map $http_cloudfront_viewer_address $exammaker_client_ip", config);
        Assert.Contains("proxy_set_header X-Forwarded-For $exammaker_client_ip;", config);
        Assert.DoesNotContain("proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;", config);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate the repository file.");
    }
}
