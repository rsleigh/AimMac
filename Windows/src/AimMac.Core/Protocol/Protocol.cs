using System.Buffers.Binary;

namespace AimMac.Core.Protocol;

/// <summary>
/// BLE identifiers shared with the Mac receiver and the iPhone app.
/// The byte layout is the one documented in Shared/Protocol.swift.
/// </summary>
public static class ProtocolIds
{
    public static readonly Guid Service = Guid.Parse("7E1A0001-3C2B-4F7D-9B61-2A9E5D0C4F10");
    public static readonly Guid Input = Guid.Parse("7E1A0002-3C2B-4F7D-9B61-2A9E5D0C4F10");
    public static readonly Guid Status = Guid.Parse("7E1A0003-3C2B-4F7D-9B61-2A9E5D0C4F10");
}

public enum PacketKind : byte
{
    Hello = 0x01,
    Aim = 0x02,
    Press = 0x03,
    Release = 0x04,
    Right = 0x05,
    Scroll = 0x06,
    Center = 0x07,
    Pause = 0x08,
    Info = 0x09,
    Move = 0x0A,
}

public enum LinkState : byte
{
    Waiting = 0,
    Approved = 1,
    Denied = 2,
}

public abstract record Packet
{
    public sealed record Hello(byte[] UuidBytes, byte DeviceKind, string Name) : Packet;
    public sealed record Aim(double Time, float Azimuth, float Elevation) : Packet;
    public sealed record Press(double Time) : Packet;
    public sealed record Release(double Time) : Packet;
    public sealed record Right(double Time) : Packet;
    public sealed record Scroll(float Dx, float Dy) : Packet;
    public sealed record Center : Packet;
    public sealed record Pause(bool On) : Packet;
    public sealed record Info(byte Battery, string Model) : Packet;
    public sealed record Move(float Dx, float Dy) : Packet;
}

public static class PacketCodec
{
    public const int MaxPacketLength = 78;
    public const int MaxTextBytes = 60;

    public static bool TryDecode(ReadOnlySpan<byte> data, out Packet? packet, out string error)
    {
        packet = null;
        if (data.Length == 0)
        {
            error = "Empty packet.";
            return false;
        }

        if (data.Length > MaxPacketLength)
        {
            error = "Packet longer than the protocol maximum.";
            return false;
        }

        if (!Enum.IsDefined(typeof(PacketKind), data[0]))
        {
            error = "Unknown packet kind.";
            return false;
        }

        var kind = (PacketKind)data[0];
        var rest = data[1..];
        switch (kind)
        {
            case PacketKind.Hello:
                return TryHello(rest, out packet, out error);
            case PacketKind.Aim:
                return TryAim(rest, out packet, out error);
            case PacketKind.Press:
                return TryTimed(rest, static t => new Packet.Press(t), out packet, out error);
            case PacketKind.Release:
                return TryTimed(rest, static t => new Packet.Release(t), out packet, out error);
            case PacketKind.Right:
                return TryTimed(rest, static t => new Packet.Right(t), out packet, out error);
            case PacketKind.Scroll:
                return TryPair(rest, static (x, y) => new Packet.Scroll(x, y), out packet, out error);
            case PacketKind.Center:
                if (rest.Length != 0)
                {
                    error = "Center packet has extra bytes.";
                    return false;
                }
                packet = new Packet.Center();
                error = "";
                return true;
            case PacketKind.Pause:
                if (rest.Length != 1 || (rest[0] != 0 && rest[0] != 1))
                {
                    error = "Pause packet must be one byte, 0 or 1.";
                    return false;
                }
                packet = new Packet.Pause(rest[0] == 1);
                error = "";
                return true;
            case PacketKind.Info:
                return TryInfo(rest, out packet, out error);
            case PacketKind.Move:
                return TryPair(rest, static (x, y) => new Packet.Move(x, y), out packet, out error);
            default:
                error = "Unknown packet kind.";
                return false;
        }
    }

    public static byte[] EncodeStatus(LinkState state, bool paused, string hostName)
    {
        var name = SanitizeHostName(hostName);
        var utf8 = System.Text.Encoding.UTF8.GetBytes(name);
        if (utf8.Length > MaxTextBytes)
        {
            utf8 = utf8.AsSpan(0, MaxTextBytes).ToArray();
        }

        var data = new byte[2 + utf8.Length];
        data[0] = (byte)state;
        data[1] = paused ? (byte)1 : (byte)0;
        utf8.CopyTo(data, 2);
        return data;
    }

    public static bool TryDecodeStatus(ReadOnlySpan<byte> data, out LinkState state, out bool paused, out string name, out string error)
    {
        state = LinkState.Waiting;
        paused = false;
        name = "";
        if (data.Length < 2)
        {
            error = "Status shorter than 2 bytes.";
            return false;
        }

        if (!Enum.IsDefined(typeof(LinkState), data[0]))
        {
            error = "Unknown link state.";
            return false;
        }

        if (data[1] != 0 && data[1] != 1)
        {
            error = "Status pause byte must be 0 or 1.";
            return false;
        }

        if (!TryText(data[2..], required: false, out name, out error))
        {
            return false;
        }

        state = (LinkState)data[0];
        paused = data[1] == 1;
        return true;
    }

    /// <summary>
    /// UUID bytes are in RFC string order, matching Swift's uuid_t layout.
    /// This is not the mixed-endian layout of <see cref="Guid(byte[])"/>.
    /// </summary>
    public static string FormatUuid(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 16)
        {
            return "";
        }

        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    public static string SanitizeHostName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Windows";
        }

        var cleaned = new string(name.Trim().Where(c => !char.IsControl(c)).ToArray());
        if (cleaned.Length == 0)
        {
            return "Windows";
        }

        return cleaned.Length <= 24 ? cleaned : cleaned[..24];
    }

    private static bool TryHello(ReadOnlySpan<byte> rest, out Packet? packet, out string error)
    {
        packet = null;
        if (rest.Length < 17 || rest.Length > 17 + MaxTextBytes)
        {
            error = "Hello length is outside 18..78 bytes.";
            return false;
        }

        var uuid = rest[..16].ToArray();
        var kind = rest[16];
        if (kind > 1)
        {
            error = "Hello device kind must be 0 (watch) or 1 (phone).";
            return false;
        }

        if (!TryText(rest[17..], required: false, out var name, out error))
        {
            return false;
        }

        packet = new Packet.Hello(uuid, kind, name);
        return true;
    }

    private static bool TryInfo(ReadOnlySpan<byte> rest, out Packet? packet, out string error)
    {
        packet = null;
        if (rest.Length < 1 || rest.Length > 1 + MaxTextBytes)
        {
            error = "Info length is outside 2..62 bytes.";
            return false;
        }

        var battery = rest[0];
        if (battery != 255 && battery > 100)
        {
            error = "Battery must be 0..100 or 255.";
            return false;
        }

        if (!TryText(rest[1..], required: false, out var model, out error))
        {
            return false;
        }

        packet = new Packet.Info(battery, model);
        return true;
    }

    private static bool TryAim(ReadOnlySpan<byte> rest, out Packet? packet, out string error)
    {
        packet = null;
        if (rest.Length != 16)
        {
            error = "Aim packet must be 17 bytes.";
            return false;
        }

        var time = BinaryPrimitives.ReadDoubleLittleEndian(rest[..8]);
        var az = BinaryPrimitives.ReadSingleLittleEndian(rest[8..12]);
        var el = BinaryPrimitives.ReadSingleLittleEndian(rest[12..16]);
        if (!GoodTime(time) || !GoodAngle(az) || !GoodAngle(el))
        {
            error = "Aim values must be finite and in range.";
            return false;
        }

        packet = new Packet.Aim(time, az, el);
        error = "";
        return true;
    }

    private static bool TryTimed(ReadOnlySpan<byte> rest, Func<double, Packet> make, out Packet? packet, out string error)
    {
        packet = null;
        if (rest.Length != 8)
        {
            error = "Timed packet must be 9 bytes.";
            return false;
        }

        var time = BinaryPrimitives.ReadDoubleLittleEndian(rest);
        if (!GoodTime(time))
        {
            error = "Timestamp must be finite, non-negative, and below 1e9 seconds.";
            return false;
        }

        packet = make(time);
        error = "";
        return true;
    }

    private static bool TryPair(ReadOnlySpan<byte> rest, Func<float, float, Packet> make, out Packet? packet, out string error)
    {
        packet = null;
        if (rest.Length != 8)
        {
            error = "Delta packet must be 9 bytes.";
            return false;
        }

        var dx = BinaryPrimitives.ReadSingleLittleEndian(rest[..4]);
        var dy = BinaryPrimitives.ReadSingleLittleEndian(rest[4..]);
        if (!GoodDelta(dx) || !GoodDelta(dy))
        {
            error = "Delta values must be finite and within ±4000.";
            return false;
        }

        packet = make(dx, dy);
        error = "";
        return true;
    }

    private static bool TryText(ReadOnlySpan<byte> bytes, bool required, out string text, out string error)
    {
        text = "";
        error = "";
        if (bytes.Length == 0)
        {
            if (required)
            {
                error = "Missing text.";
                return false;
            }
            return true;
        }

        if (!System.Text.Unicode.Utf8.IsValid(bytes))
        {
            error = "Text is not valid UTF-8.";
            return false;
        }

        text = System.Text.Encoding.UTF8.GetString(bytes);
        foreach (var c in text)
        {
            if (char.IsControl(c))
            {
                error = "Text contains a control character.";
                text = "";
                return false;
            }
        }

        return true;
    }

    private static bool GoodTime(double t) => double.IsFinite(t) && t >= 0 && t <= 1_000_000_000d;

    private static bool GoodAngle(float v) => float.IsFinite(v) && v is >= -720f and <= 720f;

    private static bool GoodDelta(float v) => float.IsFinite(v) && v is >= -4000f and <= 4000f;
}
