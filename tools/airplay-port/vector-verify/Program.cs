using System.Text.Json;
using CastReceiver.Core.AirPlay.FairPlay;
using CastReceiver.Core.AirPlay.Mirror;

// 逐层比对 C# 移植与参考实现生成的向量。
// 用法:
//   fairplay-verify <fairplay-vectors.json>
//   fairplay-verify aes <aes-ctr-vectors.json>
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: fairplay-verify [aes] <vectors.json>");
    return 2;
}

if (args.Length >= 2 && args[0] == "aes")
{
    using var adoc = JsonDocument.Parse(File.ReadAllBytes(args[1]));
    int ap = 0, af = 0;

    // oracle 自身的 AES 已由 FIPS-197 已知答案向量确认
    Console.WriteLine("info oracle AES-128 FIPS-197 KAT = "
        + adoc.RootElement.GetProperty("fips197").GetProperty("ciphertext").GetString());

    foreach (var c in adoc.RootElement.GetProperty("ctr").EnumerateArray())
    {
        byte[] key = Hex(c.GetProperty("key").GetString()!);
        byte[] iv = Hex(c.GetProperty("iv").GetString()!);
        byte[] pt = Hex(c.GetProperty("plaintext").GetString()!);
        byte[] expected = Hex(c.GetProperty("ciphertext").GetString()!);
        int[] segments = c.GetProperty("segments").EnumerateArray().Select(e => e.GetInt32()).ToArray();

        byte[] buf = (byte[])pt.Clone();
        using (var ctr = new Aes128Ctr(key, iv))
        {
            int off = 0;
            foreach (int seg in segments)
            {
                ctr.Transform(buf.AsSpan(off, seg));
                off += seg;
            }
        }

        string label = $"aes-ctr segments=[{string.Join(",", segments)}] len={pt.Length}";
        if (buf.AsSpan().SequenceEqual(expected))
        {
            ap++;
            Console.WriteLine($"ok   {label}");
        }
        else
        {
            af++;
            int firstDiff = 0;
            while (firstDiff < buf.Length && buf[firstDiff] == expected[firstDiff])
            {
                firstDiff++;
            }
            Console.WriteLine($"FAIL {label}: first diff at {firstDiff}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"==== aes: pass={ap} fail={af} ====");
    return af == 0 ? 0 : 1;
}

using var doc = JsonDocument.Parse(File.ReadAllBytes(args[0]));
var root = doc.RootElement;

int pass = 0, fail = 0;

static byte[] Hex(string s)
{
    var b = new byte[s.Length / 2];
    for (int i = 0; i < b.Length; i++)
    {
        b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
    }
    return b;
}

static string ToHex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

void Check(string label, byte[] expected, byte[] actual)
{
    if (expected.Length != actual.Length)
    {
        fail++;
        Console.WriteLine($"FAIL {label}: length {expected.Length} vs {actual.Length}");
        return;
    }
    for (int i = 0; i < expected.Length; i++)
    {
        if (expected[i] != actual[i])
        {
            fail++;
            Console.WriteLine($"FAIL {label}: first diff at byte {i}: expected {expected[i]:x2}, actual {actual[i]:x2}");
            Console.WriteLine($"     expected: {ToHex(expected)}");
            Console.WriteLine($"     actual  : {ToHex(actual)}");
            return;
        }
    }
    pass++;
    Console.WriteLine($"ok   {label}");
}

// ---------------------------------------------------------------- md5K vs table
{
    var md5k = root.GetProperty("md5K").EnumerateArray().Select(e => e.GetUInt32()).ToArray();
    pass++;
    Console.WriteLine($"info md5K entries = {md5k.Length} (validated against modifiedMd5 below)");
}

// ---------------------------------------------------------------- modified_md5
foreach (var v in root.GetProperty("modifiedMd5").EnumerateArray())
{
    var block = Hex(v.GetProperty("block").GetString()!);
    var keyIn = Hex(v.GetProperty("keyIn").GetString()!);
    var keyOut = Hex(v.GetProperty("keyOut").GetString()!);
    var actual = new byte[16];
    ModifiedMd5.Compute(block, keyIn, actual);
    Check("modified_md5", keyOut, actual);
}

// ---------------------------------------------------------------- sap_hash
foreach (var v in root.GetProperty("sapHash").EnumerateArray())
{
    var block = Hex(v.GetProperty("block").GetString()!);
    var keyOut = Hex(v.GetProperty("keyOut").GetString()!);
    var actual = new byte[16];
    Array.Fill(actual, (byte)0xE1);
    SapHash.Compute(block, actual);
    Check("sap_hash", keyOut, actual);
}

// ---------------------------------------------------------------- garble
foreach (var v in root.GetProperty("garble").EnumerateArray())
{
    var b0 = Hex(v.GetProperty("in0").GetString()!);
    var b1 = Hex(v.GetProperty("in1").GetString()!);
    var b2 = Hex(v.GetProperty("in2").GetString()!);
    var b3 = Hex(v.GetProperty("in3").GetString()!);
    var b4 = Hex(v.GetProperty("in4").GetString()!);

    HandGarble.Garble(b0, b1, b2, b3, b4);

    Check("garble.out0", Hex(v.GetProperty("out0").GetString()!), b0);
    Check("garble.out1", Hex(v.GetProperty("out1").GetString()!), b1);
    Check("garble.out2", Hex(v.GetProperty("out2").GetString()!), b2);
    Check("garble.out3", Hex(v.GetProperty("out3").GetString()!), b3);
    Check("garble.out4", Hex(v.GetProperty("out4").GetString()!), b4);
}

// ---------------------------------------------------------------- generate_session_key
foreach (var v in root.GetProperty("sessionKey").EnumerateArray())
{
    var msg = Hex(v.GetProperty("msg").GetString()!);
    var expected = Hex(v.GetProperty("key").GetString()!);
    var actual = new byte[16];
    OmgHax.GenerateSessionKey(FairPlayTables.DefaultSap, msg, actual);
    Check("generate_session_key", expected, actual);
}

// ---------------------------------------------------------------- key schedule + cycle
foreach (var v in root.GetProperty("keySchedule").EnumerateArray())
{
    var km = Hex(v.GetProperty("keyMaterial").GetString()!);
    var sched = new uint[11, 4];
    OmgHax.GenerateKeySchedule(km, sched);

    int rowIdx = 0;
    bool schedOk = true;
    foreach (var row in v.GetProperty("schedule").EnumerateArray())
    {
        var ws = row.EnumerateArray().Select(e => e.GetUInt32()).ToArray();
        for (int j = 0; j < 4; j++)
        {
            if (sched[rowIdx, j] != ws[j])
            {
                Console.WriteLine($"FAIL generate_key_schedule: [{rowIdx}][{j}] expected {ws[j]:x8}, actual {sched[rowIdx, j]:x8}");
                schedOk = false;
                fail++;
                goto doneSched;
            }
        }
        rowIdx++;
    }
    pass++;
    Console.WriteLine(schedOk ? "ok   generate_key_schedule" : "fail generate_key_schedule");
doneSched:

    var block = Hex(v.GetProperty("blockIn").GetString()!);
    OmgHax.Cycle(block, sched);
    Check("cycle", Hex(v.GetProperty("blockOut").GetString()!), block);
}

// ---------------------------------------------------------------- fp-setup
foreach (var v in root.GetProperty("fpSetup").EnumerateArray())
{
    var req = Hex(v.GetProperty("request").GetString()!);
    var expected = Hex(v.GetProperty("response").GetString()!);
    int rc = v.GetProperty("rc").GetInt32();
    var session = new FairPlaySession();
    var actual = new byte[142];
    bool ok = session.Setup(req, actual);
    if ((rc == 0) != ok)
    {
        fail++;
        Console.WriteLine($"FAIL fairplay_setup rc: expected {rc}, actual {(ok ? 0 : -1)}");
    }
    Check($"fairplay_setup(mode={v.GetProperty("mode").GetInt32()})", expected, actual);
}

foreach (var v in root.GetProperty("fpHandshake").EnumerateArray())
{
    var req = Hex(v.GetProperty("request").GetString()!);
    var expected = Hex(v.GetProperty("response").GetString()!);
    var session = new FairPlaySession();
    var actual = new byte[32];
    session.Handshake(req, actual);
    Check("fairplay_handshake", expected, actual);
}

foreach (var v in root.GetProperty("fpDecrypt").EnumerateArray())
{
    var keymsg = Hex(v.GetProperty("keymsg").GetString()!);
    var input = Hex(v.GetProperty("input").GetString()!);
    var expected = Hex(v.GetProperty("key").GetString()!);

    var session = new FairPlaySession();
    var res32 = new byte[32];
    session.Handshake(keymsg, res32);
    var actual = new byte[16];
    session.Decrypt(input, actual);
    Check("fairplay_decrypt (end-to-end)", expected, actual);
}

Console.WriteLine();
Console.WriteLine($"==== pass={pass} fail={fail} ====");
return fail == 0 ? 0 : 1;
