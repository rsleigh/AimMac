using AimMac.Core.Aim;
using AimMac.Core.Protocol;

namespace AimMac.Core.Session;

/// <summary>
/// Decides whether a connected controller may move the cursor.
/// Cursor commands are emitted only when the receiver is running, input is armed,
/// the peer has been explicitly approved, and the active security mode is satisfied.
/// Secure mode requires an encrypted link and a non-empty OS device id.
/// Prototype mode is plaintext compatibility with the current iPhone app: the hello
/// UUID is a label, never an approval key, and approvals are not persisted.
/// Every exit path that can end a gesture sets <see cref="SessionOutcome.ForceRelease"/>.
/// </summary>
public sealed class ReceiverEngine
{
    private const int MaxPeers = 4;
    private const double DragHoldSeconds = 0.33;
    private const double ClickFreezeSeconds = 0.12;

    private readonly IApprovalStore _approvals;
    private readonly IDisplaySource _displays;
    private readonly IClock _clock;
    private readonly AimTuning _tuning;
    private readonly Dictionary<string, Peer> _peers = new(StringComparer.Ordinal);
    private readonly Queue<double> _aimTimes = new();

    private long _generation;
    private bool _running;
    private bool _armed;
    private bool _prototype;
    private bool _paused;

    public ReceiverEngine(IApprovalStore approvals, IDisplaySource displays, IClock clock, AimTuning? tuning = null, string? hostName = null)
    {
        _approvals = approvals;
        _displays = displays;
        _clock = clock;
        _tuning = tuning ?? new AimTuning();
        HostName = PacketCodec.SanitizeHostName(hostName);
    }

    public bool Running => _running;
    public bool InputArmed => _armed;
    public bool PrototypeEnabled => _prototype;
    public bool GlobalPaused => _paused;
    public long AcceptedAims { get; private set; }
    public long RejectedFrames { get; private set; }
    public string HostName { get; }

    public int RecentAimCount
    {
        get
        {
            PruneAims(_clock.Seconds);
            return _aimTimes.Count;
        }
    }

    public SessionOutcome Start()
    {
        var generation = Begin();
        if (_running)
        {
            return SessionOutcome.Note("Receiver is already started.");
        }

        _running = true;
        var mode = _prototype
            ? "Prototype mode is on. The link is not authenticated. Cursor control is still off until you arm input."
            : "Secure mode is on. Cursor control is still off until you arm input.";
        return Finish(generation, false, mode);
    }

    public SessionOutcome Stop()
    {
        var generation = Begin();
        _running = false;
        _armed = false;
        _paused = false;
        return ClearPeers(generation, "Stopped. Any held mouse button is released and cursor control is off.");
    }

    public SessionOutcome EnablePrototype(bool warningAcknowledged)
    {
        var generation = Begin();
        if (!warningAcknowledged)
        {
            return Finish(generation, false, "Prototype mode was not enabled. The warning was not acknowledged.");
        }

        _prototype = true;
        _armed = false;
        return ClearPeers(generation, "Prototype mode enabled. Bluetooth encryption is off. The hello UUID is not authentication. Cursor control is off.");
    }

    public SessionOutcome EnableSecureMode()
    {
        var generation = Begin();
        _prototype = false;
        _armed = false;
        return ClearPeers(generation, "Secure mode selected. Live sessions were dropped and cursor control is off.");
    }

    public SessionOutcome Arm(bool prototypeWarningAcknowledged = false)
    {
        var generation = Begin();
        if (!_running)
        {
            return Finish(generation, false, "Start the receiver before arming cursor control.");
        }

        if (_prototype && !prototypeWarningAcknowledged)
        {
            return Finish(generation, false, "Cursor control stayed off. Prototype mode needs a fresh warning acknowledgement before arming.");
        }

        _armed = true;
        var note = _prototype
            ? "Cursor control armed in prototype mode. Approval is still required. This link is not authenticated."
            : "Cursor control armed. A device still has to be approved, and secure mode still requires an encrypted OS identity.";
        return Finish(generation, false, note);
    }

    public SessionOutcome ReleaseGesture()
    {
        var generation = Begin();
        EndAllGestures();
        return Finish(generation, true);
    }

    public SessionOutcome Disarm()
    {
        var generation = Begin();
        _armed = false;
        EndAllGestures();
        return Finish(generation, true, "Cursor control disarmed. Any held mouse button is released.");
    }

    public SessionOutcome SetPaused(bool paused)
    {
        var generation = Begin();
        _paused = paused;
        if (paused)
        {
            EndAllGestures();
            return Finish(generation, true, "Paused. Aim and buttons are ignored and any held mouse button is released.");
        }

        return Finish(generation, false, "Resumed. Cursor control still requires arming and an approved device.");
    }

    public SessionOutcome Center()
    {
        var generation = Begin();
        var commands = new List<PointerCommand>();
        foreach (var peer in _peers.Values)
        {
            if (peer.LastAz is double az && peer.LastEl is double el)
            {
                peer.Az0 = az;
                peer.El0 = el;
                peer.FilterX.Reset();
                peer.FilterY.Reset();
            }

            if (!CanInject(peer))
            {
                continue;
            }

            var center = new Vec(_displays.Primary.MidX, _displays.Primary.MidY);
            peer.LastPoint = center;
            commands.Add(new PointerCommand(generation, PointerOp.Move, center.X, center.Y, 0, 0));
        }

        return Finish(generation, false, commands, "Center stored. The next aim is the middle of the main display.");
    }

    public SessionOutcome Tick()
    {
        var generation = Begin();
        var commands = new List<PointerCommand>();
        var now = _clock.Seconds;
        foreach (var peer in _peers.Values)
        {
            if (peer.Pressed && !peer.Dragging && now - peer.PressHostTime >= DragHoldSeconds)
            {
                if (!CanInject(peer))
                {
                    peer.Pressed = false;
                    peer.Frozen = false;
                    continue;
                }

                peer.Dragging = true;
                peer.Frozen = false;
                peer.HoldsOsButton = true;
                var point = PressPoint(peer);
                peer.LastPoint = point;
                commands.Add(new PointerCommand(generation, PointerOp.LeftDown, point.X, point.Y, 0, 0));
            }

            if (peer.Frozen && !peer.Pressed && now >= peer.FreezeUntil)
            {
                peer.Frozen = false;
            }
        }

        return Finish(generation, false, commands);
    }

    public SessionOutcome Approve(string connectionId)
    {
        var generation = Begin();
        if (!_peers.TryGetValue(connectionId, out var peer))
        {
            return Finish(generation, false, "No such connection to approve.");
        }

        if (peer.Phase != PeerPhase.Pending)
        {
            return Finish(generation, false, "That device is not waiting for approval.");
        }

        if (!_prototype)
        {
            if (!peer.LinkEncrypted || string.IsNullOrEmpty(peer.OsDeviceId))
            {
                peer.Phase = PeerPhase.Denied;
                peer.Epoch++;
                return Finish(generation, true, "Approval refused. Secure mode has no encrypted OS device identity, so this device stays denied.");
            }
        }

        foreach (var other in _peers.Values)
        {
            if (other.ConnectionId == peer.ConnectionId || other.Phase != PeerPhase.Approved)
            {
                continue;
            }

            EndGesture(other);
            other.Phase = PeerPhase.Pending;
            other.Epoch++;
        }

        peer.Phase = PeerPhase.Approved;
        peer.Epoch++;
        if (!_prototype && peer.OsDeviceId is string osId)
        {
            _approvals.Add(osId);
        }

        var note = _prototype
            ? $"Approved “{peer.Name}” for this connection only. Prototype mode does not remember devices, and the hello UUID is not authentication."
            : $"Approved “{peer.Name}” for this encrypted OS identity. Cursor control still requires arming.";
        return Finish(generation, true, note);
    }

    public SessionOutcome Deny(string connectionId)
    {
        var generation = Begin();
        if (!_peers.TryGetValue(connectionId, out var peer))
        {
            return Finish(generation, false, "No such connection to deny.");
        }

        EndGesture(peer);
        peer.Phase = PeerPhase.Denied;
        peer.Epoch++;
        return Finish(generation, true, $"Denied “{peer.Name}”. Further input from this connection is ignored.");
    }

    public SessionOutcome Revoke(string connectionId)
    {
        var generation = Begin();
        if (!_peers.TryGetValue(connectionId, out var peer))
        {
            return Finish(generation, false, "No such connection to revoke.");
        }

        var removed = false;
        if (!string.IsNullOrEmpty(peer.OsDeviceId))
        {
            removed = _approvals.Remove(peer.OsDeviceId);
        }

        EndGesture(peer);
        peer.Phase = PeerPhase.Denied;
        peer.Epoch++;
        foreach (var other in _peers.Values)
        {
            if (other.ConnectionId == peer.ConnectionId || other.OsDeviceId != peer.OsDeviceId || string.IsNullOrEmpty(peer.OsDeviceId))
            {
                continue;
            }

            EndGesture(other);
            other.Phase = PeerPhase.Denied;
            other.Epoch++;
        }

        var memory = removed
            ? "Removed the saved secure-mode identity."
            : "Nothing was saved for this device. Prototype approvals are not stored.";
        return Finish(generation, true, $"Revoked “{peer.Name}”. {memory}");
    }

    public SessionOutcome OnConnected(string connectionId, string? osDeviceId, bool linkEncrypted) =>
        Connect(0, connectionId, osDeviceId, linkEncrypted);

    public SessionOutcome ReceiveFrame(string connectionId, string? osDeviceId, bool linkEncrypted, ReadOnlySpan<byte> frame)
    {
        var generation = Begin();
        if (!_running)
        {
            RejectedFrames++;
            return Finish(generation, true, "Ignored a frame while the receiver is stopped.");
        }

        if (string.IsNullOrWhiteSpace(connectionId))
        {
            RejectedFrames++;
            return Finish(generation, false, "Ignored a frame with no connection id.");
        }

        if (!_peers.ContainsKey(connectionId))
        {
            var connected = Connect(generation, connectionId, osDeviceId, linkEncrypted, alreadyBegun: true);
            if (!_peers.ContainsKey(connectionId))
            {
                RejectedFrames++;
                return connected;
            }
        }
        else
        {
            var peer = _peers[connectionId];
            peer.LinkEncrypted = linkEncrypted;
            if (!string.IsNullOrEmpty(osDeviceId))
            {
                peer.OsDeviceId = osDeviceId;
            }
        }

        if (!PacketCodec.TryDecode(frame, out var packet, out var error) || packet is null)
        {
            RejectedFrames++;
            return Finish(generation, false, "Rejected frame: " + error);
        }

        return Dispatch(generation, _peers[connectionId], packet);
    }

    public SessionOutcome OnDisconnected(string connectionId)
    {
        var generation = Begin();
        if (!_peers.TryGetValue(connectionId, out var peer))
        {
            return Finish(generation, true, "Disconnect ignored for an unknown connection. Mouse button released if this app was holding it.");
        }

        EndGesture(peer);
        _peers.Remove(connectionId);
        return Finish(generation, true, $"“{peer.Name}” disconnected. Held buttons are released and its aim queue is dropped.");
    }

    public SessionOutcome OnTransportError(string connectionId, string message)
    {
        var generation = Begin();
        var safe = string.IsNullOrWhiteSpace(message) ? "transport error" : message.Trim();
        if (safe.Length > 180)
        {
            safe = safe[..180];
        }

        if (_peers.TryGetValue(connectionId, out var peer))
        {
            EndGesture(peer);
            _peers.Remove(connectionId);
        }

        return Finish(generation, true, "Link error (" + safe + "). Held buttons are released and queued input is dropped.");
    }

    public bool IsCommandCurrent(PointerCommand command) => command.Generation == _generation;

    public byte[] StatusFor(string connectionId)
    {
        if (!_peers.TryGetValue(connectionId, out var peer))
        {
            return PacketCodec.EncodeStatus(LinkState.Waiting, _paused, HostName);
        }

        var state = peer.Phase switch
        {
            PeerPhase.Approved => LinkState.Approved,
            PeerPhase.Denied => LinkState.Denied,
            _ => LinkState.Waiting,
        };
        return PacketCodec.EncodeStatus(state, _paused || peer.DevicePaused, HostName);
    }

    public IReadOnlyList<PeerSnapshot> Peers() =>
        _peers.Values.Select(p => new PeerSnapshot(
            p.ConnectionId,
            p.Name,
            p.Model,
            p.Battery,
            p.Phase,
            p.LinkEncrypted,
            p.HelloLabel,
            p.OsDeviceId,
            p.DeviceKind)).ToArray();

    public void SetTuning(int rangeIndex, int smoothIndex, bool invertX, bool invertY)
    {
        _ = Begin();
        _tuning.RangeIndex = rangeIndex;
        _tuning.SmoothIndex = smoothIndex;
        _tuning.InvertX = invertX;
        _tuning.InvertY = invertY;
        var smooth = _tuning.Smoothing;
        foreach (var peer in _peers.Values)
        {
            peer.FilterX.MinCutoff = smooth.Cutoff;
            peer.FilterX.Beta = smooth.Beta;
            peer.FilterY.MinCutoff = smooth.Cutoff;
            peer.FilterY.Beta = smooth.Beta;
            peer.FilterX.Reset();
            peer.FilterY.Reset();
        }
    }

    private SessionOutcome Connect(long generation, string connectionId, string? osDeviceId, bool linkEncrypted, bool alreadyBegun = false)
    {
        if (!alreadyBegun)
        {
            generation = Begin();
        }

        if (!_running)
        {
            return Finish(generation, true, "Ignored a connection while the receiver is stopped.");
        }

        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return Finish(generation, false, "Rejected a connection with no id.");
        }

        if (_peers.ContainsKey(connectionId))
        {
            return Finish(generation, false, "Connection is already open.");
        }

        if (_peers.Count >= MaxPeers)
        {
            return Finish(generation, false, "Rejected an extra connection. Only four controllers are tracked, and unknown devices cannot move the cursor.");
        }

        var os = string.IsNullOrWhiteSpace(osDeviceId) ? null : osDeviceId.Trim();
        _peers.Add(connectionId, new Peer(connectionId, os, linkEncrypted, _tuning.Smoothing));
        return Finish(generation, false, "Device connected. Waiting for hello. No cursor control yet.");
    }

    private SessionOutcome Dispatch(long generation, Peer peer, Packet packet)
    {
        switch (packet)
        {
            case Packet.Hello hello:
                return ApplyHello(generation, peer, hello);
            case Packet.Info info:
                peer.Battery = info.Battery == 255 ? null : info.Battery;
                peer.Model = info.Model;
                return Finish(generation, false);
            case Packet.Pause pause:
                if (peer.Phase != PeerPhase.Approved || !_running)
                {
                    return Finish(generation, false, "Ignored pause from a device that is not approved.");
                }

                peer.DevicePaused = pause.On;
                if (pause.On)
                {
                    EndGesture(peer);
                    return Finish(generation, true, "Device paused. Held buttons are released.");
                }

                return Finish(generation, false, "Device resumed aiming.");
            case Packet.Aim aim:
                return ApplyAim(generation, peer, aim);
            case Packet.Press press:
                return ApplyPress(generation, peer, press.Time);
            case Packet.Release release:
                return ApplyRelease(generation, peer, release.Time);
            case Packet.Right right:
                return ApplyRight(generation, peer, right.Time);
            case Packet.Scroll scroll:
                return ApplyScroll(generation, peer, scroll.Dx, scroll.Dy);
            case Packet.Move move:
                return ApplyMove(generation, peer, move.Dx, move.Dy);
            case Packet.Center:
                return ApplyCenter(generation, peer);
            default:
                RejectedFrames++;
                return Finish(generation, false, "Ignored an unhandled packet.");
        }
    }

    private SessionOutcome ApplyHello(long generation, Peer peer, Packet.Hello hello)
    {
        if (peer.Phase == PeerPhase.Denied)
        {
            return Finish(generation, false, "Ignored hello from a denied connection.");
        }

        peer.DeviceKind = hello.DeviceKind;
        peer.HelloLabel = PacketCodec.FormatUuid(hello.UuidBytes);
        peer.Name = string.IsNullOrWhiteSpace(hello.Name) ? "Unknown device" : hello.Name.Trim();

        if (_prototype)
        {
            if (peer.Phase != PeerPhase.Approved)
            {
                peer.Phase = PeerPhase.Pending;
            }

            return Finish(generation, false, $"Hello from “{peer.Name}”. Prototype label {peer.HelloLabel}. Waiting for an explicit approval. The label is not a credential.");
        }

        if (!peer.LinkEncrypted || string.IsNullOrEmpty(peer.OsDeviceId))
        {
            peer.Phase = PeerPhase.Denied;
            peer.Epoch++;
            EndGesture(peer);
            return Finish(generation, true, "Denied hello. Secure mode requires an encrypted link and an OS device id. The hello UUID was not accepted as identity.");
        }

        var someoneElse = _peers.Values.Any(p => p.ConnectionId != peer.ConnectionId && p.Phase == PeerPhase.Approved);
        if (!someoneElse && peer.Phase != PeerPhase.Approved && _approvals.Contains(peer.OsDeviceId))
        {
            peer.Phase = PeerPhase.Approved;
            return Finish(generation, false, $"Restored approval for “{peer.Name}” from the saved OS identity. Cursor control still requires arming. The hello UUID is only a label.");
        }

        if (peer.Phase != PeerPhase.Approved)
        {
            peer.Phase = PeerPhase.Pending;
        }

        return Finish(generation, false, $"Hello from “{peer.Name}”. Waiting for approval. Hello label {peer.HelloLabel} is not a credential.");
    }

    private SessionOutcome ApplyAim(long generation, Peer peer, Packet.Aim aim)
    {
        if (!AcceptInput(peer))
        {
            return Finish(generation, false);
        }

        if (peer.LastDeviceTime is double previous && aim.Time < previous)
        {
            RejectedFrames++;
            return Finish(generation, false, "Dropped an aim sample that went backwards in time.");
        }

        peer.LastDeviceTime = aim.Time;
        peer.LastAz = aim.Azimuth;
        peer.LastEl = aim.Elevation;
        if (peer.Az0 is null || peer.El0 is null)
        {
            peer.Az0 = aim.Azimuth;
            peer.El0 = aim.Elevation;
        }

        var dx = -AimProjection.Wrap(aim.Azimuth - peer.Az0.Value);
        var dy = -(aim.Elevation - peer.El0.Value);
        if (_tuning.InvertX) dx = -dx;
        if (_tuning.InvertY) dy = -dy;
        var filteredX = peer.FilterX.Filter(dx, aim.Time);
        var filteredY = peer.FilterY.Filter(dy, aim.Time);
        var primary = _displays.Primary;
        if (primary.Width < 2 || primary.Height < 2)
        {
            return Finish(generation, false, "No usable main display.");
        }

        var point = AimProjection.ClampToDisplays(
            AimProjection.Project(filteredX, filteredY, _tuning.RangeDegrees, primary),
            _displays.All);
        Remember(peer, aim.Time, point);
        AcceptedAims++;
        _aimTimes.Enqueue(_clock.Seconds);
        PruneAims(_clock.Seconds);
        if (!CanInject(peer) || peer.Frozen)
        {
            return Finish(generation, false);
        }

        peer.LastPoint = point;
        return Finish(generation, false, [new PointerCommand(generation, PointerOp.Move, point.X, point.Y, 0, 0)]);
    }

    private SessionOutcome ApplyPress(long generation, Peer peer, double deviceTime)
    {
        if (!AcceptInput(peer) || peer.Pressed)
        {
            return Finish(generation, false);
        }

        peer.Pressed = true;
        peer.Dragging = false;
        peer.PressSerial++;
        peer.PressHostTime = _clock.Seconds;
        peer.Frozen = true;
        var point = PointAt(peer, deviceTime);
        peer.PressX = point.X;
        peer.PressY = point.Y;
        return Finish(generation, false);
    }

    private SessionOutcome ApplyRelease(long generation, Peer peer, double deviceTime)
    {
        if (!peer.Pressed)
        {
            return Finish(generation, peer.HoldsOsButton);
        }

        var dragging = peer.Dragging;
        peer.Pressed = false;
        peer.Dragging = false;
        peer.PressSerial++;
        var point = dragging ? peer.LastPoint ?? PressPoint(peer) : PointAt(peer, deviceTime);
        if (dragging || peer.HoldsOsButton)
        {
            peer.HoldsOsButton = false;
            peer.Frozen = false;
            peer.LastPoint = point;
            return Finish(generation, true, [new PointerCommand(generation, PointerOp.LeftUp, point.X, point.Y, 0, 0)]);
        }

        if (!CanInject(peer))
        {
            peer.Frozen = false;
            return Finish(generation, false);
        }

        peer.FreezeUntil = _clock.Seconds + ClickFreezeSeconds;
        peer.Frozen = true;
        peer.LastPoint = point;
        return Finish(
            generation,
            false,
            [
                new PointerCommand(generation, PointerOp.LeftDown, point.X, point.Y, 0, 0),
                new PointerCommand(generation, PointerOp.LeftUp, point.X, point.Y, 0, 0),
            ]);
    }

    private SessionOutcome ApplyRight(long generation, Peer peer, double deviceTime)
    {
        if (!AcceptInput(peer) || !CanInject(peer))
        {
            return Finish(generation, false);
        }

        var point = PointAt(peer, deviceTime);
        var release = peer.HoldsOsButton || peer.Dragging;
        if (release)
        {
            EndGesture(peer);
        }

        return Finish(
            generation,
            release,
            [new PointerCommand(generation, PointerOp.RightClick, point.X, point.Y, 0, 0)],
            release ? "Released the left button before the right click." : null);
    }

    private SessionOutcome ApplyScroll(long generation, Peer peer, float dx, float dy)
    {
        if (!AcceptInput(peer) || !CanInject(peer))
        {
            return Finish(generation, false);
        }

        return Finish(generation, false, [new PointerCommand(generation, PointerOp.Scroll, 0, 0, dx, dy)]);
    }

    private SessionOutcome ApplyMove(long generation, Peer peer, float dx, float dy)
    {
        if (!AcceptInput(peer) || !CanInject(peer) || peer.Frozen)
        {
            return Finish(generation, false);
        }

        var primary = _displays.Primary;
        var origin = peer.LastPoint ?? new Vec(primary.MidX, primary.MidY);
        var point = AimProjection.ClampToDisplays(new Vec(origin.X + dx, origin.Y + dy), _displays.All);
        peer.History.Clear();
        peer.LastPoint = point;
        return Finish(generation, false, [new PointerCommand(generation, PointerOp.Move, point.X, point.Y, dx, dy)]);
    }

    private SessionOutcome ApplyCenter(long generation, Peer peer)
    {
        if (!AcceptInput(peer))
        {
            return Finish(generation, false);
        }

        if (peer.LastAz is double az && peer.LastEl is double el)
        {
            peer.Az0 = az;
            peer.El0 = el;
        }

        peer.FilterX.Reset();
        peer.FilterY.Reset();
        if (!CanInject(peer))
        {
            return Finish(generation, false, "Center stored. The cursor stays put until input is armed.");
        }

        var center = new Vec(_displays.Primary.MidX, _displays.Primary.MidY);
        peer.LastPoint = center;
        peer.Frozen = false;
        return Finish(generation, false, [new PointerCommand(generation, PointerOp.Move, center.X, center.Y, 0, 0)], "Centered.");
    }

    private bool AcceptInput(Peer peer) =>
        peer.Phase == PeerPhase.Approved && _running && !_paused;

    private bool CanInject(Peer peer)
    {
        if (!_running || !_armed || _paused || peer.DevicePaused || peer.Phase != PeerPhase.Approved)
        {
            return false;
        }

        if (_prototype)
        {
            return true;
        }

        return peer.LinkEncrypted && !string.IsNullOrEmpty(peer.OsDeviceId);
    }

    private void EndAllGestures()
    {
        foreach (var peer in _peers.Values)
        {
            EndGesture(peer);
        }
    }

    private static void EndGesture(Peer peer)
    {
        peer.Pressed = false;
        peer.Dragging = false;
        peer.Frozen = false;
        peer.HoldsOsButton = false;
        peer.PressSerial++;
        peer.History.Clear();
    }

    private SessionOutcome ClearPeers(long generation, string note)
    {
        foreach (var peer in _peers.Values)
        {
            EndGesture(peer);
        }

        _peers.Clear();
        _aimTimes.Clear();
        return Finish(generation, true, note);
    }

    private static Vec PressPoint(Peer peer) =>
        peer.PressX is double x && peer.PressY is double y
            ? new Vec(x, y)
            : peer.LastPoint ?? new Vec(0, 0);

    private Vec PointAt(Peer peer, double deviceTime)
    {
        Vec? hit = null;
        foreach (var sample in peer.History)
        {
            if (sample.Time <= deviceTime)
            {
                hit = sample.Point;
            }
        }

        if (hit is Vec found)
        {
            return found;
        }

        if (peer.LastPoint is Vec last)
        {
            return last;
        }

        var primary = _displays.Primary;
        return new Vec(primary.MidX, primary.MidY);
    }

    private static void Remember(Peer peer, double time, Vec point)
    {
        peer.History.Add(new Sample(time, point));
        if (peer.History.Count > 400)
        {
            peer.History.RemoveRange(0, peer.History.Count - 400);
        }

        peer.History.RemoveAll(sample => time - sample.Time > 1.0);
    }

    private void PruneAims(double now)
    {
        while (_aimTimes.Count > 0 && now - _aimTimes.Peek() > 1.0)
        {
            _aimTimes.Dequeue();
        }
    }

    private long Begin() => ++_generation;

    private SessionOutcome Finish(long generation, bool forceRelease, string? note = null) =>
        Finish(generation, forceRelease, [], note);

    private SessionOutcome Finish(long generation, bool forceRelease, IReadOnlyList<PointerCommand> commands, string? note = null)
    {
        _ = generation;
        IReadOnlyList<string> notes = string.IsNullOrEmpty(note) ? [] : [note];
        return new SessionOutcome(forceRelease, commands, notes);
    }

    private sealed class Peer
    {
        public Peer(string connectionId, string? osDeviceId, bool linkEncrypted, (double Cutoff, double Beta) smooth)
        {
            ConnectionId = connectionId;
            OsDeviceId = osDeviceId;
            LinkEncrypted = linkEncrypted;
            FilterX = new OneEuro(smooth.Cutoff, smooth.Beta);
            FilterY = new OneEuro(smooth.Cutoff, smooth.Beta);
        }

        public string ConnectionId { get; }
        public string? OsDeviceId { get; set; }
        public bool LinkEncrypted { get; set; }
        public int Epoch { get; set; } = 1;
        public PeerPhase Phase { get; set; } = PeerPhase.WaitingHello;
        public string Name { get; set; } = "Unknown device";
        public string Model { get; set; } = "";
        public int? Battery { get; set; }
        public byte? DeviceKind { get; set; }
        public string HelloLabel { get; set; } = "";
        public bool DevicePaused { get; set; }
        public double? Az0 { get; set; }
        public double? El0 { get; set; }
        public double? LastAz { get; set; }
        public double? LastEl { get; set; }
        public double? LastDeviceTime { get; set; }
        public Vec? LastPoint { get; set; }
        public List<Sample> History { get; } = [];
        public OneEuro FilterX { get; }
        public OneEuro FilterY { get; }
        public bool Pressed { get; set; }
        public bool Dragging { get; set; }
        public bool Frozen { get; set; }
        public bool HoldsOsButton { get; set; }
        public double PressHostTime { get; set; }
        public double? PressX { get; set; }
        public double? PressY { get; set; }
        public int PressSerial { get; set; }
        public double FreezeUntil { get; set; }
    }

    private readonly record struct Sample(double Time, Vec Point);
}
