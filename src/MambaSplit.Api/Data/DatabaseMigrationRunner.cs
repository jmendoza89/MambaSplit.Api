using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MambaSplit.Api.Data;

public static class DatabaseMigrationRunner
{
    private const string HistoryTableName = "schema_history";

    public static void Run(string connectionString, ILogger logger)
    {
        var scriptsPath = Path.Combine(AppContext.BaseDirectory, "Database", "Migrations");
        if (!Directory.Exists(scriptsPath))
        {
            throw new InvalidOperationException($"Migration folder not found: {scriptsPath}");
        }

        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        EnsureHistoryTable(connection);
        var appliedMigrations = ReadAppliedMigrations(connection);
        var scripts = GetScripts(scriptsPath);

        foreach (var script in scripts)
        {
            if (appliedMigrations.TryGetValue(script.Version, out var appliedChecksum))
            {
                if (!string.Equals(appliedChecksum, script.Checksum, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Migration {script.Version} was already applied with a different checksum.");
                }

                continue;
            }

            using var transaction = connection.BeginTransaction();
            try
            {
                using (var apply = new NpgsqlCommand(script.Sql, connection, transaction))
                {
                    apply.ExecuteNonQuery();
                }

                using (var insert = new NpgsqlCommand(
                           $"""
                            INSERT INTO public.{HistoryTableName} (version, script, checksum)
                            VALUES (@version, @script, @checksum);
                            """,
                           connection,
                           transaction))
                {
                    insert.Parameters.AddWithValue("version", script.Version);
                    insert.Parameters.AddWithValue("script", script.FileName);
                    insert.Parameters.AddWithValue("checksum", script.Checksum);
                    insert.ExecuteNonQuery();
                }

                transaction.Commit();
                logger.LogInformation("Applied migration {MigrationVersion} ({MigrationFile})", script.Version, script.FileName);
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                throw new InvalidOperationException($"Failed to apply migration {script.FileName}.", ex);
            }
        }

        logger.LogInformation("Database migrations applied successfully.");
    }

    private static void EnsureHistoryTable(NpgsqlConnection connection)
    {
        const string sql = $"""
            CREATE TABLE IF NOT EXISTS public.{HistoryTableName} (
                installed_rank bigserial PRIMARY KEY,
                version character varying(64) NOT NULL UNIQUE,
                script character varying(255) NOT NULL,
                checksum character varying(64) NOT NULL,
                installed_on timestamp with time zone NOT NULL DEFAULT NOW()
            );
            """;

        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    private static Dictionary<string, string> ReadAppliedMigrations(NpgsqlConnection connection)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var command = new NpgsqlCommand(
            $"SELECT version, checksum FROM public.{HistoryTableName};",
            connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    private static IReadOnlyList<MigrationScript> GetScripts(string scriptsPath)
    {
        var files = Directory.GetFiles(scriptsPath, "V*__*.sql")
            .Select(path => new FileInfo(path))
            .OrderBy(file => ParseVersion(file.Name), MigrationVersionComparer.Instance)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return files.Select(file =>
        {
            var version = ParseVersionToken(file.Name);
            var sql = File.ReadAllText(file.FullName);
            var checksum = ComputeSha256(sql);
            return new MigrationScript(version, file.Name, sql, checksum);
        }).ToList();
    }

    private static string ParseVersionToken(string fileName)
    {
        var separatorIndex = fileName.IndexOf("__", StringComparison.Ordinal);
        if (separatorIndex <= 1 || !fileName.StartsWith('V'))
        {
            throw new InvalidOperationException($"Invalid migration filename '{fileName}'. Expected format Vx__description.sql.");
        }

        return fileName[1..separatorIndex];
    }

    private static MigrationVersion ParseVersion(string fileName)
    {
        var token = ParseVersionToken(fileName);
        var parts = token.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => int.TryParse(part, out var value) ? value : -1)
            .ToArray();

        if (parts.Length == 0 || parts.Any(static part => part < 0))
        {
            throw new InvalidOperationException($"Invalid migration version '{token}' in '{fileName}'.");
        }

        return new MigrationVersion(parts);
    }

    private static string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes);
    }

    private sealed record MigrationScript(string Version, string FileName, string Sql, string Checksum);

    private sealed record MigrationVersion(int[] Parts);

    private sealed class MigrationVersionComparer : IComparer<MigrationVersion>
    {
        public static readonly MigrationVersionComparer Instance = new();

        public int Compare(MigrationVersion? x, MigrationVersion? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var maxLength = Math.Max(x.Parts.Length, y.Parts.Length);
            for (var i = 0; i < maxLength; i++)
            {
                var xPart = i < x.Parts.Length ? x.Parts[i] : 0;
                var yPart = i < y.Parts.Length ? y.Parts[i] : 0;
                var partComparison = xPart.CompareTo(yPart);
                if (partComparison != 0)
                {
                    return partComparison;
                }
            }

            return 0;
        }
    }
}
