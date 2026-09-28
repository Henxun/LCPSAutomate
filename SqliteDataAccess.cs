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

            // Records 表
            var cmd = con.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Records (
                    Qr TEXT NOT NULL PRIMARY KEY,
                    IsProcessed INTEGER NOT NULL,
                    RetryCount INTEGER NOT NULL DEFAULT 0,
                    LastError TEXT NULL,
                    UpdatedAtUtc TEXT NOT NULL DEFAULT ''
                );";
            await cmd.ExecuteNonQueryAsync();

            // 兼容旧版本数据库。
            await EnsureColumnAsync(con, "RetryCount", "INTEGER NOT NULL DEFAULT 0");
            await EnsureColumnAsync(con, "LastError", "TEXT NULL");
            await EnsureColumnAsync(con, "UpdatedAtUtc", "TEXT NOT NULL DEFAULT ''");

            // FileReadRecord 表
            cmd = con.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS FileReadRecord (
                    FilePath TEXT NOT NULL PRIMARY KEY,
                    LastPosition INTEGER NOT NULL
                );";
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task EnsureColumnAsync(SqliteConnection con, string columnName, string definition)
        {
            await using var info = con.CreateCommand();
            info.CommandText = "PRAGMA table_info(Records);";
            await using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            await reader.DisposeAsync();
            await using var alter = con.CreateCommand();
            alter.CommandText = $"ALTER TABLE Records ADD COLUMN {columnName} {definition};";
            await alter.ExecuteNonQueryAsync();
        }

        public async Task AddOrIgnoreRecordAsync(Records r)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            await using var con = new SqliteConnection(_connectionString);
            await con.OpenAsync();
            await using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                INSERT OR IGNORE INTO Records
                    (Qr, IsProcessed, RetryCount, LastError, UpdatedAtUtc)
                VALUES ($qr, $is, $retry, $error, $updated);";
            cmd.Parameters.AddWithValue("$qr", r.Qr ?? string.Empty);
            cmd.Parameters.AddWithValue("$is", r.IsProcessed ? 1 : 0);
            cmd.Parameters.AddWithValue("$retry", r.RetryCount);
            cmd.Parameters.AddWithValue("$error", (object?)r.LastError ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$updated", string.IsNullOrEmpty(r.UpdatedAtUtc) ? DateTime.UtcNow.ToString("O") : r.UpdatedAtUtc);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task<IEnumerable<Records>> GetUnprocessedRecordsAsync()
        {
            var list = new List<Records>();
            await using var con = new SqliteConnection(_connectionString);
            await con.OpenAsync();
            await using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT Qr, IsProcessed, RetryCount, LastError, UpdatedAtUtc FROM Records WHERE IsProcessed = 0;";
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new Records
                {
                    Qr = reader.GetString(0),
                    IsProcessed = reader.GetInt32(1) != 0,
                    RetryCount = reader.GetInt32(2),
                    LastError = reader.IsDBNull(3) ? null : reader.GetString(3),
                    UpdatedAtUtc = reader.IsDBNull(4) ? string.Empty : reader.GetString(4)
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
            cmd.CommandText = @"
                UPDATE Records
                SET IsProcessed = 1,
                    LastError = NULL,
                    UpdatedAtUtc = $updated
                WHERE Qr = $qr;";
            cmd.Parameters.AddWithValue("$qr", qr);
            cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task MarkRecordFailedByQrAsync(string qr, string error)
        {
            if (qr == null) throw new ArgumentNullException(nameof(qr));
            await using var con = new SqliteConnection(_connectionString);
            await con.OpenAsync();
            await using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                UPDATE Records
                SET IsProcessed = 0,
                    RetryCount = RetryCount + 1,
                    LastError = $error,
                    UpdatedAtUtc = $updated
                WHERE Qr = $qr;";
            cmd.Parameters.AddWithValue("$qr", qr);
            cmd.Parameters.AddWithValue("$error", error ?? string.Empty);
            cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// 在同一个事务中保存待提交 QR 和文件读取位置。
        /// 只有事务成功后，调用方才可以认为这段文件内容已经安全接收。
        /// 返回仍处于待提交状态的 QR；已成功处理过的重复 QR 不会返回。
        /// </summary>
        public async Task<IList<string>> PersistPendingRecordsAndFilePositionAsync(
            IEnumerable<string> qrs,
            FileReadRecord rec)
        {
            if (qrs == null) throw new ArgumentNullException(nameof(qrs));
            if (rec == null) throw new ArgumentNullException(nameof(rec));

            var pending = new List<string>();
            var distinctQrs = qrs
                .Select(qr => qr.Trim())
                .Where(qr => qr.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            await using var con = new SqliteConnection(_connectionString);
            await con.OpenAsync();
            await using var transaction = await con.BeginTransactionAsync();

            foreach (var qr in distinctQrs)
            {
                await using (var insert = con.CreateCommand())
                {
                    insert.Transaction = (SqliteTransaction)transaction;
                    insert.CommandText = @"
                        INSERT OR IGNORE INTO Records
                            (Qr, IsProcessed, RetryCount, LastError, UpdatedAtUtc)
                        VALUES ($qr, 0, 0, NULL, $updated);";
                    insert.Parameters.AddWithValue("$qr", qr);
                    insert.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
                    await insert.ExecuteNonQueryAsync();
                }

                await using var status = con.CreateCommand();
                status.Transaction = (SqliteTransaction)transaction;
                status.CommandText = "SELECT IsProcessed FROM Records WHERE Qr = $qr;";
                status.Parameters.AddWithValue("$qr", qr);
                var value = await status.ExecuteScalarAsync();
                if (value != null && Convert.ToInt32(value) == 0)
                {
                    pending.Add(qr);
                }
            }

            await using (var position = con.CreateCommand())
            {
                position.Transaction = (SqliteTransaction)transaction;
                position.CommandText = @"
                    INSERT INTO FileReadRecord (FilePath, LastPosition)
                    VALUES ($p, $pos)
                    ON CONFLICT(FilePath) DO UPDATE SET LastPosition = $pos;";
                position.Parameters.AddWithValue("$p", rec.FilePath);
                position.Parameters.AddWithValue("$pos", rec.LastPosition);
                await position.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            return pending;
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
