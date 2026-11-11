using Microsoft.Data.Sqlite;

namespace Tuci.Sync;

internal sealed partial class Database
{
    private readonly string connectionString;
    public Database(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            ForeignKeys = true,
            DefaultTimeout = 15
        }.ToString();
        using var db = Open();
        db.Scalar("PRAGMA journal_mode=WAL");
        db.StartWrite();
        if (Convert.ToInt64(db.Scalar("PRAGMA user_version")) > 2)
            throw new InvalidOperationException("数据库版本比本程序新。");
        db.Execute("""
            CREATE TABLE IF NOT EXISTS vaults(id TEXT PRIMARY KEY, seq INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS devices(id TEXT PRIMARY KEY, vault TEXT NOT NULL REFERENCES vaults,
                name TEXT NOT NULL, tokenhash TEXT NOT NULL UNIQUE);
            CREATE TABLE IF NOT EXISTS codes(hash TEXT PRIMARY KEY, vault TEXT NOT NULL REFERENCES vaults,
                expires INTEGER NOT NULL, issuer TEXT REFERENCES devices(id) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS changes(vault TEXT NOT NULL REFERENCES vaults, seq INTEGER NOT NULL,
                note TEXT NOT NULL, ver INTEGER NOT NULL, path TEXT NOT NULL, body TEXT NOT NULL,
                deleted INTEGER NOT NULL, regions TEXT, PRIMARY KEY(vault,seq));
            CREATE TABLE IF NOT EXISTS heads(vault TEXT NOT NULL, note TEXT NOT NULL, seq INTEGER NOT NULL,
                pathkey TEXT NOT NULL, live INTEGER NOT NULL, PRIMARY KEY(vault,note),
                FOREIGN KEY(vault,seq) REFERENCES changes(vault,seq));
            CREATE UNIQUE INDEX IF NOT EXISTS live_paths ON heads(vault,pathkey) WHERE live=1;
            CREATE TABLE IF NOT EXISTS receipts(vault TEXT NOT NULL REFERENCES vaults, op TEXT NOT NULL,
                digest TEXT NOT NULL, result TEXT NOT NULL, PRIMARY KEY(vault,op));
            CREATE TABLE IF NOT EXISTS accounts(id TEXT PRIMARY KEY, username TEXT NOT NULL,
                namekey TEXT NOT NULL UNIQUE, vault TEXT NOT NULL UNIQUE REFERENCES vaults,
                password TEXT NOT NULL, created INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS account_sessions(device TEXT PRIMARY KEY REFERENCES devices(id) ON DELETE CASCADE,
                account TEXT NOT NULL REFERENCES accounts(id), expires INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS invites(hash TEXT PRIMARY KEY, created INTEGER NOT NULL,
                expires INTEGER NOT NULL, issuer TEXT NOT NULL, used INTEGER, usedby TEXT);
            CREATE INDEX IF NOT EXISTS invites_created ON invites(created DESC);
            CREATE TABLE IF NOT EXISTS auth_failures(namekey TEXT PRIMARY KEY, failures INTEGER NOT NULL, since INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS state_sequences(vault TEXT PRIMARY KEY REFERENCES vaults, seq INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS state_changes(vault TEXT NOT NULL REFERENCES vaults, seq INTEGER NOT NULL,
                key TEXT NOT NULL, value TEXT NOT NULL, deleted INTEGER NOT NULL, PRIMARY KEY(vault,seq));
            CREATE TABLE IF NOT EXISTS state_heads(vault TEXT NOT NULL, key TEXT NOT NULL, seq INTEGER NOT NULL,
                PRIMARY KEY(vault,key), FOREIGN KEY(vault,seq) REFERENCES state_changes(vault,seq));
            CREATE TABLE IF NOT EXISTS state_receipts(vault TEXT NOT NULL REFERENCES vaults, op TEXT NOT NULL,
                digest TEXT NOT NULL, result TEXT NOT NULL, PRIMARY KEY(vault,op));
            CREATE TABLE IF NOT EXISTS practice_contexts(vault TEXT NOT NULL REFERENCES vaults, note TEXT NOT NULL,
                word TEXT NOT NULL, sense TEXT NOT NULL, meaning TEXT NOT NULL, english TEXT NOT NULL,
                chinese TEXT NOT NULL, created INTEGER NOT NULL, answer_form TEXT, PRIMARY KEY(vault,note,word,sense),
                FOREIGN KEY(vault,note) REFERENCES heads(vault,note));
            PRAGMA user_version=2;
            """);
        // Schema-v2 deployments already have valid three-field context banks.
        // Adding a nullable field changes only the table metadata and leaves those rows intact.
        if (Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM pragma_table_info('practice_contexts') WHERE name='answer_form'")) == 0)
            db.Execute("ALTER TABLE practice_contexts ADD COLUMN answer_form TEXT");
        db.Commit();
    }
    public void Backup(string destination)
    {
        var file = Path.GetFullPath(destination);
        using (File.Open(file, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        try
        {
            using var source = Open();
            using var output = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = file,
                Pooling = false,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString());
            output.Open();
            source.Connection.BackupDatabase(output);
        }
        catch { File.Delete(file); throw; }
    }
    private DbSession Open() => new(connectionString);
}

internal sealed class DbSession : IDisposable
{
    internal SqliteConnection Connection { get; }
    private SqliteTransaction? transaction;
    internal DbSession(string source)
    {
        Connection = new SqliteConnection(source);
        Connection.Open();
    }
    public void StartWrite() => transaction = Connection.BeginTransaction(deferred: false);
    public void StartRead() => transaction = Connection.BeginTransaction(deferred: true);
    public void Commit() => transaction!.Commit();
    public SqliteCommand Cmd(string sql, params object?[] args)
    {
        var cmd = Connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        for (int i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("$p" + i, args[i] ?? DBNull.Value);
        return cmd;
    }
    public object? Scalar(string sql, params object?[] args)
    {
        using var cmd = Cmd(sql, args);
        return cmd.ExecuteScalar();
    }
    public int Execute(string sql, params object?[] args)
    {
        using var cmd = Cmd(sql, args);
        return cmd.ExecuteNonQuery();
    }
    public void Dispose()
    {
        transaction?.Dispose();
        Connection.Dispose();
    }
}
