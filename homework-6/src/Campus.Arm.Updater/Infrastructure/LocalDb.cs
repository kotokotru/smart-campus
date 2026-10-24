using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Campus.Arm.Updater.Infrastructure;

/// <summary>Работа с локальной БД АРМ (SQLite). Пул соединений выключен, чтобы файл БД
/// не оставался заблокированным во время резервного копирования и отката.</summary>
public static partial class LocalDb
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();

    public static SqliteConnection Open(string path, bool readOnly = false)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Локальная БД АРМ не найдена", path);

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();

        var connection = new SqliteConnection(cs);
        connection.Open();
        return connection;
    }

    public static object? Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public static int GetSchemaVersion(SqliteConnection c) =>
        Convert.ToInt32(Scalar(c, "SELECT version FROM schema_info LIMIT 1"));

    public static bool ColumnExists(SqliteConnection c, string table, string column)
    {
        EnsureIdentifier(table);
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column";
        cmd.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>Количество строк в ключевых таблицах — «снимок» для проверки, что данные не потерялись.</summary>
    public static Dictionary<string, long> CountRows(SqliteConnection c, params string[] tables)
    {
        var result = new Dictionary<string, long>();
        foreach (var table in tables)
        {
            EnsureIdentifier(table);
            result[table] = Convert.ToInt64(Scalar(c, $"SELECT COUNT(*) FROM {table}"));
        }
        return result;
    }

    public static readonly string[] KeyTables = ["access_events", "operators", "sync_queue"];

    private static void EnsureIdentifier(string name)
    {
        if (!Identifier().IsMatch(name))
            throw new ArgumentException($"Недопустимое имя таблицы: {name}");
    }
}
