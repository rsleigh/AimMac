using AimMac.Core.Protocol;
using Xunit;

namespace AimMac.Core.Tests;

public class PacketCodecTests
{
    [Fact]
    public void DecodesAimExactly()
    {
        Assert.True(PacketCodec.TryDecode(Frames.Aim(12.5, 10f, -3f), out var packet, out var error), error);
        var aim = Assert.IsType<Packet.Aim>(packet);
        Assert.Equal(12.5, aim.Time);
        Assert.Equal(10f, aim.Azimuth);
        Assert.Equal(-3f, aim.Elevation);
    }

    [Fact]
    public void DecodesHelloInRfcUuidOrder()
    {
        var uuid = new byte[16];
        uuid[0] = 0x00;
        uuid[1] = 0x11;
        uuid[2] = 0x22;
        uuid[3] = 0x33;
        uuid[4] = 0x44;
        uuid[5] = 0x55;
        uuid[6] = 0x66;
        uuid[7] = 0x77;
        uuid[8] = 0x88;
        uuid[9] = 0x99;
        uuid[10] = 0xaa;
        uuid[11] = 0xbb;
        uuid[12] = 0xcc;
        uuid[13] = 0xdd;
        uuid[14] = 0xee;
        uuid[15] = 0xff;
        Assert.True(PacketCodec.TryDecode(Frames.Hello("iPhone", 1, uuid), out var packet, out var error), error);
        var hello = Assert.IsType<Packet.Hello>(packet);
        Assert.Equal(1, hello.DeviceKind);
        Assert.Equal("iPhone", hello.Name);
        Assert.Equal("00112233-4455-6677-8899-aabbccddeeff", PacketCodec.FormatUuid(hello.UuidBytes));
    }

    [Theory]
    [InlineData(0x03)]
    [InlineData(0x04)]
    [InlineData(0x05)]
    public void DecodesTimedPackets(byte kind)
    {
        Assert.True(PacketCodec.TryDecode(Frames.Timed(kind, 4), out var packet, out var error), error);
        Assert.Equal(kind, packet switch
        {
            Packet.Press => (byte)0x03,
            Packet.Release => (byte)0x04,
            Packet.Right => (byte)0x05,
            _ => (byte)0,
        });
    }

    [Fact]
    public void DecodesScrollMoveCenterPauseAndInfo()
    {
        Assert.IsType<Packet.Scroll>(Decoded(Frames.Delta(0x06, 1.5f, -2f)));
        Assert.IsType<Packet.Move>(Decoded(Frames.Delta(0x0A, 3f, 4f)));
        Assert.IsType<Packet.Center>(Decoded([0x07]));
        Assert.IsType<Packet.Pause>(Decoded([0x08, 0x01]));
        var info = Assert.IsType<Packet.Info>(Decoded([0x09, 80, (byte)'i']));
        Assert.Equal(80, info.Battery);
        Assert.Equal("i", info.Model);
        var unknownBattery = Assert.IsType<Packet.Info>(Decoded([0x09, 255]));
        Assert.Equal(255, unknownBattery.Battery);
    }

    [Theory]
    [MemberData(nameof(InvalidFrames))]
    public void RejectsInvalidFrames(byte[] frame)
    {
        Assert.False(PacketCodec.TryDecode(frame, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    public static IEnumerable<object[]> InvalidFrames()
    {
        yield return [Array.Empty<byte>()];
        yield return [new byte[] { 0x00 }];
        yield return [new byte[] { 0x02, 1 }];
        yield return [new byte[] { 0x07, 0 }];
        yield return [new byte[] { 0x08, 2 }];
        yield return [new byte[] { 0x08 }];
        yield return [new byte[] { 0x09, 101 }];
    }

    [Fact]
    public void RejectsNonFiniteAndOutOfRangeNumbers()
    {
        Assert.False(PacketCodec.TryDecode(Frames.Aim(double.NaN, 0, 0), out _, out _));
        Assert.False(PacketCodec.TryDecode(Frames.Aim(1, float.PositiveInfinity, 0), out _, out _));
        Assert.False(PacketCodec.TryDecode(Frames.Aim(-1, 0, 0), out _, out _));
        Assert.False(PacketCodec.TryDecode(Frames.Aim(1, 800, 0), out _, out _));
        Assert.False(PacketCodec.TryDecode(Frames.Delta(0x06, float.NaN, 0), out _, out _));
        Assert.False(PacketCodec.TryDecode(Frames.Delta(0x0A, 0, 4001), out _, out _));
    }

    [Fact]
    public void RejectsBadHelloTextAndKind()
    {
        var badKind = Frames.Hello(kind: 9);
        Assert.False(PacketCodec.TryDecode(badKind, out _, out _));

        var controls = Frames.Hello("bad\nname");
        Assert.False(PacketCodec.TryDecode(controls, out _, out _));

        var invalid = new byte[19];
        invalid[0] = 0x01;
        invalid[17] = 1;
        invalid[18] = 0xFF;
        Assert.False(PacketCodec.TryDecode(invalid, out _, out _));

        var tooLong = Frames.Hello(new string('a', 61));
        Assert.False(PacketCodec.TryDecode(tooLong, out _, out _));
    }

    [Fact]
    public void AllowsEmptyHelloName()
    {
        var hello = Assert.IsType<Packet.Hello>(Decoded(Frames.Hello("")));
        Assert.Equal("", hello.Name);
    }

    [Fact]
    public void StatusRoundTripAndHostNameLimit()
    {
        var encoded = PacketCodec.EncodeStatus(LinkState.Approved, true, "Ironfist-extra-characters-past-limit");
        Assert.True(PacketCodec.TryDecodeStatus(encoded, out var state, out var paused, out var name, out var error), error);
        Assert.Equal(LinkState.Approved, state);
        Assert.True(paused);
        Assert.Equal(24, name.Length);
        Assert.StartsWith("Ironfist", name);
        Assert.False(PacketCodec.TryDecodeStatus([0x01], out _, out _, out _, out _));
    }

    [Fact]
    public void RejectsOversizedPacket()
    {
        Assert.False(PacketCodec.TryDecode(new byte[79], out _, out _));
    }

    private static Packet Decoded(byte[] frame)
    {
        Assert.True(PacketCodec.TryDecode(frame, out var packet, out var error), error);
        return packet!;
    }
}
