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
            
            LocalPilotLogger.Log($"[Storage] Initializing persistent engine at: {_dbPath}", LogCategory.Storage);
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            _connection = new SqliteConnection($"Data Source={_dbPath}");
            _connection.Open();

            // 🚀 WORLD-CLASS PERFORMANCE TUNING
            using (var cmd = _connection.CreateCommand())
            {
                // WAL mode for concurrency
                // mmap_size for memory-mapped I/O (up to 256MB)
                // cache_size set to -20000 (roughly 20MB of memory cache)
                // synchronous=NORMAL for the best balance of safety and speed
                cmd.CommandText = @"
                    PRAGMA journal_mode=WAL; 
                    PRAGMA synchronous=NORMAL; 
                    PRAGMA mmap_size=268435456; 
                    PRAGMA cache_size=-20000;
                    PRAGMA page_size=4096;";
                cmd.ExecuteNonQuery();
                LocalPilotLogger.Log("[Storage] SQLite Turbo Mode enabled (WAL + MMAP).", LogCategory.Storage);
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
                    cmd.CommandText = @"
                        CREATE VIRTUAL TABLE IF NOT EXISTS SearchIndex USING fts5(
                            Content, 
                            Path UNINDEXED, 
                            ChunkId UNINDEXED,
                            tokenize='porter unicode61'
                        );";
                    cmd.ExecuteNonQuery();

                    // 4. Granular Chunks (for Semantic RAG)
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS Chunks (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            Path TEXT,
                            Content TEXT,
                            Vector BLOB
                        );
                        CREATE INDEX IF NOT EXISTS idx_chunks_path ON Chunks(Path);";
                    cmd.ExecuteNonQuery();

                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS EmbeddingCache (
                            Key TEXT PRIMARY KEY,
                            Vector BLOB,
                            Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP
                        );";
                    cmd.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }

        public SqliteConnection GetConnection() => _connection;
        public System.Threading.SemaphoreSlim GetLock() => _dbLock;

        public async Task<float[]> GetCachedEmbeddingAsync(string key)
        {
            await _dbLock.WaitAsync();
            try
            {
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT Vector FROM EmbeddingCache WHERE Key = @Key";
                    cmd.Parameters.AddWithValue("@Key", key);
                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        if (await reader.ReadAsync())
                        {
                            byte[] blob = reader.GetValue(0) as byte[];
                            if (blob == null) return null;
                            
                            var result = new float[blob.Length / 4];
                            Buffer.BlockCopy(blob, 0, result, 0, blob.Length);
                            return result;
                        }
                    }
                }
            }
            catch { }
            finally { _dbLock.Release(); }
            return null;
        }

        public async Task StoreCachedEmbeddingAsync(string key, float[] vector)
        {
            if (vector == null) return;
            await _dbLock.WaitAsync();
            try
            {
                byte[] blob = new byte[vector.Length * 4];
                Buffer.BlockCopy(vector, 0, blob, 0, blob.Length);

                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = "INSERT OR REPLACE INTO EmbeddingCache (Key, Vector) VALUES (@Key, @Vector)";
                    cmd.Parameters.AddWithValue("@Key", key);
                    cmd.Parameters.AddWithValue("@Vector", blob);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            catch { }
            finally { _dbLock.Release(); }
        }

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
            catch (Exception ex)
            {
                LocalPilotLogger.LogError($"[Storage] SQL Execution failed: {sql}", ex, LogCategory.Storage);
                throw;
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
