using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SQLite;

namespace triaxis.Data.Sqlite
{
    /// <summary>
    /// Represents a single SQLite database file
    /// </summary>
    public class Database
    {
        private readonly string _path;
        private readonly ILogger _logger;

        /// <summary>
        /// Creates a <see cref="Database" /> instance
        /// </summary>
        public Database(string path, ILogger logger)
        {
            _path = path;
            _logger = logger;
        }

        /// <summary>
        /// Gets the underlying <see cref="SQLiteConnection" /> to the database
        /// </summary>
        public SQLiteConnection Connection { get; private set; }

        /// <summary>
        /// Opens and initializes the database, using the provided schema
        /// </summary>
        public void Initialize(Action<SQLiteConnection> schema)
        {
            SQLiteConnection con = null;

            try
            {
                _logger?.LogInformation("Opening database: {Path}", _path);

                string upgradePath = _path + ".$upg";

                if (File.Exists(upgradePath))
                {
                    if (!File.Exists(_path))
                    {
                        _logger?.LogWarning("Moving finished upgrade over non-existent primary file");
                        File.Move(upgradePath, _path);
                    }
                    else
                    {
                        _logger?.LogWarning("Deleting unfinished upgrade file");
                        File.Delete(upgradePath);
                    }
                }

                con = new SQLiteConnection(_path);

                string currentSchema = GetSchema(con);
                // get the required datbase schema by creating an empty in-memory database
                string requiredSchema = GetRequiredSchema(schema);

                if (currentSchema != requiredSchema)
                {
                    // create a new database, copy data over and replace the active database
                    _logger?.LogInformation("Upgrading database schema");
                    using (var conUpgrade = new SQLiteConnection(upgradePath))
                    {
                        schema(conUpgrade);
                        _logger?.LogInformation("Moving existing data");
                        CopyData(con, _path, conUpgrade);
                    }

                    con.Dispose();

                    _logger?.LogInformation("Replacing databse file with the upgraded one");
                    File.Delete(_path);
                    File.Move(upgradePath, _path);

                    con = new SQLiteConnection(_path);
                }

                _logger?.LogInformation("Database opened successfully");
                Connection = con;
                con = null;
            }
            catch (Exception err)
            {
                _logger?.LogError(err, "Failed to initialize database");
                throw;
            }
            finally
            {
                con?.Dispose();
            }
        }

        /// <summary>
        /// Gets the required database schema by creating an empty in-memory database
        /// </summary>
        static string GetRequiredSchema(Action<SQLiteConnection> schema)
        {
            using (var con = new SQLiteConnection(":memory:"))
            {
                schema(con);
                return GetSchema(con);
            }
        }

        class sqlite_master
        {
            public string type { get; set; }
            public string tbl_name { get; set; }
            public string name { get; set; }
            public string sql { get; set; }
        }

        /// <summary>
        /// Gets the schema of an existing SQLite database
        /// </summary>
        private static string GetSchema(SQLiteConnection con)
        {
            StringBuilder sb = new StringBuilder();

            foreach (var tbl in con.Query<sqlite_master>("SELECT type, tbl_name, name, sql FROM sqlite_master ORDER BY type, tbl_name, name"))
            {
                sb.AppendFormat("-- type: {0}, tbl_name: {1}, name: {2}", tbl.type, tbl.tbl_name, tbl.name);
                sb.AppendLine();
                sb.AppendLine(tbl.sql ?? "-- <NULL>");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static List<string> GetTables(SQLiteConnection con)
            => con.Query<sqlite_master>("SELECT [name] FROM [sqlite_master] WHERE [type] = 'table'").ConvertAll(m => m.name);

        private static List<string> GetColumns(SQLiteConnection con, string table)
            => con.Query<sqlite_master>("PRAGMA table_info('" + table + "')").ConvertAll(m => m.name);

        private static void CopyData(SQLiteConnection src, string srcFile, SQLiteConnection dst)
        {
            var triggers = src.Query<sqlite_master>("SELECT [name], [sql] FROM [sqlite_master] WHERE [type] = 'trigger'");

            var commands = new List<string>();

            foreach (string table in GetTables(src))
            {
                var srcColumns = GetColumns(src, table);
                var dstColumns = GetColumns(dst, table);
                var commonColumns = srcColumns.Intersect(dstColumns, StringComparer.OrdinalIgnoreCase).ToList();

                if (commonColumns.Count == 0)
                    continue;

                string columns = "[" + string.Join("], [", commonColumns) + "]";

                StringBuilder sb = new StringBuilder();
                sb.Append("REPLACE INTO [");
                sb.Append(table);
                sb.Append("] (");
                sb.Append(columns);
                sb.Append(") SELECT ");
                sb.Append(columns);
                sb.Append(" FROM [source].[");
                sb.Append(table);
                sb.Append("]");

                commands.Add(sb.ToString());
            }

            dst.Execute("ATTACH ? AS [source]", srcFile);

            dst.BeginTransaction();
            bool success = false;
            try
            {
                // drop triggers
                foreach (var trigger in triggers)
                    dst.Execute($"DROP TRIGGER [{trigger.name}]");

                // copy data
                foreach (var cmd in commands)
                    dst.Execute(cmd);

                // recreate triggers
                foreach (var trigger in triggers)
                    dst.Execute(trigger.sql);

                dst.Commit();
                success = true;
            }
            finally
            {
                if (!success)
                {
                    dst.Rollback();
                }
                dst.Execute("DETACH [source]");
            }
        }
    }
}
