using System;
using System.Numerics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FModel.Extensions;

/// <summary>
/// Ports the detection heuristic from the user-supplied convert_uint64_floats.py: some game data
/// stores a double's raw 8 bytes as a big uint64 in json. This walks a parsed json document and
/// replaces any integer that looks like a bit-cast double with the decoded, human readable float.
/// </summary>
public static class Uint64FloatConverter
{
    // Same defaults as the python script.
    private const double MinFloat = 1e-6;
    private const double MaxFloat = 1e12;

    // Integers below this are almost certainly real integers (ids, counts, enum values, ...)
    // so they're skipped before even trying to decode them.
    private const ulong Uint64Threshold = 1UL << 32; // 4 294 967 296

    public static string Convert(string json)
    {
        var token = JToken.Parse(json);
        ConvertToken(token);
        return token.ToString(Formatting.Indented);
    }

    private static void ConvertToken(JToken token)
    {
        switch (token)
        {
            case JObject obj:
                foreach (var prop in obj.Properties())
                {
                    if (TryConvertValue(prop.Value, out var replaced)) prop.Value = replaced;
                    else ConvertToken(prop.Value);
                }
                break;
            case JArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (TryConvertValue(arr[i], out var replaced)) arr[i] = replaced;
                    else ConvertToken(arr[i]);
                }
                break;
        }
    }

    private static bool TryConvertValue(JToken value, out JValue replaced)
    {
        replaced = null;
        if (value.Type != JTokenType.Integer) return false;

        ulong u;
        switch (((JValue) value).Value)
        {
            case long l when l >= 0:
                u = (ulong) l;
                break;
            case ulong ul:
                u = ul;
                break;
            case BigInteger bi when bi >= 0 && bi <= ulong.MaxValue:
                u = (ulong) bi;
                break;
            default:
                return false; // negative or out of uint64 range: not a legitimate bit-cast double
        }

        if (u < Uint64Threshold) return false;

        var f = BitConverter.Int64BitsToDouble(unchecked((long) u));
        if (double.IsNaN(f) || double.IsInfinity(f)) return false;

        var absF = Math.Abs(f);
        if (absF != 0.0 && absF < MinFloat) return false;
        if (absF > MaxFloat) return false;

        replaced = new JValue(f);
        return true;
    }
}
