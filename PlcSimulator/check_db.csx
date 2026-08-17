using Microsoft.Data.Sqlite;
using System;

var configDir = Path.Combine(
    Environment.GetEnvironmentVariable("KANBAN_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kanban"),
    "Config");
foreach (var db in new[] { "production_logs.db", "alarm_events.db", "status_transitions.db", "work_orders.db" })
{
    var path = Path.Combine(configDir, db);
    if (!File.Exists(path)) { Console.WriteLine($"{db}: 不存在"); continue; }
    using var c = new SqliteConnection($"Data Source={path}");
    c.Open();
    var table = db switch
    {
        "production_logs.db" => "ProductionLogs",
        "alarm_events.db" => "AlarmEvents",
        "status_transitions.db" => "StatusTransitions",
        "work_orders.db" => "WorkOrders",
        _ => ""
    };
    if (string.IsNullOrEmpty(table)) continue;
    try
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        var count = (long)cmd.ExecuteScalar()!;
        Console.WriteLine($"{db} [{table}]: {count} 行");
        if (count > 0)
        {
            cmd.CommandText = $"SELECT MIN(rowid), MAX(rowid) FROM {table}";
            using var r = cmd.ExecuteReader();
            r.Read();
            Console.WriteLine($"  rowid 范围: {r.GetInt64(0)} - {r.GetInt64(1)}");
        }
    }
    catch (Exception ex) { Console.WriteLine($"{db}: {ex.Message}"); }
}
