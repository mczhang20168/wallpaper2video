using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Wallpaper2Video.Core;

public static class SceneJson
{
    public static readonly JsonSerializerOptions Output = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public static JsonNode? Value(JsonNode? node) => node is JsonObject obj ? obj["value"] : node;
    public static string Text(JsonNode? node, string fallback = "") => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToJsonString() ?? fallback;
    public static bool Truth(JsonNode? node) => node switch
    {
        null => false,
        JsonObject o => o.Count > 0,
        JsonArray a => a.Count > 0,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        _ => Number(node, 0) != 0
    };
    public static bool False(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && !b;
    public static double Number(JsonNode? node, double fallback = 1)
    {
        bool provided = node != null;
        node = Value(node);
        if (node == null) return provided ? throw new InvalidDataException("参数绑定缺少有效值") : fallback;
        if (node is JsonValue v && v.TryGetValue<bool>(out bool b)) return b ? 1 : 0;
        double number = double.Parse(Text(node), CultureInfo.InvariantCulture);
        if (!double.IsFinite(number)) throw new InvalidDataException("参数必须是有限数");
        return number;
    }
    public static double RequiredNumber(JsonNode? node) => node == null ? throw new InvalidDataException("缺少必需的数字参数") : Number(node);
    public static JsonArray Array(JsonNode? node) => node as JsonArray ?? new JsonArray();
    public static JsonNode? At(JsonNode? root, IReadOnlyList<string> path)
    {
        foreach (string key in path) root = root is JsonArray a ? a[int.Parse(key, CultureInfo.InvariantCulture)] : root?[key];
        return root;
    }
    public static void Set(JsonNode root, IReadOnlyList<string> path, JsonNode? value)
    {
        if (path.Count == 0) throw new InvalidDataException("不能替换场景根节点");
        var parent = At(root, path.Take(path.Count - 1).ToArray()) ?? throw new InvalidDataException("找不到场景绑定");
        if (parent is JsonArray a) a[int.Parse(path[^1], CultureInfo.InvariantCulture)] = value;
        else parent[path[^1]] = value;
    }
    public static bool Prefix(IReadOnlyList<string> prefix, IReadOnlyList<string> path) => prefix.Count <= path.Count && prefix.SequenceEqual(path.Take(prefix.Count));
    public static string Key(IReadOnlyList<string> path) => string.Join('/', path);
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static JsonObject Parse(string source)
    {
        using var document = JsonDocument.Parse(source, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        JsonNode? Convert(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var result = new JsonObject();
                // Author metadata can repeat keys. Python json.loads keeps the last value.
                foreach (var property in value.EnumerateObject()) result[property.Name] = Convert(property.Value);
                return result;
            }
            if (value.ValueKind == JsonValueKind.Array) return new JsonArray(value.EnumerateArray().Select(Convert).ToArray());
            return JsonNode.Parse(value.GetRawText());
        }
        return Convert(document.RootElement) as JsonObject ?? throw new InvalidDataException("素材定义顶层应为对象");
    }
    public static string CodeOnly(string source) => Regex.Replace(source, "//[^\\n]*|/\\*[\\s\\S]*?\\*/|'(?:\\\\.|[^'\\\\])*'|\"(?:\\\\.|[^\"\\\\])*\"",
        m => new string('\n', m.Value.Count(c => c == '\n')) + " ");
    public static string? Restricted(string source)
    {
        string code = CodeOnly(source);
        if (Regex.IsMatch(code, @"\bDate\b")) return "脚本读取系统日期时间";
        if (Regex.Matches(code, @"\b[A-Za-z_$][\w$]*\b").Any(m => Regex.IsMatch(m.Value, "cursor|pointer|mouse", RegexOptions.IgnoreCase))) return "脚本使用鼠标、指针或光标事件";
        if (Regex.IsMatch(code, @"\b(?:keyboard|onKeyDown|onKeyUp|keyDown|keyUp|keyPress|keyCode|KeyboardEvent)\b", RegexOptions.IgnoreCase)) return "脚本使用键盘交互输入";
        if (Regex.IsMatch(code, @"\b(?:eval|Function|globalThis|Reflect|Proxy)\b")) return "动态生成代码，无法验证鼠标和日期时间排除规则";
        if (Regex.Matches(source, "\\b(?:from|import)\\s*['\"]([^'\"]+)['\"]").Any(m => m.Groups[1].Value is not ("WEMath" or "WEColor" or "WEVector"))) return "脚本导入尚未核对，无法验证鼠标和日期时间排除规则";
        return null;
    }
    public static bool Dynamic(JsonNode? node) => node is JsonObject o ? Truth(o["script"]) || Truth(o["animation"]) || o.Any(p => Dynamic(p.Value)) : node is JsonArray a && a.Any(Dynamic);
    public static bool HasScript(JsonNode? node) => node is JsonObject o ? Truth(o["script"]) || o.Any(p => HasScript(p.Value)) : node is JsonArray a && a.Any(HasScript);
    public static string Glsl(double value)
    {
        if (!double.IsFinite(value)) throw new InvalidDataException("shader常量必须为有限数");
        string text = value.ToString("G17", CultureInfo.InvariantCulture).ToLowerInvariant();
        return text.IndexOfAny(['.', 'e']) >= 0 ? text : text + ".0";
    }
    // Python json.dumps(files, sort_keys=True), including its exact ASCII escaping and separators.
    public static string PythonStringMap(IReadOnlyDictionary<string, string> files)
    {
        static string Quote(string value)
        {
            var b = new StringBuilder("\"");
            foreach (char c in value) b.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when c < 32 || c > 126 => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                _ => c.ToString()
            });
            return b.Append('"').ToString();
        }
        return "{" + string.Join(", ", files.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => Quote(p.Key) + ": " + Quote(p.Value))) + "}";
    }
}
