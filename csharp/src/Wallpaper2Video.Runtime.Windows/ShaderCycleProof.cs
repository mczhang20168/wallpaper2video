using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Wallpaper2Video.Core.SceneJson;

namespace Wallpaper2Video.Runtime.Windows;

// This verifier handles straight-line vertex/fragment expressions. Every unwrapped
// time value must reach a recognized periodic operation before it can affect output.
internal static partial class ShaderCycleProof
{
    internal sealed record Result(double? Period, double[] Components, string[] Members);
    private sealed record Helper(string[] Parameters, string Body);
    private readonly record struct Clock(double Period, bool Angular);
    private sealed record Signal(double[]? Constant = null, double[]? Rate = null, Clock[]? Clocks = null)
    {
        public Clock[] Periods => Clocks ?? [];
        public bool Affine => Rate != null && Rate.Any(rate => rate != 0);
    }
    private static readonly Signal Unknown = new();
    private sealed class Unverified : Exception { }
    private static Signal Demand(bool condition, Signal value) => condition ? value : throw new Unverified();
    private static Signal Periodic(IEnumerable<Signal> list)
    {
        var signals = list.ToArray();
        return Demand(signals.All(s => !s.Affine), new(Clocks: signals.SelectMany(s => s.Periods).Distinct().ToArray()));
    }

    public static Result? Analyze(AssetBundle bundle, EffectGraph graph, JsonNode effect)
    {
        try
        {
            var definition = Parse(graph.Files[Text(effect["file"])]);
            var effectPasses = Array(definition["passes"]);
            if (effectPasses.Count != 1 || definition["targets"] != null || effectPasses[0]?["target"] != null) return null;
            var material = Array(Parse(graph.Files[Text(effectPasses[0]?["material"])])["passes"]);
            if (material.Count != 1) return null;
            var instance = Array(effect["passes"]);
            if (instance.Count > 1) return null;
            JsonNode? pass = material[0], overrides = instance.FirstOrDefault();
            string shader = "shaders/" + Text(pass?["shader"]);
            string[] members = [shader + ".vert", shader + ".frag"];
            var defines = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in graph.Combos) defines[key] = Text(Value(value), "0");
            foreach (var option in new[] { pass?["combos"], overrides?["combos"] })
                if (option is JsonObject obj) foreach (var (key, value) in obj)
                    defines[key] = value is JsonObject b && (Dynamic(b) || Truth(b["user"])) ? "?" : Text(Value(value), "0");
            if (graph.Files.Any(kv => kv.Key.EndsWith(".h", StringComparison.Ordinal) && Time().IsMatch(WithoutDeclaration(CodeOnly(kv.Value))))) return null;
            var varying = new Dictionary<string, Signal>(StringComparer.Ordinal);
            var clocks = new List<Clock>();
            foreach (string member in members)
            {
                string source = graph.Files[member];
                var values = new Dictionary<string, Signal>(varying, StringComparer.Ordinal) { ["g_Time"] = new(Rate: [1]) };
                var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
                var textures = new Dictionary<string, string>(StringComparer.Ordinal);
                var constants = new Dictionary<string, string>(defines, StringComparer.Ordinal);
                foreach (Match uniform in Uniform().Matches(source))
                {
                    var metadata = Parse(uniform.Groups["json"].Value);
                    string variable = uniform.Groups["name"].Value;
                    string key = Text(metadata["material"]);
                    if (key.Length > 0 && (overrides?["constantshadervalues"]?[key] ?? pass?["constantshadervalues"]?[key]) is JsonNode setting &&
                        Dynamic(setting))
                        values[variable] = new(Rate: [double.NaN]);
                    if (metadata["combo"] is JsonValue combo && TextureSlot(variable) is int slot)
                        constants.TryAdd(Text(combo), string.IsNullOrEmpty(SelectedTexture(slot, pass, overrides, metadata)) ? "0" : "1");
                }
                string active = Active(source, constants);
                foreach (Match uniform in Uniform().Matches(active))
                {
                    string variable = uniform.Groups["name"].Value;
                    var metadata = Parse(uniform.Groups["json"].Value);
                    string key = Text(metadata["material"]);
                    if (TextureSlot(variable) is int slot) textures[variable] = SelectedTexture(slot, pass, overrides, metadata);
                    var raw = key.Length == 0 ? metadata["default"] : overrides?["constantshadervalues"]?[key] ?? pass?["constantshadervalues"]?[key] ?? metadata["default"];
                    // A user setting is stable while recording but its value isn't a
                    // proven constant for calculating temporal frequency.
                    if (raw != null && !(raw is JsonObject user && Truth(user["user"])) &&
                        !(values.TryGetValue(variable, out var previous) && previous.Affine) &&
                        TryVector(Text(Value(raw)), out var numbers)) values[variable] = new(numbers);
                }
                string code = WithoutDeclaration(CodeOnly(active));
                if (External().IsMatch(code)) return null;
                var main = Main().Match(code);
                if (!main.Success || Time().Matches(code).Count != Time().Matches(main.Groups["body"].Value).Count) return null;
                var helpers = SafeHelpers(graph, code);
                foreach (Match variable in Varying().Matches(code))
                    sizes[variable.Groups["name"].Value] = int.TryParse(variable.Groups["size"].Value, out int size) ? size : 1;
                var parser = new Expression(values, constants, helpers, tex => textures.TryGetValue(tex, out string? texture) && Repeat(bundle, texture));
                var integerVariables = new HashSet<string>(StringComparer.Ordinal);
                foreach (string raw in main.Groups["body"].Value.Split(';'))
                {
                    string statement = raw.Trim();
                    if (statement.Length == 0) continue;
                    var assignment = Assignment().Match(statement);
                    if (!assignment.Success)
                    {
                        var declaration = Declaration().Match(statement);
                        if (!declaration.Success) return null;
                        sizes[declaration.Groups["name"].Value] = int.TryParse(declaration.Groups["size"].Value, out int n) ? n : 1;
                        if (declaration.Groups["type"].Value is "int" or "bool") integerVariables.Add(declaration.Groups["name"].Value);
                        continue;
                    }
                    string target = assignment.Groups["name"].Value, mask = assignment.Groups["mask"].Value;
                    string op = assignment.Groups["op"].Value, rhs = assignment.Groups["rhs"].Value;
                    var value = parser.Parse(rhs);
                    if (op.Length > 1) value = Expression.Binary(op[0], parser.Parse(target + mask), value);
                    if (assignment.Groups["type"].Value is "int" or "bool") integerVariables.Add(target);
                    if (value.Affine && integerVariables.Contains(target)) return null;
                    if (mask.Length > 0)
                    {
                        var former = values.GetValueOrDefault(target, Unknown);
                        int[] channels = Expression.Channels(mask[1..]);
                        int count = sizes.GetValueOrDefault(target, former.Rate?.Length ?? former.Constant?.Length ?? channels.Max() + 1);
                        if (channels.Max() >= count) throw new Unverified();
                        var slopes = Enumerable.Range(0, count).Select(i => Expression.Component(former.Rate, i, 0)).ToArray();
                        for (int index = 0; index < channels.Length; index++) slopes[channels[index]] = Expression.Component(value.Rate, index, 0);
                        value = new(Rate: slopes, Clocks: former.Periods.Concat(value.Periods).Distinct().ToArray());
                    }
                    values[target] = value;
                }
                if (member.EndsWith(".vert", StringComparison.Ordinal))
                {
                    foreach (string name in sizes.Keys.Where(n => Varying().Matches(code).Any(m => m.Groups["name"].Value == n)))
                        if (values.TryGetValue(name, out var value)) varying[name] = value;
                    if (values.TryGetValue("gl_Position", out var geometry))
                    {
                        if (geometry.Affine) return null;
                        clocks.AddRange(geometry.Periods);
                    }
                }
                else if (values.TryGetValue("gl_FragColor", out var color))
                {
                    if (color.Affine) return null;
                    clocks.AddRange(color.Periods);
                }
            }
            Clock[] activeClocks = clocks.Distinct().ToArray();
            if (activeClocks.Length == 0) return null;
            double[] components = activeClocks.Select(c => c.Period).Distinct().Order().ToArray();
            double? shared = null;
            if (activeClocks.All(c => c.Angular == activeClocks[0].Angular))
                for (int n = 1; n <= 256; n++)
                {
                    double candidate = n * components[^1];
                    if (components.All(p => Math.Abs(candidate / p - Math.Round(candidate / p)) < 1e-10))
                    { shared = candidate; break; }
                }
            return new(shared, components, members);
        }
        catch (Exception e) when (e is Unverified or IOException or InvalidDataException or ArgumentException or
            System.Text.Json.JsonException or FormatException or OverflowException or KeyNotFoundException)
        { return null; }
    }

    private static Dictionary<string, Helper> SafeHelpers(EffectGraph graph, string shader)
    {
        var source = string.Join('\n', graph.Files.Where(p => p.Key.EndsWith(".h", StringComparison.Ordinal))
            .Select(p => CodeOnly(p.Value)).Prepend(shader));
        var result = new Dictionary<string, Helper>(StringComparer.Ordinal);
        foreach (var functions in FunctionBody().Matches(source).Cast<Match>().GroupBy(m => m.Groups["name"].Value))
        {
            if (functions.Count() != 1) continue;
            var f = functions.Single();
            string name = f.Groups["name"].Value, body = f.Groups["body"].Value, parameters = f.Groups["parameters"].Value;
            if (name == "main" || body.Contains('#') ||
                Regex.IsMatch(source, @"(?m)^\s*#\s*define\s+" + Regex.Escape(name) + @"\b")) continue;
            string[] names = parameters.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(t => Regex.Match(t, @"^(?:float|vec[234])\s+(\w+)$").Groups[1].Value).ToArray();
            if (names.Length > 0 && names.All(n => n.Length > 0)) result[name] = new(names, body);
        }
        return result;
    }

    private static string SelectedTexture(int index, JsonNode? pass, JsonNode? instance, JsonObject metadata)
    {
        JsonNode? AtSlot(JsonNode? row) => row?["textures"] is JsonArray a && index < a.Count ? a[index] : null;
        return Text(AtSlot(instance) ?? AtSlot(pass) ?? metadata["default"]);
    }
    private static int? TextureSlot(string name) =>
        name.StartsWith("g_Texture", StringComparison.Ordinal) && int.TryParse(name[9..], out int slot) ? slot : null;
    private static bool Repeat(AssetBundle bundle, string texture)
    {
        if (texture.Length == 0 || texture.StartsWith('_') || texture.StartsWith("rt_", StringComparison.OrdinalIgnoreCase)) return false;
        string path = texture.StartsWith("materials/", StringComparison.Ordinal) ? texture : "materials/" + texture;
        if (!path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)) path += ".tex";
        var (header, _) = bundle.ReadRange(path, 0, 42);
        return header.Length == 42 && header.AsSpan(0, 18).SequenceEqual("TEXV0005\0TEXI0001\0"u8) &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(22, 4)) == 0 &&
            TextureTiming.ReadTail(bundle.ReadTail(path)) == null;
    }
    private static string WithoutDeclaration(string code) => TimeDeclaration().Replace(code, "");

    private static string Active(string source, Dictionary<string, string> values)
    {
        var result = new StringBuilder();
        var conditions = new Stack<(bool Parent, bool Seen)>();
        bool active = true;
        double CompareValue(string term)
        {
            term = term.Trim();
            if (TryVector(values.GetValueOrDefault(term, term), out var n) && n.Length == 1) return n[0];
            // An absent shader combo is undefined in the preprocessor.
            if (!values.ContainsKey(term) && Regex.IsMatch(term, @"^[A-Za-z_]\w*$")) return 0;
            throw new Unverified();
        }
        bool Flag(string expression)
        {
            if (expression.Contains("||", StringComparison.Ordinal)) return expression.Split("||").Any(Flag);
            if (expression.Contains("&&", StringComparison.Ordinal)) return expression.Split("&&").All(Flag);
            expression = expression.Trim();
            var defined = Regex.Match(expression, @"^defined\s*\(\s*(\w+)\s*\)$");
            if (defined.Success) return values.ContainsKey(defined.Groups[1].Value);
            var m = Regex.Match(expression, @"^(\w+)\s*(==|!=|>=|<=|>|<)\s*(\w+)$");
            if (m.Success)
            {
                double a = CompareValue(m.Groups[1].Value), b = CompareValue(m.Groups[3].Value);
                return m.Groups[2].Value switch { "==" => a == b, "!=" => a != b, ">=" => a >= b, "<=" => a <= b, ">" => a > b, _ => a < b };
            }
            return expression.StartsWith('!') ? !Flag(expression[1..]) : CompareValue(expression) != 0;
        }
        foreach (string line in source.Split('\n'))
        {
            string nonComment = line.Split("//", 2, StringSplitOptions.None)[0].Trim();
            if (!nonComment.StartsWith('#')) { if (active) result.AppendLine(line); continue; }
            var directive = Regex.Match(nonComment, @"^#\s*(\w+)\s*(.*)$");
            string name = directive.Groups[1].Value, argument = directive.Groups[2].Value;
            if (name is "if" or "ifdef" or "ifndef")
            {
                bool on = active && (name == "if" ? Flag(argument) : values.ContainsKey(argument) == (name == "ifdef"));
                conditions.Push((active, on)); active = on;
            }
            else if (name is "elif" or "else")
            {
                if (!conditions.TryPop(out var prior)) throw new Unverified();
                active = prior.Parent && !prior.Seen && (name == "else" || Flag(argument));
                conditions.Push((prior.Parent, prior.Seen || active));
            }
            else if (name == "endif")
            {
                if (!conditions.TryPop(out var prior)) throw new Unverified();
                active = prior.Parent;
            }
            else if (active && name == "define")
            {
                var define = Regex.Match(argument, @"^(\w+)(?:\s+(.*))?$");
                if (define.Success) values[define.Groups[1].Value] = define.Groups[2].Success ? define.Groups[2].Value : "1";
            }
        }
        if (conditions.Count != 0) throw new Unverified();
        return result.ToString();
    }

    private static bool TryVector(string value, out double[] vector)
    {
        string[] parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        vector = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!double.TryParse(parts[i].TrimEnd('f', 'F'), NumberStyles.Float, CultureInfo.InvariantCulture, out vector[i]) || !double.IsFinite(vector[i])) return false;
        return parts.Length > 0;
    }
    private sealed class Expression(Dictionary<string, Signal> signals, Dictionary<string, string> defines,
        Dictionary<string, Helper> helpers, Func<string, bool> repeat)
    {
        public static int[] Channels(string mask) => mask.Select(c => c switch { 'r' or 'x' => 0, 'g' or 'y' => 1, 'b' or 'z' => 2, 'a' or 'w' => 3, _ => throw new Unverified() }).ToArray();
        public static double Component(double[]? values, int index, double fallback) =>
            values == null ? fallback : values.Length == 1 ? values[0] : index < values.Length ? values[index] : throw new Unverified();
        public static Signal Binary(char op, Signal a, Signal b)
        {
            int count = new[] { a.Constant?.Length ?? 0, a.Rate?.Length ?? 0, b.Constant?.Length ?? 0, b.Rate?.Length ?? 0 }.Max();
            if (op == '*' && !a.Affine && !b.Affine &&
                (a.Constant?.All(n => n == 0) == true || b.Constant?.All(n => n == 0) == true))
                return new(new double[Math.Max(1, count)]);
            if (a.Constant != null && b.Constant != null)
                return new(Enumerable.Range(0, count).Select(i => Numeric(op, Component(a.Constant, i, 0), Component(b.Constant, i, 0))).ToArray());
            if (!a.Affine && !b.Affine) return Periodic([a, b]);
            if (a.Periods.Length > 0 || b.Periods.Length > 0) throw new Unverified();
            return op switch
            {
                '+' or '-' => new(Rate: Enumerable.Range(0, count).Select(i => Numeric(op, Component(a.Rate, i, 0), Component(b.Rate, i, 0))).ToArray()),
                '*' when a.Constant != null || b.Constant != null => new(Rate: Enumerable.Range(0, count).Select(i => Component(a.Affine ? a.Rate : b.Rate, i, 0) * Component(a.Constant ?? b.Constant, i, 0)).ToArray()),
                '/' when b.Constant != null => new(Rate: Enumerable.Range(0, count).Select(i => Component(a.Rate, i, 0) / Component(b.Constant, i, 0)).ToArray()),
                _ => throw new Unverified()
            };
        }
        private static double Numeric(char op, double a, double b) => op switch { '+' => a + b, '-' => a - b, '*' => a * b, '/' => a / b, _ => throw new Unverified() };
        private static string[] Split(string input, char delimiter)
        {
            int depth = 0, start = 0;
            var result = new List<string>();
            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c is '(' or '[') depth++;
                else if (c is ')' or ']') depth--;
                else if (c == delimiter && depth == 0) { result.Add(input[start..i]); start = i + 1; }
            }
            result.Add(input[start..]); return result.ToArray();
        }
        private static int Operator(string input, string operators)
        {
            int depth = 0;
            for (int i = input.Length - 1; i > 0; i--)
            {
                char c = input[i];
                if (c is ')' or ']') depth++;
                else if (c is '(' or '[') depth--;
                else if (depth == 0 && operators.Contains(c))
                {
                    int before = i - 1;
                    while (before >= 0 && char.IsWhiteSpace(input[before])) before--;
                    if (before >= 0 && ("+-*/(,".Contains(input[before]) ||
                        ((input[before] is 'e' or 'E') && before > 0 && char.IsAsciiDigit(input[before - 1])))) continue;
                    return i;
                }
            }
            return -1;
        }
        private static bool Enclosed(string input)
        {
            if (!input.StartsWith('(') || !input.EndsWith(')')) return false;
            int depth = 0;
            for (int i = 0; i < input.Length - 1; i++)
            {
                if (input[i] == '(') depth++;
                if (input[i] == ')' && --depth == 0) return false;
            }
            return true;
        }
        private static Signal Gate(Signal signal, double span, bool angular)
        {
            if (!signal.Affine) return Periodic([signal]);
            if (!double.IsFinite(span) || span <= 0) throw new Unverified();
            var periods = signal.Rate!.Where(r => r != 0).Select(r => span / Math.Abs(r)).ToArray();
            if (periods.Any(p => !double.IsFinite(p) || p <= 0)) throw new Unverified();
            return new(Clocks: signal.Periods.Concat(periods.Select(p => new Clock(p, angular))).Distinct().ToArray());
        }
        public Signal Parse(string raw, int level = 0)
        {
            if (level > 64) throw new Unverified();
            string input = raw.Trim();
            Signal Next(string text) => Parse(text, level + 1);
            if (TryVector(input, out var n)) return new(n);
            if (signals.TryGetValue(input, out var signal)) return signal;
            if (defines.TryGetValue(input, out var macro) && macro != input) return Next(macro);
            if (Enclosed(input)) return Next(input[1..^1]);
            int op = Operator(input, "+-");
            if (op < 0) op = Operator(input, "*/");
            if (op > 0) return Binary(input[op], Next(input[..op]), Next(input[(op + 1)..]));
            if (input.StartsWith('-')) return Binary('*', new([-1]), Next(input[1..]));
            if (input.StartsWith('+')) return Next(input[1..]);
            var member = Regex.Match(input, @"^(.*)\.([xyzwrgba]{1,4})$", RegexOptions.Singleline);
            if (member.Success)
            {
                var target = Next(member.Groups[1].Value);
                int[] indices = Channels(member.Groups[2].Value);
                return new(target.Constant == null ? null : indices.Select(i => Component(target.Constant, i, 0)).ToArray(),
                    target.Rate == null ? null : indices.Select(i => Component(target.Rate, i, 0)).ToArray(), target.Periods);
            }
            var call = Regex.Match(input, @"^(\w+)\s*(\(.*\))$", RegexOptions.Singleline);
            if (call.Success && Enclosed(call.Groups[2].Value))
            {
                string name = call.Groups[1].Value;
                string[] args = Split(call.Groups[2].Value[1..^1], ',');
                var arguments = args.Select(Next).ToArray();
                if (args.Length == 1 && name is "sin" or "cos") return Gate(arguments[0], 2 * Math.PI, true);
                if (args.Length == 1 && name is "frac" or "fract") return Gate(arguments[0], 1, false);
                if (args.Length == 2 && (name == "mod" || name == "fmod" && args[0].Trim() == "g_Time") &&
                    !defines.ContainsKey(name) && arguments[1].Constant is [double span])
                    return Gate(arguments[0], Math.Abs(span), false);
                if (args.Length >= 2 && name is "texSample2D" or "texSample2DLod" or "texture" or "textureLod" && arguments[1].Affine)
                {
                    if (!repeat(args[0].Trim()) || arguments.Where((a, i) => i != 1).Any(a => a.Affine)) throw new Unverified();
                    arguments[1] = Gate(arguments[1], 1, false);
                }
                if (Regex.IsMatch(name, @"^(?:vec|CAST)[234]$"))
                {
                    int width = name[^1] - '0';
                    if (arguments.All(a => a.Constant != null))
                    {
                        var vector = arguments.SelectMany(a => a.Constant!).ToArray();
                        if (vector.Length == 1) vector = Enumerable.Repeat(vector[0], width).ToArray();
                        if (vector.Length == width) return new(vector);
                    }
                    if (arguments.Any(a => a.Affine))
                    {
                        var rates = arguments.SelectMany(a => a.Rate ?? new double[a.Constant?.Length ?? 1]).ToArray();
                        if (rates.Length == 1) rates = Enumerable.Repeat(rates[0], width).ToArray();
                        return Demand(rates.Length == width, new(Rate: rates, Clocks: arguments.SelectMany(a => a.Periods).Distinct().ToArray()));
                    }
                }
                if (name == "sign" && arguments.Length == 1 && arguments[0].Constant is double[] numbers)
                    return new(numbers.Select(v => (double)Math.Sign(v)).ToArray());
                if (name == "pow" && arguments.Length == 2 && arguments.All(a => a.Constant != null))
                    return new(Enumerable.Range(0, Math.Max(arguments[0].Constant!.Length, arguments[1].Constant!.Length))
                        .Select(i => Math.Pow(Component(arguments[0].Constant, i, 0), Component(arguments[1].Constant, i, 0))).ToArray());
                if (arguments.Any(a => a.Affine) && helpers.TryGetValue(name, out var helper) && args.Length == helper.Parameters.Length)
                    return EvaluateHelper(helper, arguments, level);
                return Periodic(arguments);
            }
            return Periodic(Identifiers().Matches(input).Select(token => signals.GetValueOrDefault(token.Value, Unknown)));
        }
        private Signal EvaluateHelper(Helper helper, Signal[] arguments, int level)
        {
            var locals = helper.Parameters.Zip(arguments).ToDictionary(p => p.First, p => p.Second, StringComparer.Ordinal);
            var nested = new Expression(locals, defines, helpers, repeat);
            string[] statements = helper.Body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (statements.Length == 0 || !statements[^1].StartsWith("return ", StringComparison.Ordinal)) throw new Unverified();
            foreach (string statement in statements[..^1])
            {
                var item = Assignment().Match(statement);
                if (!item.Success || item.Groups["type"].Value is not ("float" or "vec2" or "vec3" or "vec4") ||
                    item.Groups["mask"].Length != 0 || item.Groups["op"].Value != "=") throw new Unverified();
                var value = nested.Parse(item.Groups["rhs"].Value, level + 1);
                if (!locals.TryAdd(item.Groups["name"].Value, value)) throw new Unverified();
            }
            return nested.Parse(statements[^1]["return ".Length..], level + 1);
        }
    }

    [GeneratedRegex(@"\buniform\s+\w+\s+(?<name>\w+)\s*;\s*//\s*(?<json>\{[^\r\n]*\})")]
    private static partial Regex Uniform();
    [GeneratedRegex(@"\buniform\s+float\s+g_Time\s*;")]
    private static partial Regex TimeDeclaration();
    [GeneratedRegex(@"\bg_Time\b")]
    private static partial Regex Time();
    [GeneratedRegex(@"\bg_(?:Audio|Pointer|Mouse|Parallax|Date|TimeOfDay|TimeDelta|DeltaTime|FrameIndex)\w*\b", RegexOptions.IgnoreCase)]
    private static partial Regex External();
    [GeneratedRegex(@"\bvoid\s+main\s*\([^)]*\)\s*\{(?<body>[^{}]*)\}")]
    private static partial Regex Main();
    [GeneratedRegex(@"\bvarying\s+(?:vec(?<size>[234])|\w+)\s+(?<name>\w+)\s*;")]
    private static partial Regex Varying();
    [GeneratedRegex(@"^(?:(?<type>float|int|bool|vec[234]|mat[234])\s+)?(?<name>\w+)(?<mask>\.[xyzwrgba]{1,4})?\s*(?<op>[+\-*/]?=)\s*(?<rhs>.*)$", RegexOptions.Singleline)]
    private static partial Regex Assignment();
    [GeneratedRegex(@"^(?<type>vec(?<size>[234])|float|int|bool)\s+(?<name>\w+)$")]
    private static partial Regex Declaration();
    [GeneratedRegex(@"\b[A-Za-z_]\w*\b")]
    private static partial Regex Identifiers();
    [GeneratedRegex(@"\b(?:float|vec[234])\s+(?<name>\w+)\s*\((?<parameters>[^()]*)\)\s*\{(?<body>[^{}]*)\}")]
    private static partial Regex FunctionBody();
}
