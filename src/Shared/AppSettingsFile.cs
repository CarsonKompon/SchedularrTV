using System.Text.Json;
using System.Text.Json.Nodes;

namespace SchedularrTV.Hosting;

/// <summary>
/// The user's <c>settings.json</c> in the data folder: connection details and keys entered in the app,
/// laid out like <c>appsettings.json</c> (<c>Jellyfin:BaseUrl</c> is <c>{"Jellyfin":{"BaseUrl":…}}</c>)
/// so the server reads it as an ordinary configuration source.
/// </summary>
public sealed class AppSettingsFile(string path)
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public string Path { get; } = path;

    public JsonObject Load()
    {
        try
        {
            return File.Exists(Path) && JsonNode.Parse(File.ReadAllText(Path)) is JsonObject root ? root : new JsonObject();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new JsonObject();
        }
    }

    /// <summary>Written to a temporary file first, so a crash mid-save can't leave half a file.</summary>
    public void Save(JsonObject root)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string temp = Path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(Indented));
        File.Move(temp, Path, overwrite: true);
    }

    /// <summary>The node at a colon-separated key, or null.</summary>
    public static JsonNode? Get(JsonObject root, string key)
    {
        JsonNode? node = root;
        foreach (string part in key.Split(':'))
        {
            node = node is JsonObject obj && obj.TryGetPropertyValue(part, out JsonNode? child) ? child : null;
            if (node is null)
            {
                return null;
            }
        }

        return node;
    }

    public static string? GetString(JsonObject root, string key) =>
        Get(root, key) is JsonValue value && value.TryGetValue(out string? text) ? text : Get(root, key)?.ToString();

    public static int? GetInt(JsonObject root, string key) =>
        Get(root, key) is JsonValue value && value.TryGetValue(out int number) ? number
        : int.TryParse(GetString(root, key), out int parsed) ? parsed : null;

    public static bool? GetBool(JsonObject root, string key) =>
        Get(root, key) is JsonValue value && value.TryGetValue(out bool flag) ? flag
        : bool.TryParse(GetString(root, key), out bool parsed) ? parsed : null;

    /// <summary>Sets a colon-separated key, creating sections as needed; null removes it.</summary>
    public static void Set(JsonObject root, string key, JsonNode? value)
    {
        string[] parts = key.Split(':');
        JsonObject section = root;
        foreach (string part in parts[..^1])
        {
            if (section[part] is not JsonObject child)
            {
                if (value is null)
                {
                    return;
                }

                child = new JsonObject();
                section[part] = child;
            }

            section = child;
        }

        if (value is null)
        {
            section.Remove(parts[^1]);
        }
        else
        {
            section[parts[^1]] = value;
        }
    }

    public static int Port(JsonObject root) => GetInt(root, "Server:Port") is int port and > 0 and < 65536 ? port : AppPaths.DefaultPort;

    public static bool AllowRemote(JsonObject root) => GetBool(root, "Server:AllowRemote") ?? false;
}
