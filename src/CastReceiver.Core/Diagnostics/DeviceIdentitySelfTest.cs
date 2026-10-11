using CastReceiver.Core.Devices;

namespace CastReceiver.Core.Diagnostics;

/// <summary>
/// 设备识别（⑦）的自检。这些用例的价值在于把"认不出来时会不会瞎猜"这件事钉死：
/// 收录的型号必须认得准，**没收录的必须老实显示原始代码**，而不是编一个看着合理的名字。
/// </summary>
internal static class DeviceIdentitySelfTest
{
    private const string Group = "K 设备识别";

    internal static void Run(Action<string, string, SelfTestRunner.Verdict, string> add)
    {
        ArgumentNullException.ThrowIfNull(add);

        CheckKnownApple(add);
        CheckUnknownMustNotGuess(add);
        CheckAndroidBrand(add);
        CheckNothingKnown(add);
        CheckSystemLabel(add);
        CheckMiracast(add);
    }

    private static void CheckKnownApple(Action<string, string, SelfTestRunner.Verdict, string> add)
    {
        (string Code, string Expected)[] cases =
        {
            ("iPhone14,5", "iPhone 13"),
            ("iPhone15,2", "iPhone 14 Pro"),
            ("iPhone16,1", "iPhone 15 Pro"),
            ("iPhone11,8", "iPhone XR"),
            ("iPad13,1", "iPad Air（第 4 代）"),
            ("iPad14,1", "iPad mini（第 6 代）"),
        };

        var wrong = new List<string>();
        foreach ((string code, string expected) in cases)
        {
            string? actual = DeviceModelCatalog.AppleModel(code);
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                wrong.Add($"{code}→{actual ?? "(空)"}（应为 {expected}）");
            }
        }

        add(Group, $"已知苹果型号识别（{cases.Length} 个）", wrong.Count == 0,
            wrong.Count == 0 ? "全部与公开标识符清单一致" : string.Join("；", wrong));

        // 大小写不该影响识别
        bool caseInsensitive = DeviceModelCatalog.AppleModel("iphone15,2") == "iPhone 14 Pro";
        add(Group, "型号识别不区分大小写", caseInsensitive, caseInsensitive ? "iphone15,2 也能认出" : "大小写导致识别失败");
    }

    private static void CheckUnknownMustNotGuess(Action<string, string, SelfTestRunner.Verdict, string> add)
    {
        // iPad8,9 在公开清单里被标成两代（自相矛盾），所以刻意没收录 ——
        // 这种情况必须返回 null，让界面显示原始代码。
        string? unknown = DeviceModelCatalog.AppleModel("iPad8,9");
        bool notGuessed = unknown is null;

        var identity = DeviceModelCatalog.FromAirPlay("iPad8,9", "某人的 iPad", null, null, null, null);
        string described = identity.Describe();

        add(Group, "未收录型号不猜名字（宁显示原始代码）",
            notGuessed && described.Contains("iPad8,9", StringComparison.Ordinal),
            notGuessed ? $"描述为「{described}」" : $"竟然给出了 {unknown}");

        // 完全没听说过的编号同样不能猜
        string? bogus = DeviceModelCatalog.AppleModel("iPhone99,9");
        add(Group, "不存在的型号返回空", bogus is null, bogus is null ? "返回 null" : $"返回了 {bogus}");
    }

    private static void CheckAndroidBrand(Action<string, string, SelfTestRunner.Verdict, string> add)
    {
        (string Model, string Brand)[] cases =
        {
            ("SM-G998B", "三星"),
            ("Pixel 7", "Google"),
            ("Redmi Note 12", "红米"),
            ("CPH2451", "OPPO"),
            ("V2219A", "vivo"),
            ("ANY-AN00", "荣耀"),
        };

        var wrong = new List<string>();
        foreach ((string model, string brand) in cases)
        {
            string? actual = DeviceModelCatalog.AndroidBrandOf(model);
            if (!string.Equals(actual, brand, StringComparison.Ordinal))
            {
                wrong.Add($"{model}→{actual ?? "(空)"}（应为 {brand}）");
            }
        }

        add(Group, $"安卓品牌前缀推断（{cases.Length} 个）", wrong.Count == 0,
            wrong.Count == 0 ? "全部命中" : string.Join("；", wrong));

        // 品牌是猜的，必须标出来；而且**原始型号代码要一起显示**，不能只给品牌
        var identity = DeviceModelCatalog.FromMiracast("SM-G998B", "3c:5a:b4:11:22:33");
        string described = identity.Describe();
        bool honest = identity.BrandIsGuess && described.Contains("三星", StringComparison.Ordinal)
                      && described.Contains("SM-G998B", StringComparison.Ordinal);

        add(Group, "安卓只推断到品牌且标注为猜测", honest, $"描述为「{described}」");

        // 完全不认识的安卓型号：不能硬套品牌
        string? none = DeviceModelCatalog.AndroidBrandOf("ZZZ-1234");
        add(Group, "认不出的安卓型号不套品牌", none is null, none is null ? "返回 null" : $"返回了 {none}");
    }

    private static void CheckNothingKnown(Action<string, string, SelfTestRunner.Verdict, string> add)
    {
        var empty = DeviceModelCatalog.FromMiracast(null, null);
        string described = empty.Describe();
        bool honest = described == "未知设备";
        add(Group, "什么都不知道时显示「未知设备」", honest, $"描述为「{described}」");
    }

    private static void CheckSystemLabel(Action<string, string, SelfTestRunner.Verdict, string> add)
    {
        var withVersion = DeviceModelCatalog.FromAirPlay("iPhone14,5", "张三的 iPhone", null, "iOS", "18.1", "AirPlay/770.8.1");
        string described = withVersion.Describe();
        bool ok = described == "iPhone 13 · iOS 18.1";
        add(Group, "系统名与版本拼接成一行", ok, $"描述为「{described}」");

        // 只有系统名没有版本时不该出现多余空格或 "null"
        var noVersion = DeviceModelCatalog.FromAirPlay(null, null, null, "iPadOS", null, null);
        string d2 = noVersion.Describe();
        bool ok2 = d2 == "iPadOS" && !d2.Contains("null", StringComparison.OrdinalIgnoreCase);
        add(Group, "缺系统版本时不出现 null", ok2, $"描述为「{d2}」");

        bool apple = withVersion.IsApple && noVersion.IsApple && !DeviceModelCatalog.FromMiracast("SM-G998B", "aa:bb").IsApple;
        add(Group, "能区分苹果与安卓", apple, "iPhone/iPadOS 判为苹果，安卓侧不判为苹果");
    }

    private static void CheckMiracast(Action<string, string, SelfTestRunner.Verdict, string> add)
    {
        // Windows 只给名字和 MAC，没有型号/系统版本 —— 所以不能假装认识型号
        var identity = DeviceModelCatalog.FromMiracast("SM-G998B", "3c:5a:b4:11:22:33");
        bool noFakeModel = identity.FriendlyModel is null;
        bool keepsRaw = identity.Describe().Contains("SM-G998B", StringComparison.Ordinal);
        add(Group, "Miracast 侧不假装认识型号", noFakeModel && keepsRaw,
            $"描述为「{identity.Describe()}」，FriendlyModel={(identity.FriendlyModel ?? "null")}");

        var named = DeviceModelCatalog.FromMiracast("张三的手机", "3c:5a:b4:11:22:33");
        bool fallsBack = named.Describe() == "张三的手机";
        add(Group, "Miracast 认不出时用原始设备名", fallsBack, $"描述为「{named.Describe()}」");
    }
}
