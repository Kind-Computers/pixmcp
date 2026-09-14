using System.Text.Json;
using Microsoft.Data.Sqlite;
using PixMcp.Pix;

namespace PixMcp.Tests;

/// <summary>
/// PixStorage-shaped synthetic timing capture: every table and index of the recorded PIX 2606.18 schema with its real DDL,
/// the eight pixstorage.dll virtual tables as plain stand-ins without hidden columns, and the rows of
/// <c>Fixtures/timing-fixture.json</c>. Built by <c>scripts/make_timing_fixture.py</c>; each instance works on a private copy.
/// </summary>
internal sealed class TimingFixture : IDisposable
{
    internal static string SourcePath { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "timing-synthetic.sqlite");

    private static readonly Lazy<JsonDocument> SeedDocument = new(() =>
        JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "timing-fixture.json"))));

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("pixmcp-timing-fixture-");

    internal TimingFixture()
    {
        Path = System.IO.Path.Combine(_directory.FullName, "pixstorage.sqlite");
        File.Copy(SourcePath, Path);
    }

    internal string Path { get; }

    /// <summary>Seed rows, so assertions can derive expected values instead of repeating them.</summary>
    internal static JsonElement Seed => SeedDocument.Value.RootElement;

    /// <summary>A writable connection for test mutations; the real DDL declares foreign keys, which tests may break on purpose.</summary>
    internal SqliteConnection Connect()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false, ForeignKeys = false }.ToString());
        connection.Open();
        return connection;
    }

    internal void Execute(string sql)
    {
        using SqliteConnection connection = Connect();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal TimingDatabase Open(Action<SqliteConnection>? configure = null) => new(Path, null, configureForTests: configure ?? (_ => { }));

    public void Dispose() => _directory.Delete(recursive: true);
}
