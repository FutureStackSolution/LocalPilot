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
        private const int CurrentSchemaVersion = 2;

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

        private readonly object _initLock = new object();
        private bool _isInitialized = false;

        private StorageService()
        {
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LocalPilot");
            if (!Directory.Exists(appData)) Directory.CreateDirectory(appData);
            _dbPath = Path.Combine(appData, "localpilot_v2.db");
            
            LocalPilotLogger.Log($"[Storage] Initializing persistent engine instance at: {_dbPath}", LogCategory.Storage);
            // 🚀 EXPERT: We no longer initialize in the constructor to avoid UI blocking.
            // Initialization happens lazily on the first request.
        }

        private void InitializeDatabase()
        {
            lock (_initLock)
            {
                if (_isInitialized && _connection != null && _connection.State == System.Data.ConnectionState.Open) return;

                int retryCount = 0;
                const int MaxRetries = 1;

                while (retryCount <= MaxRetries)
                {
                    try
                    {
                        if (_connection == null)
                        {
                            _connection = new SqliteConnection($"Data Source={_dbPath}");
                        }

                        if (_connection.State != System.Data.ConnectionState.Open)
                        {
                            _connection.Open();
                        }

                        // 1. Core Performance Tuning
                        using (var cmd = _connection.CreateCommand())
                        {
                            cmd.CommandText = @"
                                PRAGMA journal_mode=WAL; 
                                PRAGMA synchronous=NORMAL; 
                                PRAGMA busy_timeout=5000;
                                PRAGMA foreign_keys=ON;
                                PRAGMA mmap_size=268435456; 
                                PRAGMA cache_size=-20000;
                                PRAGMA page_size=4096;";
                            cmd.ExecuteNonQuery();
                        }

                        // 2. Schema Version Check
                        int version = 0;
                        using (var cmd = _connection.CreateCommand())
                        {
                            cmd.CommandText = "PRAGMA user_version;";
                            version = Convert.ToInt32(cmd.ExecuteScalar());
                        }

                        if (version < CurrentSchemaVersion)
                        {
                            LocalPilotLogger.Log($"[Storage] Database schema outdated (v{version} -> v{CurrentSchemaVersion}). Applying migrations...", LogCategory.Storage);
                            ApplyMigrations(version);
                        }

                        // 3. Final Verification: Ensure critical tables exist
                        using (var cmd = _connection.CreateCommand())
                        {
                            cmd.CommandText = "CREATE TABLE IF NOT EXISTS Files (Path TEXT PRIMARY KEY, Hash TEXT, Content TEXT, LastIndexed DATETIME, Metadata TEXT);";
                            cmd.ExecuteNonQuery();
                        }

                        _isInitialized = true;
                        return; // Success!
                    }
                    catch (Exception ex)
                    {
                        LocalPilotLogger.LogError($"[Storage] Database initialization attempt {retryCount + 1} failed", ex);
                        
                        try { _connection?.Close(); _connection?.Dispose(); _connection = null; } catch { }

                        if (retryCount < MaxRetries)
                        {
                            ResetDatabase();
                            retryCount++;
                        }
                        else
                        {
                            LocalPilotLogger.Log("[Storage] CRITICAL: Self-healing failed. LocalPilot will proceed in memory-only mode where possible.", LogCategory.Storage, LogSeverity.Error);
                            _isInitialized = true; // Mark as 'attempted' to prevent infinite retry loops
                            break;
                        }
                    }
                }
            }
        }

        private void ResetDatabase()
        {
            try
            {
                _connection?.Close();
                _connection = null;

                if (File.Exists(_dbPath))
                {
                    try { File.Delete(_dbPath); }
                    catch (IOException ioEx) 
                    { 
                        LocalPilotLogger.Log($"[Storage] Could not delete database file (locked by another process). Proceeding with current file: {ioEx.Message}", LogCategory.Storage, LogSeverity.Warning); 
                    }
                    LocalPilotLogger.Log("[Storage] Corrupt database file deleted to allow clean re-initialization.", LogCategory.Storage, LogSeverity.Warning);
                }
            }
            catch (Exception ex)
            {
                LocalPilotLogger.LogError("[Storage] Failed to reset corrupted database", ex);
            }
        }

        private void ApplyMigrations(int currentVersion)
        {
            using (var transaction = _connection.BeginTransaction())
            {
                try
                {
                    using (var cmd = _connection.CreateCommand())
                    {
                        cmd.Transaction = transaction;

                        if (currentVersion < 1)
                        {
                            // Initial baseline
                            cmd.CommandText = @"
                                CREATE TABLE IF NOT EXISTS Files (Path TEXT PRIMARY KEY, Hash TEXT, Content TEXT, LastIndexed DATETIME, Metadata TEXT);
                                CREATE TABLE IF NOT EXISTS NexusNodes (Id TEXT PRIMARY KEY, Label TEXT, Type TEXT, Metadata TEXT);
                                CREATE TABLE IF NOT EXISTS NexusEdges (SourceId TEXT, TargetId TEXT, Type TEXT, PRIMARY KEY (SourceId, TargetId, Type));
                                CREATE VIRTUAL TABLE IF NOT EXISTS SearchIndex USING fts5(Content, Path UNINDEXED, ChunkId UNINDEXED, tokenize='porter unicode61');
                                CREATE TABLE IF NOT EXISTS Chunks (Id INTEGER PRIMARY KEY AUTOINCREMENT, Path TEXT, Content TEXT, Vector BLOB);
                                CREATE TABLE IF NOT EXISTS EmbeddingCache (Key TEXT PRIMARY KEY, Vector BLOB, Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP);
                                CREATE INDEX IF NOT EXISTS idx_chunks_path ON Chunks(Path);";
                            cmd.ExecuteNonQuery();
                        }

                        if (currentVersion < 2)
                        {
                            // Migration for v2: Ensure SearchIndex has ChunkId (recreate if missing as FTS5 is limited)
                            bool hasChunkId = false;
                            try
                            {
                                using (var checkCmd = _connection.CreateCommand()) {
                                    checkCmd.Transaction = transaction;
                                    checkCmd.CommandText = "SELECT ChunkId FROM SearchIndex LIMIT 1";
                                    checkCmd.ExecuteScalar();
                                    hasChunkId = true;
                                }
                            } catch { hasChunkId = false; }

                            if (!hasChunkId)
                            {
                                cmd.CommandText = "DROP TABLE IF EXISTS SearchIndex; CREATE VIRTUAL TABLE SearchIndex USING fts5(Content, Path UNINDEXED, ChunkId UNINDEXED, tokenize='porter unicode61');";
                                cmd.ExecuteNonQuery();
                            }
                        }

                        // Update version pragma
                        cmd.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion};";
                        cmd.ExecuteNonQuery();
                    }
                    transaction.Commit();
                    LocalPilotLogger.Log($"[Storage] Database migrated to v{CurrentSchemaVersion} successfully.", LogCategory.Storage);
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    throw new Exception("Migration failed. Database will be reset.", ex);
                }
            }
        }

        public SqliteConnection GetConnection()
        {
            if (!_isInitialized || _connection == null || _connection.State != System.Data.ConnectionState.Open)
            {
                InitializeDatabase();
            }

            if (_connection == null)
            {
                throw new InvalidOperationException("LocalPilot Storage Engine failed to initialize. Please check the logs.");
            }

            return _connection;
        }
        public System.Threading.SemaphoreSlim GetLock() => _dbLock;

        public async Task<float[]> GetCachedEmbeddingAsync(string key)
        {
            await _dbLock.WaitAsync();
            try
            {
                using (var cmd = GetConnection().CreateCommand())
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

                using (var cmd = GetConnection().CreateCommand())
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
                using (var cmd = GetConnection().CreateCommand())
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
