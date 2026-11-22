using Npgsql;

namespace PromptManager.Data;

public static class ConnectionStrings
{
    /// <summary>
    /// Accepts either an Npgsql "Host=...;Database=..." string or a URL such as
    /// postgresql://user:password@host:5432/database, which is what hosting
    /// platforms like Render hand out, and returns an Npgsql string.
    /// GSS/Kerberos is turned off unless asked for: it is never used here and
    /// probing for it logs a libgssapi error in the slim runtime image.
    /// </summary>
    public static string ToNpgsql(string connectionString)
    {
        var builder = connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            || connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)
            ? FromUrl(new Uri(connectionString))
            : new NpgsqlConnectionStringBuilder(connectionString);

        if (!connectionString.Contains("GSS", StringComparison.OrdinalIgnoreCase))
            builder.GssEncryptionMode = GssEncryptionMode.Disable;

        return builder.ConnectionString;
    }

    private static NpgsqlConnectionStringBuilder FromUrl(Uri url)
    {
        var userInfo = url.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = url.Host,
            Port = url.IsDefaultPort || url.Port <= 0 ? 5432 : url.Port,
            Database = Uri.UnescapeDataString(url.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null
        };

        // Carry over query options such as ?sslmode=require.
        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2)
                builder[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
        }
        return builder;
    }
}
