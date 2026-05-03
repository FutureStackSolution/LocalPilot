using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace LocalPilot.Services
{
    /// <summary>
    /// The Persistent Storage Engine. 
    /// Replaces JSON-based memory-resident storage with a high-performance SQLite backend.
    /// Supports WAL (Write-Ahead Logging) for concurrent reads/writes and FTS5 for ultra-fast text search.
    /// </summary>
    public class StorageService : IDisposable
    {
        private static StorageService _instance;
        private static readonly object _lock = new object();
        private static readonly System.Threading.SemaphoreSlim _dbLock = new System.Threading.SemaphoreSlim(1, 1);
        private readonly string _dbPath;
        private SqliteConnection _connection;

        public static StorageService Instance
        {
            get
            {
                lock (_lock)
                {
                    return _instance ?? (_instance = new StorageService());
                }
            }
        }

        private StorageService()
        {
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LocalPilot");
            if (!Directory.Exists(appData)) Directory.CreateDirectory(appData);
            _dbPath = Path.Combine(appData, "localpilot_v2.db");
            
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            _connection = new SqliteConnection($"Data Source={_dbPath}");
            _connection.Open();

            // 🚀 PERFORMANCE TUNING: Enable WAL mode for high concurrency
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                cmd.ExecuteNonQuery();
            }

            using (var transaction = _connection.BeginTransaction())
            {
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = transaction;

                    // 1. Files table (RAG / Context)
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS Files (
                            Path TEXT PRIMARY KEY,
                            Hash TEXT,
                            Content TEXT,
                            LastIndexed DATETIME,
                            Metadata TEXT
                        );";
                    cmd.ExecuteNonQuery();

                    // 2. Nexus Graph (Dependencies)
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS NexusNodes (
                            Id TEXT PRIMARY KEY,
                            Label TEXT,
                            Type TEXT,
                            Metadata TEXT
                        );
                        CREATE TABLE IF NOT EXISTS NexusEdges (
                            SourceId TEXT,
                            TargetId TEXT,
                            Type TEXT,
                            PRIMARY KEY (SourceId, TargetId, Type)
                        );";
                    cmd.ExecuteNonQuery();

                    // 3. FTS5 Search Index (for ultra-fast text retrieval)
                    // Note: If FTS5 is not available in the binary, this might fail, 
                    // but modern Microsoft.Data.Sqlite includes it.
                    cmd.CommandText = @"
                        CREATE VIRTUAL TABLE IF NOT EXISTS SearchIndex USING fts5(
                            Content, 
                            Path UNINDEXED, 
                            tokenize='porter unicode61'
                        );";
                    cmd.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        public SqliteConnection GetConnection() => _connection;
        public System.Threading.SemaphoreSlim GetLock() => _dbLock;

        public async Task ExecuteAsync(string sql, object parameters = null)
        {
            await _dbLock.WaitAsync();
            try
            {
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = sql;
                    AddParameters(cmd, parameters);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            finally
            {
                _dbLock.Release();
            }
        }

        private void AddParameters(SqliteCommand cmd, object parameters)
        {
            if (parameters == null) return;
            foreach (var prop in parameters.GetType().GetProperties())
            {
                cmd.Parameters.AddWithValue($"@{prop.Name}", prop.GetValue(parameters) ?? DBNull.Value);
            }
        }

        public void Dispose()
        {
            _connection?.Close();
            _connection?.Dispose();
        }
    }
}
