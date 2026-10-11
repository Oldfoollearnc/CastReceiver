using System.Globalization;

namespace CastReceiver.Core.Devices;

/// <summary>
/// 投屏设备的识别结果。
///
/// 数据来源（都是协议里真实带的，不是猜的）：
/// <list type="bullet">
/// <item>AirPlay：SETUP 的 plist 里有 <c>deviceID</c> / <c>model</c> / <c>name</c>，
/// 请求头里有 <c>User-Agent</c>（UxPlay 也是读这几个字段，见 raop_handlers.h:756-759、816）；</item>
/// <item>Miracast：Windows 只给 <c>Transmitter.Name</c> 和 <c>MacAddress</c>，
/// **没有**型号/系统版本，所以安卓侧只能靠名字猜，且只猜品牌级别。</item>
/// </list>
///
/// 原则：<b>认不出来就老实显示原始名字或原始型号代码，绝不编一个可能错的型号。</b>
/// </summary>
public sealed record DeviceIdentity
{
    /// <summary>协议名（"AirPlay" / "无线投屏"）。</summary>
    public required string Protocol { get; init; }

    /// <summary>手机自己报上来的设备名（如「张三的 iPhone」）。</summary>
    public string? Name { get; init; }

    /// <summary>型号代码（如 iPhone14,5 / SM-G998B）。</summary>
    public string? Model { get; init; }

    /// <summary>型号代码映射出的可读名称；映射不到则为 null（此时界面显示原始代码）。</summary>
    public string? FriendlyModel { get; init; }

    /// <summary>品牌（安卓侧通常是按型号前缀推断的，属于猜测）。</summary>
    public string? Brand { get; init; }

    /// <summary>品牌是否只是"按前缀推断"的猜测（用来决定界面是否标注"可能"）。</summary>
    public bool BrandIsGuess { get; init; }

    public string? OsName { get; init; }
    public string? OsVersion { get; init; }
    public string? UserAgent { get; init; }
    public string? DeviceId { get; init; }

    /// <summary>是不是明确认出是苹果设备。</summary>
    public bool IsApple =>
        FriendlyModel is not null
        || (Model?.StartsWith("iPhone", StringComparison.OrdinalIgnoreCase) ?? false)
        || (Model?.StartsWith("iPad", StringComparison.OrdinalIgnoreCase) ?? false)
        || (OsName?.Contains("iOS", StringComparison.OrdinalIgnoreCase) ?? false)
        || (OsName?.Contains("iPadOS", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>是否拿到了型号信息（哪怕只是代码）。</summary>
    public bool HasModelInfo => !string.IsNullOrWhiteSpace(Model);

    /// <summary>
    /// 给人看的一行描述。按"知道多少说多少"的顺序降级，
    /// 全部不知道时也不编，直接说未知。
    /// </summary>
    public string Describe()
    {
        string? system = SystemLabel();

        if (!string.IsNullOrWhiteSpace(FriendlyModel))
        {
            return system is null ? FriendlyModel! : $"{FriendlyModel} · {system}";
        }

        if (!string.IsNullOrWhiteSpace(Brand) && !string.IsNullOrWhiteSpace(Model))
        {
            // 品牌是猜的就标出来，别让人以为是精确型号
            string tail = BrandIsGuess ? $"{Brand}（型号 {Model}）" : $"{Brand} {Model}";
            return system is null ? tail : $"{tail} · {system}";
        }

        if (!string.IsNullOrWhiteSpace(Model))
        {
            return system is null ? Model! : $"{Model} · {system}";
        }

        if (!string.IsNullOrWhiteSpace(Name))
        {
            return Name!;
        }

        // 型号和名字都没有，但至少知道系统（例如只拿到 osName="iPadOS"）：
        // 这仍然是真实信息，比一句"未知设备"有用。
        if (system is not null)
        {
            return system;
        }

        return "未知设备";
    }

    /// <summary>系统名 + 版本（拿不到版本就只给系统名）。</summary>
    public string? SystemLabel()
    {
        string? os = NormalizeOsName(OsName);
        bool hasVersion = !string.IsNullOrWhiteSpace(OsVersion);

        if (os is null && !hasVersion)
        {
            return null;
        }

        if (os is null)
        {
            return "系统 " + OsVersion;
        }

        return hasVersion ? $"{os} {OsVersion}" : os;
    }

    /// <summary>小窗/状态条这种窄地方用的短标签。</summary>
    public string ShortLabel()
    {
        if (!string.IsNullOrWhiteSpace(FriendlyModel))
        {
            return FriendlyModel!;
        }
        if (!string.IsNullOrWhiteSpace(Name))
        {
            return Name!.Length <= 14 ? Name! : Name![..14] + "…";
        }
        if (!string.IsNullOrWhiteSpace(Brand))
        {
            return Brand!;
        }
        return Protocol;
    }

    private static string? NormalizeOsName(string? osName)
    {
        if (string.IsNullOrWhiteSpace(osName))
        {
            return null;
        }

        return osName.Trim().ToLowerInvariant() switch
        {
            "ios" => "iOS",
            "ipados" => "iPadOS",
            "macos" or "mac os x" => "macOS",
            "android" => "Android",
            "windows" => "Windows",
            _ => osName.Trim(),
        };
    }
}

/// <summary>型号代码 → 可读名称。表是**刻意不完整**的：识别不到就返回 null，让界面显示原始代码。</summary>
public static class DeviceModelCatalog
{
    /// <summary>
    /// 苹果型号标识。iPhone 部分来自公开的标识符清单（adamawolf/3048717）并与已知机型交叉核对过。
    /// iPad Pro 那几个在公开清单里互相冲突（同一代号被标成两代），
    /// 所以这里**故意不收** —— 宁可显示 iPad13,4 也不显示一个可能是错的代次。
    /// </summary>
    private static readonly Dictionary<string, string> Apple = new(StringComparer.OrdinalIgnoreCase)
    {
        // ---- iPhone
        ["iPhone10,1"] = "iPhone 8",
        ["iPhone10,2"] = "iPhone 8 Plus",
        ["iPhone10,3"] = "iPhone X",
        ["iPhone10,4"] = "iPhone 8",
        ["iPhone10,5"] = "iPhone 8 Plus",
        ["iPhone10,6"] = "iPhone X",
        ["iPhone11,2"] = "iPhone XS",
        ["iPhone11,4"] = "iPhone XS Max",
        ["iPhone11,6"] = "iPhone XS Max",
        ["iPhone11,8"] = "iPhone XR",
        ["iPhone12,1"] = "iPhone 11",
        ["iPhone12,3"] = "iPhone 11 Pro",
        ["iPhone12,5"] = "iPhone 11 Pro Max",
        ["iPhone12,8"] = "iPhone SE（第 2 代）",
        ["iPhone13,1"] = "iPhone 12 mini",
        ["iPhone13,2"] = "iPhone 12",
        ["iPhone13,3"] = "iPhone 12 Pro",
        ["iPhone13,4"] = "iPhone 12 Pro Max",
        ["iPhone14,2"] = "iPhone 13 Pro",
        ["iPhone14,3"] = "iPhone 13 Pro Max",
        ["iPhone14,4"] = "iPhone 13 mini",
        ["iPhone14,5"] = "iPhone 13",
        ["iPhone14,6"] = "iPhone SE（第 3 代）",
        ["iPhone14,7"] = "iPhone 14",
        ["iPhone14,8"] = "iPhone 14 Plus",
        ["iPhone15,2"] = "iPhone 14 Pro",
        ["iPhone15,3"] = "iPhone 14 Pro Max",
        ["iPhone15,4"] = "iPhone 15",
        ["iPhone15,5"] = "iPhone 15 Plus",
        ["iPhone16,1"] = "iPhone 15 Pro",
        ["iPhone16,2"] = "iPhone 15 Pro Max",
        ["iPhone17,1"] = "iPhone 16 Pro",
        ["iPhone17,2"] = "iPhone 16 Pro Max",
        ["iPhone17,3"] = "iPhone 16",
        ["iPhone17,4"] = "iPhone 16 Plus",
        ["iPhone17,5"] = "iPhone 16e",
        ["iPhone18,1"] = "iPhone 17 Pro",
        ["iPhone18,2"] = "iPhone 17 Pro Max",
        ["iPhone18,3"] = "iPhone 17",
        ["iPhone18,4"] = "iPhone Air",
        ["iPhone18,5"] = "iPhone 17e",

        // ---- iPad（只收公开清单里没有歧义的）
        ["iPad11,1"] = "iPad mini（第 5 代）",
        ["iPad11,2"] = "iPad mini（第 5 代）",
        ["iPad11,3"] = "iPad Air（第 3 代）",
        ["iPad11,4"] = "iPad Air（第 3 代）",
        ["iPad11,6"] = "iPad（第 8 代）",
        ["iPad11,7"] = "iPad（第 8 代）",
        ["iPad12,1"] = "iPad（第 9 代）",
        ["iPad12,2"] = "iPad（第 9 代）",
        ["iPad13,1"] = "iPad Air（第 4 代）",
        ["iPad13,2"] = "iPad Air（第 4 代）",
        ["iPad13,16"] = "iPad Air（第 5 代）",
        ["iPad13,17"] = "iPad Air（第 5 代）",
        ["iPad13,18"] = "iPad（第 10 代）",
        ["iPad13,19"] = "iPad（第 10 代）",
        ["iPad14,1"] = "iPad mini（第 6 代）",
        ["iPad14,2"] = "iPad mini（第 6 代）",
        ["iPad14,8"] = "iPad Air 11 英寸（第 6 代）",
        ["iPad14,9"] = "iPad Air 11 英寸（第 6 代）",
        ["iPad14,10"] = "iPad Air 13 英寸（第 6 代）",
        ["iPad14,11"] = "iPad Air 13 英寸（第 6 代）",
        ["iPad15,3"] = "iPad Air 11 英寸（第 7 代）",
        ["iPad15,4"] = "iPad Air 11 英寸（第 7 代）",
        ["iPad15,5"] = "iPad Air 13 英寸（第 7 代）",
        ["iPad15,6"] = "iPad Air 13 英寸（第 7 代）",
        ["iPad15,7"] = "iPad（第 11 代）",
        ["iPad15,8"] = "iPad（第 11 代）",
        ["iPad16,1"] = "iPad mini（第 7 代）",
        ["iPad16,2"] = "iPad mini（第 7 代）",
    };

    /// <summary>
    /// 安卓品牌前缀。安卓机型有成千上万种，**没有**权威的"代号→型号"总表，
    /// 所以这里只做到品牌级别，而且明确标成"猜测"，界面上会把原始型号代码一起显示。
    /// </summary>
    private static readonly (string Prefix, string Brand)[] AndroidBrands =
    {
        ("SM-", "三星"), ("SAMSUNG", "三星"), ("GT-", "三星"),
        ("Pixel", "Google"),
        ("Redmi", "红米"), ("POCO", "POCO"), ("MI ", "小米"), ("M2", "小米"), ("2201", "小米"), ("2210", "小米"),
        ("CPH", "OPPO"), ("PACM", "OPPO"), ("PG", "OPPO"),
        ("V2", "vivo"), ("V1", "vivo"), ("PD", "vivo"), ("iQOO", "iQOO"),
        ("HUAWEI", "华为"), ("ANE-", "华为"), ("ELE-", "华为"), ("JLN-", "华为"),
        ("ANY-", "荣耀"), ("REA-", "荣耀"), ("FNE-", "荣耀"),
        ("ONEPLUS", "一加"), ("LE2", "一加"), ("NE2", "一加"), ("CPH2", "一加"),
        ("NX", "努比亚 / 红魔"), ("REDMAGIC", "红魔"),
        ("moto", "摩托罗拉"), ("XT", "摩托罗拉"),
        ("LGE", "LG"), ("LM-", "LG"),
        ("Xperia", "索尼"), ("SO-", "索尼"),
        ("MEIZU", "魅族"), ("m1", "魅族"),
        ("blackshark", "黑鲨"),
    };

    /// <summary>苹果型号代码 → 可读名称；不认识返回 null。</summary>
    public static string? AppleModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        return Apple.TryGetValue(model.Trim(), out string? name) ? name : null;
    }

    /// <summary>按型号代码前缀推断品牌；推断不出返回 null。结果始终标记为猜测。</summary>
    public static string? AndroidBrandOf(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        string value = model.Trim();
        foreach ((string prefix, string brand) in AndroidBrands)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return brand;
            }
        }

        // 有些手机把设备名当型号报上来（如"小明的小米13"），退一步在整串里找品牌词
        foreach ((string prefix, string brand) in AndroidBrands)
        {
            if (prefix.Length >= 3 && value.Contains(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return brand;
            }
        }

        return null;
    }

    /// <summary>从 AirPlay 握手里拿到的字段构造识别结果。</summary>
    public static DeviceIdentity FromAirPlay(string? model, string? name, string? deviceId,
                                             string? osName, string? osVersion, string? userAgent)
    {
        string? friendly = AppleModel(model);
        return new DeviceIdentity
        {
            Protocol = "AirPlay",
            Name = Trim(name),
            Model = Trim(model),
            FriendlyModel = friendly,
            Brand = friendly is not null ? "Apple" : null,
            OsName = Trim(osName),
            OsVersion = Trim(osVersion),
            UserAgent = Trim(userAgent),
            DeviceId = Trim(deviceId),
        };
    }

    /// <summary>
    /// 从 Miracast 的发送端信息构造识别结果。Windows 只给名字和 MAC，
    /// 没有系统版本可拿，所以这里能确定的只有"安卓侧 + 可能的品牌"。
    /// </summary>
    public static DeviceIdentity FromMiracast(string? transmitterName, string? macAddress)
    {
        string? name = Trim(transmitterName);
        string? brand = AndroidBrandOf(name);

        return new DeviceIdentity
        {
            Protocol = "无线投屏",
            Name = name,
            Model = name,                 // 安卓这边"型号"就是它报的名字
            FriendlyModel = null,         // 安卓没有权威映射表，不假装认识
            Brand = brand,
            BrandIsGuess = brand is not null,
            DeviceId = Trim(macAddress),
        };
    }

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
