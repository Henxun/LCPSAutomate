using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace LCPSAutomate
{
    public class SqliteDataAccess : IDisposable
    {
        private readonly string _dbPath;
        private readonly string _connectionString;

        public SqliteDataAccess(string dbPath = "")
        {
            _dbPath = string.IsNullOrWhiteSpace(dbPath) ? "lcpsautomate.db" : dbPath;
            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _connectionString = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();

            EnsureDatabaseAsync().GetAwaiter().GetResult();
        }

        public async Task<bool> QrExistsAsync(string qr)
        {
            if (qr == null) throw new ArgumentNullException(nameof(qr));
            await using var con = new SqliteConnection(_connectionString);
            await con.OpenAsync();
            await using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM Records WHERE Qr = $qr LIMIT 1;";
            cmd.Parameters.AddWithValue("$qr", qr);
            await using var reader = await cmd.ExecuteReaderAsync();
            return await reader.ReadAsync();
        }

        private async Task EnsureDatabaseAsync()
        {
            await using var con = new SqliteConnection(_connectionString);
            await con.OpenAsync();

            // Records ±í
            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Records (
                    Qr TEXT NOT NULL PRIMARY KEY,
                    IsProcessed INTEGER NOT NULL
                );";
            await cmd.ExecuteNonQueryAsync();

            // FileReadRecord ±í
            cmd = con.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS FileReadRecord (
                    FilePath TEXT NOT NULL PRIMARY KEY,
                    LastPosition INTEGER NOT NULL
                );";
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task AddOrIgnoreRecordAsync(Records r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            await using var con = new SqliteConnection(_connectionString);
            await con.OpenAsync();
            await using var cmd = con.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO Records (Qr, IsProcessed) VALUES ($qr, $is);";
            cmd.Parameters.AddWithValue("$qr", r.Qr ?? string.Empty);
            cmd.Parameters.AddWithValue("$is", r.IsProcessed ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<IEnumerable<Records>> GetUnprocessedRecordsAsync()
        {
            var list = new List<Records>();
            await using var con = new SqliteConnection(_connectionString);
            await con.OpenAsync();
            await using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT Qr, IsProcessed FROM Records WHERE IsProcessed = 0;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new Records
                {
                    Qr = reader.GetString(0),
                    IsProcessed = reader.GetInt32(1) != 0
                });
            }
            return list;
        }

        public async Task MarkRecordProcessedByQrAsync(string qr)
        {
            if (qr == null) throw new ArgumentNullException(nameof(qr));
            await using var con = new SqliteConnection(_connectionString);
            await con.OpenAsync();
            await using var cmd = con.CreateCommand();
            cmd.CommandText = "UPDATE Records SET IsProcessed = 1 WHERE Qr = $qr;";
            cmd.Parameters.AddWithValue("$qr", qr);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<IList<FileReadRecord>> GetFileReadRecordsAsync()
        {
            var list = new List<FileReadRecord>();
            await using var con = new SqliteConnection(_connectionString);
            await con.OpenAsync();
            await using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT FilePath, LastPosition FROM FileReadRecord;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new FileReadRecord
                {
                    FilePath = reader.GetString(0),
                    LastPosition = reader.GetInt64(1)
                });
            }
            return list;
        }

        public async Task<FileReadRecord?> GetFileReadRecordAsync(string filePath)
        {
            if (filePath == null) throw new ArgumentNullException(nameof(filePath));
            await using var con = new SqliteConnection(_connectionString);
            await con.OpenAsync();
            await using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT FilePath, LastPosition FROM FileReadRecord WHERE FilePath = $p;";
            cmd.Parameters.AddWithValue("$p", filePath);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new FileReadRecord
                {
                    FilePath = reader.GetString(0),
                    LastPosition = reader.GetInt64(1)
                };
            }
            return null;
        }

        public async Task UpsertFileReadRecordAsync(FileReadRecord rec)
        {
            if (rec == null) throw new ArgumentNullException(nameof(rec));
            await using var con = new SqliteConnection(_connectionString);
            await con.OpenAsync();
            await using var cmd = con.CreateCommand();
            cmd.CommandText = "INSERT INTO FileReadRecord (FilePath, LastPosition) VALUES ($p, $pos) ON CONFLICT(FilePath) DO UPDATE SET LastPosition = $pos;";
            cmd.Parameters.AddWithValue("$p", rec.FilePath);
            cmd.Parameters.AddWithValue("$pos", rec.LastPosition);
            await cmd.ExecuteNonQueryAsync();
        }

        public void Dispose()
        {
            // nothing to dispose for now
        }
    }
}
