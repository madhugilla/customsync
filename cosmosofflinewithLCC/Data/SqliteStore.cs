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
            _idProp = typeof(T).GetProperty("ID") ?? throw new Exception("Model must have ID property");
            _lastModifiedProp = typeof(T).GetProperty("LastModified") ?? throw new Exception("Model must have LastModified property");

            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            connection.Open();

            var tableCmd = connection.CreateCommand();
            // Create the main table
            tableCmd.CommandText = $"CREATE TABLE IF NOT EXISTS [{_tableName}] (ID TEXT PRIMARY KEY, Content TEXT, LastModified TEXT, OIID TEXT)";
            tableCmd.ExecuteNonQuery();

            // Create the pending changes table
            tableCmd.CommandText = $"CREATE TABLE IF NOT EXISTS PendingChanges_{_tableName} (ID TEXT PRIMARY KEY)";
            tableCmd.ExecuteNonQuery();

            // Check table info
            tableCmd.CommandText = $"PRAGMA table_info([{_tableName}])";
            using var reader = tableCmd.ExecuteReader();
            bool hasOIIDColumn = false;
            while (reader.Read())
            {
                if (reader.GetString(1) == "OIID")
                {
                    hasOIIDColumn = true;
                    break;
                }
            }
            reader.Close();

            if (!hasOIIDColumn)
            {
                var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = $"ALTER TABLE [{_tableName}] ADD COLUMN OIID TEXT;";
                alterCmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Gets a document by ID and userId (more efficient)
        /// </summary>
        /// <param name="id">The document ID</param>
        /// <param name="userId">The user ID</param>
        /// <returns>The retrieved document or null if not found</returns>
        public async Task<T?> GetAsync(string id, string userId)
        {
            if (string.IsNullOrEmpty(userId))
            {
                throw new ArgumentException("userId must not be null or empty for efficient reads", nameof(userId));
            }

            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            var cmd = connection.CreateCommand();            // Use both ID and userId for more efficient querying
            cmd.CommandText = $"SELECT Content FROM [{_tableName}] WHERE ID = @id AND (json_extract(Content, '$.oiid') = @userId OR json_extract(Content, '$.OIID') = @userId)";

            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@userId", userId);

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                var json = reader.GetString(0);
                return System.Text.Json.JsonSerializer.Deserialize<T>(json);
            }
            return null;
        }
        public Task UpsertAsync(T document)
        {
            return UpsertAsync(document, true);
        }

        public async Task UpsertAsync(T document, bool markAsPending)
        {
            var id = _idProp.GetValue(document)?.ToString() ?? throw new Exception("Id required");
            var lastModified = _lastModifiedProp.GetValue(document)?.ToString() ?? throw new Exception("LastModified required");
            string? userId = null;
            var userIdProp = typeof(T).GetProperty("OIID");
            if (userIdProp != null)
            {
                userId = userIdProp.GetValue(document)?.ToString();
            }

            var json = System.Text.Json.JsonSerializer.Serialize(document);
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            var cmd = connection.CreateCommand();
            cmd.CommandText = $@"INSERT INTO [{_tableName}] (ID, Content, LastModified, OIID) 
                               VALUES (@id, @content, @lastModified, @userId) 
                               ON CONFLICT(ID) DO UPDATE SET 
                               Content = @content, 
                               LastModified = @lastModified,
                               OIID = @userId;";

            if (markAsPending)
            {
                cmd.CommandText += $@"
                               INSERT OR IGNORE INTO PendingChanges_{_tableName} (ID) VALUES (@id);";
            }

            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@content", json);
            cmd.Parameters.AddWithValue("@lastModified", lastModified);
            cmd.Parameters.AddWithValue("@userId", userId ?? (object)DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        public Task UpsertBulkAsync(IEnumerable<T> documents)
        {
            return UpsertBulkAsync(documents, true);
        }

        public async Task UpsertBulkAsync(IEnumerable<T> documents, bool markAsPending)
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();

            foreach (var document in documents)
            {
                var id = _idProp.GetValue(document)?.ToString() ?? throw new Exception("Id required");
                var lastModified = _lastModifiedProp.GetValue(document)?.ToString() ?? throw new Exception("LastModified required");
                string? userId = null;
                var userIdProp = typeof(T).GetProperty("OIID");
                if (userIdProp != null)
                {
                    userId = userIdProp.GetValue(document)?.ToString();
                }

                var json = System.Text.Json.JsonSerializer.Serialize(document);

                var cmd = connection.CreateCommand();
                cmd.CommandText = $@"INSERT INTO [{_tableName}] (ID, Content, LastModified, OIID) 
                                    VALUES (@id, @content, @lastModified, @userId) 
                                    ON CONFLICT(ID) DO UPDATE SET 
                                    Content = @content, 
                                    LastModified = @lastModified,
                                    OIID = @userId;";

                if (markAsPending)
                {
                    cmd.CommandText += $@"
                                    INSERT OR IGNORE INTO PendingChanges_{_tableName} (ID) VALUES (@id);";
                }

                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@content", json);
                cmd.Parameters.AddWithValue("@lastModified", lastModified);
                cmd.Parameters.AddWithValue("@userId", userId ?? (object)DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        public async Task UpsertBulkWithoutPendingAsync(IEnumerable<T> documents)
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();

            foreach (var document in documents)
            {
                var id = _idProp.GetValue(document)?.ToString() ?? throw new Exception("Id required");
                var lastModified = _lastModifiedProp.GetValue(document)?.ToString() ?? throw new Exception("LastModified required");
                string? userId = null;
                var userIdProp = typeof(T).GetProperty("OIID");
                if (userIdProp != null)
                {
                    userId = userIdProp.GetValue(document)?.ToString();
                }

                var json = System.Text.Json.JsonSerializer.Serialize(document);

                var cmd = connection.CreateCommand();
                cmd.CommandText = $@"INSERT INTO [{_tableName}] (ID, Content, LastModified, OIID) 
                                    VALUES (@id, @content, @lastModified, @userId) 
                                    ON CONFLICT(ID) DO UPDATE SET 
                                    Content = @content, 
                                    LastModified = @lastModified,
                                    OIID = @userId;";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@content", json);
                cmd.Parameters.AddWithValue("@lastModified", lastModified);
                cmd.Parameters.AddWithValue("@userId", userId ?? (object)DBNull.Value);
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
            cmd.CommandText = $"SELECT i.Content FROM [{_tableName}] i JOIN PendingChanges_{_tableName} p ON i.ID = p.ID";
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
            cmd.CommandText = $"DELETE FROM PendingChanges_{_tableName} WHERE ID = @id";
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
            cmd.CommandText = $@"SELECT Content FROM [{_tableName}]                                WHERE json_extract(Content, '$.oiid') = @userId 
                                OR json_extract(Content, '$.OIID') = @userId";
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
            var cmd = connection.CreateCommand(); cmd.CommandText = $@"SELECT i.Content FROM [{_tableName}] i 
                       JOIN PendingChanges_{_tableName} p 
                       ON i.Id = p.Id 
                       WHERE json_extract(i.Content, '$.oiid') = @userId 
                       OR json_extract(i.Content, '$.OIID') = @userId";
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