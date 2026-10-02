using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MainAPP.Services;

public interface IAssistantQuestionTally
{
    void Record(string question);

    /// <summary>问到第三次及以后的问题，最近问的排在前面，最多三条。</summary>
    IReadOnlyList<string> Repeated();
}

/// <summary>同一句问题在这台电脑上的次数。只写本机文件，不进审计日志。</summary>
public sealed class AssistantQuestionTally : IAssistantQuestionTally
{
    public const int RepeatCount = 3;
    public const int MaxTracked = 40;
    public const int MaxShown = 3;

    private readonly string _path;
    private readonly object _gate = new();

    public AssistantQuestionTally()
        : this(DefaultPath())
    {
    }

    public AssistantQuestionTally(string path) => _path = path;

    public static string DefaultPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kanban",
            "assistant-questions.json");

    public void Record(string question)
    {
        var text = Normalize(question);
        if (text.Length == 0)
            return;
        try
        {
            lock (_gate)
            {
                var items = Load();
                var existing = items.FirstOrDefault(item => string.Equals(item.Text, text, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    items.Add(new Item { Text = text, Count = 1, LastUtc = DateTime.UtcNow });
                }
                else
                {
                    existing.Text = text;
                    existing.Count++;
                    existing.LastUtc = DateTime.UtcNow;
                }

                while (items.Count > MaxTracked)
                {
                    var victim = items.OrderBy(item => item.Count).ThenBy(item => item.LastUtc).First();
                    items.Remove(victim);
                }

                Save(items);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (JsonException)
        {
        }
    }

    public IReadOnlyList<string> Repeated()
    {
        try
        {
            lock (_gate)
            {
                return Load()
                    .Where(item => item.Count >= RepeatCount && item.Text.Length > 0)
                    .OrderByDescending(item => item.LastUtc)
                    .Take(MaxShown)
                    .Select(item => item.Text)
                    .ToList();
            }
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private List<Item> Load()
    {
        if (!File.Exists(_path))
            return [];
        var json = File.ReadAllText(_path);
        return JsonSerializer.Deserialize<List<Item>>(json) ?? [];
    }

    private void Save(List<Item> items)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(items));
        File.Move(temp, _path, overwrite: true);
    }

    private static string Normalize(string question)
    {
        var builder = new StringBuilder(question.Length);
        var pendingSpace = false;
        foreach (var ch in question.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
            if (builder.Length >= 800)
                break;
        }

        return builder.ToString();
    }

    private sealed class Item
    {
        public string Text { get; set; } = "";

        public int Count { get; set; }

        public DateTime LastUtc { get; set; }
    }
}
