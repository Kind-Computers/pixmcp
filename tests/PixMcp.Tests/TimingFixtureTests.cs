using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace PixMcp.Tests;

public sealed class TimingFixtureTests
{
    private static List<string> Strings(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
            values.Add(reader.GetString(0));
        return values;
    }

    private static long Count(SqliteConnection connection, string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void SyntheticFixtureHasEveryRecordedTableStandInsWithoutHiddenColumnsAndTheSeedRows()
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pixstorage-schema-2606.18.json")));
        using var fixture = new TimingFixture();
        using SqliteConnection connection = fixture.Connect();
        List<string> tables = Strings(connection, "SELECT name FROM sqlite_master WHERE type = 'table'");
        JsonElement recorded = schema.RootElement.GetProperty("tables");
        JsonElement modules = schema.RootElement.GetProperty("virtualTables");
        Assert.Equal(recorded.EnumerateObject().Count() + modules.EnumerateObject().Count(), tables.Count);
        foreach (JsonProperty table in recorded.EnumerateObject())
            Assert.Contains(table.Name, tables);
        foreach (JsonProperty module in modules.EnumerateObject())
        {
            string[] visible = module.Value.EnumerateArray().Where(c => c.GetProperty("hidden").GetInt32() == 0).Select(c => c.GetProperty("name").GetString()!).ToArray();
            Assert.Equal(visible, Strings(connection, $"SELECT name FROM pragma_table_xinfo('{module.Name}')"));
        }
        Assert.DoesNotContain("CpuExecutionRowId", Strings(connection, "SELECT name FROM pragma_table_xinfo('PixCpuExecutionTimes')"));
        foreach (JsonProperty table in TimingFixture.Seed.GetProperty("tables").EnumerateObject())
            Assert.Equal(table.Value.GetProperty("rows").GetArrayLength(), Count(connection, table.Name));
    }

    [Fact]
    public void EachFixtureWorksOnAPrivateCopy()
    {
        using var changed = new TimingFixture();
        changed.Execute("DELETE FROM Strings");
        using var fresh = new TimingFixture();
        using SqliteConnection a = changed.Connect();
        using SqliteConnection b = fresh.Connect();
        Assert.Equal(0, Count(a, "Strings"));
        Assert.Equal(TimingFixture.Seed.GetProperty("tables").GetProperty("Strings").GetProperty("rows").GetArrayLength(), Count(b, "Strings"));
    }
}
