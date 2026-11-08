using System.Buffers.Binary;
using System.Text;

namespace AimMac.Core.Tests;

internal static class Frames
{
    public static byte[] Hello(string name = "Phone", byte kind = 1, byte[]? uuid = null)
    {
        uuid ??= Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var text = Encoding.UTF8.GetBytes(name);
        var data = new byte[18 + text.Length];
        data[0] = 0x01;
        uuid.CopyTo(data.AsSpan(1));
        data[17] = kind;
        text.CopyTo(data.AsSpan(18));
        return data;
    }

    public static byte[] Aim(double time, float azimuth, float elevation)
    {
        var data = new byte[17];
        data[0] = 0x02;
        BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(1), time);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(9), azimuth);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(13), elevation);
        return data;
    }

    public static byte[] Timed(byte kind, double time)
    {
        var data = new byte[9];
        data[0] = kind;
        BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(1), time);
        return data;
    }

    public static byte[] Delta(byte kind, float dx, float dy)
    {
        var data = new byte[9];
        data[0] = kind;
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(1), dx);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(5), dy);
        return data;
    }

    public static byte[] FloatBits(float value)
    {
        Span<byte> raw = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(raw, value);
        return raw.ToArray();
    }
}
