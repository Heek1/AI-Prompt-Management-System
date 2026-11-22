using Npgsql;
using PromptManager.Data;

namespace PromptManager.Tests.Infrastructure;

public class ConnectionStringsTests
{
    private static NpgsqlConnectionStringBuilder Parse(string s) => new(ConnectionStrings.ToNpgsql(s));

    [Theory]
    [InlineData("postgresql://pm_user:s3cr%40t@dpg-abc.frankfurt-postgres.render.com:5433/promptmanager")]
    [InlineData("postgres://pm_user:s3cr%40t@dpg-abc.frankfurt-postgres.render.com:5433/promptmanager")]
    public void Converts_platform_urls(string url)
    {
        var b = Parse(url);

        Assert.Equal("dpg-abc.frankfurt-postgres.render.com", b.Host);
        Assert.Equal(5433, b.Port);
        Assert.Equal("promptmanager", b.Database);
        Assert.Equal("pm_user", b.Username);
        Assert.Equal("s3cr@t", b.Password); // percent-encoding is decoded
        Assert.Equal(GssEncryptionMode.Disable, b.GssEncryptionMode);
    }

    [Fact]
    public void Url_without_port_uses_5432_and_keeps_query_options()
    {
        var b = Parse("postgresql://u:p@dpg-internal/db?sslmode=require");

        Assert.Equal(5432, b.Port);
        Assert.Equal(SslMode.Require, b.SslMode);
    }

    [Fact]
    public void Keyword_strings_pass_through()
    {
        var b = Parse("Host=localhost;Port=5433;Database=promptmanager;Username=u;Password=p");

        Assert.Equal(("localhost", 5433, "promptmanager"), (b.Host, b.Port, b.Database));
        Assert.Equal(GssEncryptionMode.Disable, b.GssEncryptionMode);
    }

    [Fact]
    public void Explicit_gss_setting_is_respected()
    {
        Assert.Equal(GssEncryptionMode.Prefer, Parse("Host=h;Database=d;GSS Encryption Mode=Prefer").GssEncryptionMode);
    }
}
