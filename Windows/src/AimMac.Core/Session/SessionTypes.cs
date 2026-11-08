namespace AimMac.Core.Session;

public enum PeerPhase
{
    WaitingHello,
    Pending,
    Approved,
    Denied,
}

public enum PointerOp
{
    Move,
    LeftDown,
    LeftUp,
    RightClick,
    Scroll,
}

/// <summary>
/// A cursor command produced by one engine call.
/// <see cref="Generation"/> matches <see cref="ReceiverEngine"/> only until the next mutating call.
/// </summary>
public readonly record struct PointerCommand(
    long Generation,
    PointerOp Op,
    double X,
    double Y,
    double Dx,
    double Dy);

public sealed class SessionOutcome
{
    public SessionOutcome(bool forceRelease, IReadOnlyList<PointerCommand> commands, IReadOnlyList<string> notes)
    {
        ForceRelease = forceRelease;
        Commands = commands;
        Notes = notes;
    }

    public bool ForceRelease { get; }
    public IReadOnlyList<PointerCommand> Commands { get; }
    public IReadOnlyList<string> Notes { get; }

    public static SessionOutcome Note(string note) =>
        new(false, [], [note]);
}

public sealed record PeerSnapshot(
    string ConnectionId,
    string Name,
    string Model,
    int? Battery,
    PeerPhase Phase,
    bool LinkEncrypted,
    string HelloLabel,
    string? OsDeviceId,
    byte? DeviceKind);

public interface IApprovalStore
{
    bool Contains(string osDeviceId);
    void Add(string osDeviceId);
    bool Remove(string osDeviceId);
    IReadOnlyCollection<string> All { get; }
}

public interface IClock
{
    double Seconds { get; }
}

public sealed class ManualClock : IClock
{
    public double Seconds { get; set; }
}

public sealed class SystemClock : IClock
{
    public double Seconds => Environment.TickCount64 / 1000.0;
}

public sealed class MemoryApprovalStore : IApprovalStore
{
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    public bool Contains(string osDeviceId) => _ids.Contains(osDeviceId);

    public void Add(string osDeviceId)
    {
        if (!Accept(osDeviceId))
        {
            return;
        }

        _ids.Add(osDeviceId);
    }

    public bool Remove(string osDeviceId) => _ids.Remove(osDeviceId);

    public IReadOnlyCollection<string> All => _ids.ToArray();

    internal static bool Accept(string osDeviceId) =>
        !string.IsNullOrWhiteSpace(osDeviceId)
        && osDeviceId.Length <= 300
        && osDeviceId.All(c => !char.IsControl(c));
}
