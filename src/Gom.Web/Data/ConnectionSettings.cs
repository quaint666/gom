using System.Data.Common;
using Npgsql;

namespace Gom.Web.Data;

/// <summary>What Gom changes in the configured connection string before using it.</summary>
public static class ConnectionSettings
{
    /// <summary>
    /// The most connections Gom keeps open when the connection string names none. Npgsql's own default (100) is also PostgreSQL's
    /// default most connections, so requests piling up behind a lock (a long trim, 2026-11-28) took every one and kept them for
    /// minutes, and nothing else could connect. A family's Gom needs far fewer; with all taken, a request waits for one.
    /// </summary>
    public const int DefaultMaxPoolSize = 30;

    private static readonly string[] PoolSizeKeys = ["Maximum Pool Size", "Max Pool Size", "MaxPoolSize"];

    /// <summary>
    /// The connection string to use: GSS (Kerberos) encryption off whatever the setting says (Npgsql tries it first by default
    /// through the image's libgssapi_krb5, which crashed the process now and then: CI's crash report, 2026-11-26), and the pool
    /// capped at <see cref="DefaultMaxPoolSize"/> unless the configuration chose a size itself.
    /// </summary>
    public static string For(string configured)
    {
        var given = new DbConnectionStringBuilder { ConnectionString = configured };
        var builder = new NpgsqlConnectionStringBuilder(configured) { GssEncryptionMode = GssEncryptionMode.Disable };
        if (!PoolSizeKeys.Any(given.ContainsKey)) builder.MaxPoolSize = DefaultMaxPoolSize;
        return builder.ConnectionString;
    }
}
