using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace cosmosofflinewithLCC.Data
{
    // SQLite-based local store (offline, generic)
    public class SqliteStore<T> : IDocumentStore<T> where T : class, new()
    {
        private readonly string _dbPath;
        private readonly string _tableName;
        private readonly PropertyInfo _idProp;
        private readonly PropertyInfo _lastModifiedProp;

        public SqliteStore(string dbPath)
        {
            _dbPath = dbPath;
            _tableName = typeof(T).Name + "s";
            _idProp = typeof(T).GetProperty("Id") ?? throw new Exception("Model must have Id property");
            _lastModifiedProp = typeof(T).GetProperty("LastModified") ?? throw new Exception("Model must have LastModified property");

            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();
            var tableCmd = connection.CreateCommand();

            // Update the table schema to include UserId column if it doesn't exist
            tableCmd.CommandText = $@"
                CREATE TABLE IF NOT EXISTS [{_tableName}] (
                    Id TEXT PRIMARY KEY, 
                    Content TEXT, 
                    LastModified TEXT,
                    UserId TEXT
                ); 
                CREATE TABLE IF NOT EXISTS PendingChanges_{_tableName} (Id TEXT PRIMARY KEY);
                
                -- Check if UserId column exists and add it if it doesn't
                PRAGMA table_info([{_tableName}]);
            ";

            using var reader = tableCmd.ExecuteReader();
            bool hasUserIdColumn = false;
            while (reader.Read())
            {
                if (reader.GetString(1) == "UserId")
                {
                    hasUserIdColumn = true;
                    break;
                }
            }
            reader.Close();

            if (!hasUserIdColumn)
            {
                var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = $"ALTER TABLE [{_tableName}] ADD COLUMN UserId TEXT;";
                alterCmd.ExecuteNonQuery();
            }
        }
        public async Task<T?> GetAsync(string id)
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT Content FROM [{_tableName}] WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var json = reader.GetString(0);
                return System.Text.Json.JsonSerializer.Deserialize<T>(json);
            }
            return null;
        }
        public async Task UpsertAsync(T document)
        {
            var id = _idProp.GetValue(document)?.ToString() ?? throw new Exception("Id required");
            var lastModified = _lastModifiedProp.GetValue(document)?.ToString() ?? throw new Exception("LastModified required");
            var json = System.Text.Json.JsonSerializer.Serialize(document);
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            var cmd = connection.CreateCommand();
            cmd.CommandText = $@"INSERT INTO [{_tableName}] (Id, Content, LastModified) VALUES (@id, @content, @lastModified) ON CONFLICT(Id) DO UPDATE SET Content = @content, LastModified = @lastModified; INSERT OR IGNORE INTO PendingChanges_{_tableName} (Id) VALUES (@id);";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@content", json);
            cmd.Parameters.AddWithValue("@lastModified", lastModified);
            await cmd.ExecuteNonQueryAsync();
        }
        public async Task UpsertBulkAsync(IEnumerable<T> documents)
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();

            foreach (var document in documents)
            {
                var id = _idProp.GetValue(document)?.ToString() ?? throw new Exception("Id required");
                var lastModified = _lastModifiedProp.GetValue(document)?.ToString() ?? throw new Exception("LastModified required");
                var json = System.Text.Json.JsonSerializer.Serialize(document);

                var cmd = connection.CreateCommand();
                cmd.CommandText = $@"INSERT INTO [{_tableName}] (Id, Content, LastModified) VALUES (@id, @content, @lastModified) 
                                    ON CONFLICT(Id) DO UPDATE SET Content = @content, LastModified = @lastModified; 
                                    INSERT OR IGNORE INTO PendingChanges_{_tableName} (Id) VALUES (@id);";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@content", json);
                cmd.Parameters.AddWithValue("@lastModified", lastModified);
                await cmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        public async Task<List<T>> GetAllAsync()
        {
            var items = new List<T>();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT Content FROM [{_tableName}]";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var json = reader.GetString(0);
                items.Add(System.Text.Json.JsonSerializer.Deserialize<T>(json)!);
            }
            return items;
        }
        public async Task<List<T>> GetPendingChangesAsync()
        {
            var items = new List<T>();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT i.Content FROM [{_tableName}] i JOIN PendingChanges_{_tableName} p ON i.Id = p.Id";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var json = reader.GetString(0);
                items.Add(System.Text.Json.JsonSerializer.Deserialize<T>(json)!);
            }
            return items;
        }
        public async Task RemovePendingChangeAsync(string id)
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            var cmd = connection.CreateCommand();
            cmd.CommandText = $"DELETE FROM PendingChanges_{_tableName} WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Gets all documents for a specific user
        /// </summary>
        /// <param name="userId">The user ID to filter by</param>
        /// <returns>A list of documents belonging to the specified user</returns>
        public async Task<List<T>> GetByUserIdAsync(string userId)
        {
            if (string.IsNullOrEmpty(userId))
            {
                throw new ArgumentException("userId must not be null or empty", nameof(userId));
            }

            var items = new List<T>();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            // Try both camelCase and PascalCase property names for maximum compatibility
            var cmd = connection.CreateCommand();
            cmd.CommandText = $@"SELECT Content FROM [{_tableName}] 
                                WHERE json_extract(Content, '$.userId') = @userId 
                                OR json_extract(Content, '$.UserId') = @userId";
            cmd.Parameters.AddWithValue("@userId", userId);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var json = reader.GetString(0);
                items.Add(System.Text.Json.JsonSerializer.Deserialize<T>(json)!);
            }

            return items;
        }

        /// <summary>
        /// Gets all pending changes for a specific user
        /// </summary>
        /// <param name="userId">The user ID to filter by</param>
        /// <returns>A list of pending changes belonging to the specified user</returns>
        public async Task<List<T>> GetPendingChangesForUserAsync(string userId)
        {
            if (string.IsNullOrEmpty(userId))
            {
                throw new ArgumentException("userId must not be null or empty", nameof(userId));
            }

            var items = new List<T>();
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();

            // Optimize the query by directly filtering on the UserId field
            // This is more efficient than the general query with a WHERE clause on IsPendingChange
            // Since userId is mandatory, we can use an indexable query pattern
            var cmd = connection.CreateCommand();
            cmd.CommandText = $@"SELECT i.Content FROM [{_tableName}] i 
                       JOIN PendingChanges_{_tableName} p 
                       ON i.Id = p.Id 
                       WHERE json_extract(i.Content, '$.userId') = @userId 
                       OR json_extract(i.Content, '$.UserId') = @userId";
            cmd.Parameters.AddWithValue("@userId", userId);

            using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var json = reader.GetString(0);
                items.Add(System.Text.Json.JsonSerializer.Deserialize<T>(json)!);
            }

            return items;
        }
    }
}