using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using CastReceiver.Core.AirPlay.Plist;

// 与 Python plistlib 交叉验证二进制 plist 实现。
//   plist-verify read  <cases.json> <bindir>   —— 解析 plistlib 写的 plist，比对期望值
//   plist-verify write <cases.json> <bindir>   —— 写出 case_N.out.bplist 供 plistlib 校验
if (args.Length < 3)
{
    Console.Error.WriteLine("usage: plist-verify <read|write> <cases.json> <bindir>");
    return 2;
}

string mode = args[0];
var expected = JsonNode.Parse(File.ReadAllText(args[1]))!.AsArray();
string bindir = args[2];

int pass = 0, fail = 0;

if (mode == "read")
{
    for (int i = 0; i < expected.Count; i++)
    {
        string path = Path.Combine(bindir, $"case_{i}.bplist");
        try
        {
            object? parsed = BinaryPlist.Read(File.ReadAllBytes(path));
            JsonNode? actual = Tag(parsed);
            string? diff = Compare(expected[i], actual, $"case_{i}");
            if (diff is null)
            {
                pass++;
                Console.WriteLine($"ok   case_{i}");
            }
            else
            {
                fail++;
                Console.WriteLine($"FAIL {diff}");
            }
        }
        catch (Exception ex)
        {
            fail++;
            Console.WriteLine($"FAIL case_{i}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
else
{
    Directory.CreateDirectory(bindir);
    for (int i = 0; i < expected.Count; i++)
    {
        object? value = Untag(expected[i]);
        byte[] bytes = BinaryPlist.Write(value);
        File.WriteAllBytes(Path.Combine(bindir, $"case_{i}.out.bplist"), bytes);
        pass++;
    }
    Console.WriteLine($"wrote {pass} plists to {bindir} for plistlib to verify");
}

Console.WriteLine();
Console.WriteLine($"==== {mode}: pass={pass} fail={fail} ====");
return fail == 0 ? 0 : 1;

// ---------------------------------------------------------------- tagged <-> CLR

static JsonNode? Tag(object? v)
{
    switch (v)
    {
        case null:
            return null;
        case bool b:
            return JsonValue.Create(b);
        case long l:
            return new JsonObject { ["i"] = l };
        case ulong u:
            return new JsonObject { ["i"] = JsonValue.Create(u) };
        case double d:
            return new JsonObject { ["r"] = d };
        case string s:
            return new JsonObject { ["s"] = s };
        case byte[] bytes:
            return new JsonObject { ["d"] = Convert.ToHexString(bytes).ToLowerInvariant() };
        case List<object?> list:
        {
            var arr = new JsonArray();
            foreach (object? item in list)
            {
                arr.Add(Tag(item));
            }
            return new JsonObject { ["a"] = arr };
        }
        case Dictionary<string, object?> dict:
        {
            var obj = new JsonObject();
            foreach (var pair in dict)
            {
                obj[pair.Key] = Tag(pair.Value);
            }
            return new JsonObject { ["o"] = obj };
        }
        default:
            throw new NotSupportedException($"plist 值类型未知：{v.GetType()}");
    }
}

static object? Untag(JsonNode? n)
{
    if (n is null)
    {
        return null;
    }
    if (n is JsonValue v && v.TryGetValue(out bool b))
    {
        return b;
    }

    JsonObject o = n.AsObject();
    if (o.TryGetPropertyValue("i", out var i))
    {
        // 用 BigInteger 兜住超出 long 范围的无符号整数
        var bi = BigInteger.Parse(i!.ToJsonString());
        return bi >= long.MinValue && bi <= long.MaxValue ? (long)bi : (ulong)bi;
    }
    if (o.TryGetPropertyValue("r", out var r))
    {
        return r!.GetValue<double>();
    }
    if (o.TryGetPropertyValue("s", out var s))
    {
        return s!.GetValue<string>();
    }
    if (o.TryGetPropertyValue("d", out var d))
    {
        return Convert.FromHexString(d!.GetValue<string>());
    }
    if (o.TryGetPropertyValue("a", out var a))
    {
        var list = new List<object?>();
        foreach (JsonNode? item in a!.AsArray())
        {
            list.Add(Untag(item));
        }
        return list;
    }
    if (o.TryGetPropertyValue("o", out var od))
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in od!.AsObject())
        {
            dict[pair.Key] = Untag(pair.Value);
        }
        return dict;
    }
    throw new InvalidOperationException("tagged 对象类型未知");
}

// ---------------------------------------------------------------- comparison
//
// 注意：tagged 表示里 {"o": {...}} 内层的键是**用户键**，不能再当成 tag 解释，
// 否则字典里恰好有个名为 "a"/"i"/"s" 的键时会被误判（case_1 就是这么暴露的）。

static string? Compare(JsonNode? a, JsonNode? b, string path)
{
    if (a is null && b is null)
    {
        return null;
    }
    if (a is null || b is null)
    {
        return $"{path}: 一方为 null（{Describe(a)} vs {Describe(b)}）";
    }

    if (a is JsonValue av && b is JsonValue bv && av.TryGetValue(out bool ab) && bv.TryGetValue(out bool bb))
    {
        return ab == bb ? null : $"{path}: bool {ab} != {bb}";
    }

    JsonObject ao = a.AsObject();
    JsonObject bo = b.AsObject();
    if (ao.Count != bo.Count)
    {
        return $"{path}: 标签数不同 ({ao.Count} vs {bo.Count})";
    }

    foreach (var pair in ao)
    {
        if (!bo.TryGetPropertyValue(pair.Key, out JsonNode? other))
        {
            return $"{path}: 缺少标签 {pair.Key}";
        }

        switch (pair.Key)
        {
            case "i":
            {
                // 用 BigInteger 比：u64 上限（如 streamConnectionID）超出 long 范围，
                // JsonNode 会把它解析成"Number"而不是 Int64。
                if (BigInteger.Parse(pair.Value!.ToJsonString()) != BigInteger.Parse(other!.ToJsonString()))
                {
                    return $"{path}: int {pair.Value.ToJsonString()} != {other.ToJsonString()}";
                }
                break;
            }

            case "r":
            {
                double x = pair.Value!.GetValue<double>(), y = other!.GetValue<double>();
                if (Math.Abs(x - y) > 1e-12 * Math.Max(1.0, Math.Max(Math.Abs(x), Math.Abs(y))))
                {
                    return $"{path}: real {x:R} != {y:R}";
                }
                break;
            }

            case "s":
            case "d":
            {
                string x = pair.Value!.GetValue<string>(), y = other!.GetValue<string>();
                if (!string.Equals(x, y, StringComparison.Ordinal))
                {
                    return $"{path}.{pair.Key}: 值不同";
                }
                break;
            }

            case "a":
            {
                var xa = pair.Value!.AsArray();
                var ya = other!.AsArray();
                if (xa.Count != ya.Count)
                {
                    return $"{path}.a: 长度 {xa.Count} != {ya.Count}";
                }
                for (int i = 0; i < xa.Count; i++)
                {
                    string? diff = Compare(xa[i], ya[i], $"{path}[{i}]");
                    if (diff is not null)
                    {
                        return diff;
                    }
                }
                break;
            }

            case "o":
            {
                var xo = pair.Value!.AsObject();
                var yo = other!.AsObject();
                if (xo.Count != yo.Count)
                {
                    return $"{path}: 字典键数 {xo.Count} != {yo.Count}";
                }
                foreach (var kv in xo)
                {
                    if (!yo.TryGetPropertyValue(kv.Key, out JsonNode? ov))
                    {
                        return $"{path}: 缺少键 {kv.Key}";
                    }
                    string? diff = Compare(kv.Value, ov, $"{path}.{kv.Key}");
                    if (diff is not null)
                    {
                        return diff;
                    }
                }
                break;
            }

            default:
                return $"{path}: 未知标签 {pair.Key}";
        }
    }
    return null;
}

static string Describe(JsonNode? n) => n is null ? "null" : n.ToJsonString();
