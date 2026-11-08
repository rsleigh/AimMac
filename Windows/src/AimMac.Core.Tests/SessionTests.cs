using AimMac.Core.Aim;
using AimMac.Core.Session;
using Xunit;

namespace AimMac.Core.Tests;

public class SessionTests
{
    [Fact]
    public void InputIsOffUntilStartedApprovedAndArmed()
    {
        var fixture = Fixture.Create();
        var idle = fixture.Engine.ReceiveFrame("c1", "os-1", true, Frames.Aim(1, 0, 0));
        Assert.Empty(idle.Commands);
        Assert.Contains(idle.Notes, n => n.Contains("stopped", StringComparison.OrdinalIgnoreCase));

        fixture.Engine.Start();
        fixture.Engine.OnConnected("c1", "os-1", true);
        fixture.Engine.ReceiveFrame("c1", "os-1", true, Frames.Hello());
        var before = fixture.Engine.ReceiveFrame("c1", "os-1", true, Frames.Aim(1, 0, 0));
        Assert.Empty(before.Commands);
        Assert.Equal(PeerPhase.Pending, fixture.Engine.Peers().Single().Phase);

        fixture.Engine.Approve("c1");
        var armedOff = fixture.Engine.ReceiveFrame("c1", "os-1", true, Frames.Aim(1, 0, 0));
        Assert.Empty(armedOff.Commands);
        Assert.False(fixture.Engine.InputArmed);

        fixture.Engine.Arm();
        var move = fixture.Engine.ReceiveFrame("c1", "os-1", true, Frames.Aim(1.1, 8, 0));
        Assert.Contains(move.Commands, c => c.Op == PointerOp.Move);
        Assert.True(move.Commands[0].X < 960);
    }

    [Fact]
    public void SecureModeFailsClosedWithoutEncryptionOrOsIdentity()
    {
        var unencrypted = Fixture.Create();
        unencrypted.Engine.Start();
        var denied = unencrypted.Engine.ReceiveFrame("c1", "os-1", false, Frames.Hello());
        Assert.Equal(PeerPhase.Denied, unencrypted.Engine.Peers().Single().Phase);
        Assert.Contains("Denied", denied.Notes[0], StringComparison.Ordinal);
        Assert.Equal(PeerPhase.Denied, unencrypted.Engine.Peers().Single().Phase);

        var anonymous = Fixture.Create();
        anonymous.Engine.Start();
        anonymous.Engine.ReceiveFrame("c1", null, true, Frames.Hello());
        Assert.Equal(PeerPhase.Denied, anonymous.Engine.Peers().Single().Phase);
        Assert.Empty(anonymous.Store.All);
    }

    [Fact]
    public void SavedApprovalIsNotAHelloUuidAndStillNeedsArming()
    {
        var fixture = Fixture.Create();
        fixture.Store.Add("os-1");
        fixture.Engine.Start();
        fixture.Engine.ReceiveFrame("c1", "os-1", true, Frames.Hello("Phone"));
        var peer = fixture.Engine.Peers().Single();
        Assert.Equal(PeerPhase.Approved, peer.Phase);
        Assert.NotEqual(peer.HelloLabel, peer.OsDeviceId);
        var move = fixture.Engine.ReceiveFrame("c1", "os-1", true, Frames.Aim(1, 0, 0));
        Assert.Empty(move.Commands);
        fixture.Engine.Arm();
        var armed = fixture.Engine.ReceiveFrame("c1", "os-1", true, Frames.Aim(1.2, 6, 0));
        Assert.Contains(armed.Commands, c => c.Op == PointerOp.Move);
    }

    [Fact]
    public void PrototypeModeRequiresWarningAndDoesNotPersistOrTreatUuidAsAuth()
    {
        var fixture = Fixture.Create();
        fixture.Store.Add("os-1");
        var refused = fixture.Engine.EnablePrototype(false);
        Assert.False(fixture.Engine.PrototypeEnabled);
        Assert.Contains("not enabled", refused.Notes[0], StringComparison.OrdinalIgnoreCase);

        fixture.Engine.EnablePrototype(true);
        fixture.Engine.Start();
        Assert.False(fixture.Engine.InputArmed);
        fixture.Engine.ReceiveFrame("c1", "os-other", false, Frames.Hello());
        Assert.Equal(PeerPhase.Pending, fixture.Engine.Peers().Single().Phase);
        fixture.Engine.Approve("c1");
        Assert.Equal(["os-1"], fixture.Store.All);
        var blocked = fixture.Engine.Arm();
        Assert.False(fixture.Engine.InputArmed);
        Assert.Contains("Prototype", blocked.Notes[0], StringComparison.Ordinal);
        fixture.Engine.Arm(prototypeWarningAcknowledged: true);
        var move = fixture.Engine.ReceiveFrame("c1", "os-1", false, Frames.Aim(2, 4, 0));
        Assert.Contains(move.Commands, c => c.Op == PointerOp.Move);
    }

    [Fact]
    public void DisconnectDenyPauseStopRevokeAndErrorsReleaseADrag()
    {
        AssertReleases(e => e.OnDisconnected("c1"));
        AssertReleases(e => e.Deny("c1"));
        AssertReleases(e => e.SetPaused(true));
        AssertReleases(e => e.Disarm());
        AssertReleases(e => e.Stop());
        AssertReleases(e => e.Revoke("c1"));
        AssertReleases(e => e.OnTransportError("c1", "radio reset"));
        AssertReleases(e => e.EnableSecureMode());
        AssertReleases(e => e.EnablePrototype(true));
        AssertReleases(e => e.ReleaseGesture());
    }

    [Fact]
    public void StaleMoveAfterDisconnectIsNotCurrentAndInvalidFrameDoesNotRelease()
    {
        var fixture = Armed();
        var move = fixture.Engine.ReceiveFrame("c1", "os-1", true, Frames.Aim(1.2, 5, 0));
        var command = Assert.Single(move.Commands);
        fixture.Engine.OnDisconnected("c1");
        Assert.False(fixture.Engine.IsCommandCurrent(command));

        var dragging = Armed();
        Hold(dragging);
        var rejected = dragging.Engine.ReceiveFrame("c1", "os-1", true, [0x02, 1, 2, 3]);
        Assert.False(rejected.ForceRelease);
        Assert.Empty(rejected.Commands);
        var backwards = dragging.Engine.ReceiveFrame("c1", "os-1", true, Frames.Aim(0.1, 1, 0));
        Assert.False(backwards.ForceRelease);
        var still = dragging.Engine.ReceiveFrame("c1", "os-1", true, Frames.Aim(1.5, 9, 0));
        Assert.Contains(still.Commands, c => c.Op == PointerOp.Move);
    }

    [Fact]
    public void ClickIsDownAndUpAndHoldBecomesDragUntilRelease()
    {
        var click = Armed();
        Aim(click, 1, 0, 0);
        click.Engine.ReceiveFrame("c1", "os-1", true, Frames.Timed(0x03, 1));
        var up = click.Engine.ReceiveFrame("c1", "os-1", true, Frames.Timed(0x04, 1));
        Assert.Equal([PointerOp.LeftDown, PointerOp.LeftUp], up.Commands.Select(c => c.Op).ToArray());
        Assert.False(up.ForceRelease);
        var later = click.Engine.Tick();
        Assert.DoesNotContain(later.Commands, c => c.Op == PointerOp.LeftDown);

        var drag = Armed();
        var down = Hold(drag);
        Assert.Contains(down.Commands, c => c.Op == PointerOp.LeftDown);
        var release = drag.Engine.ReceiveFrame("c1", "os-1", true, Frames.Timed(0x04, 1.2));
        Assert.True(release.ForceRelease);
        Assert.Contains(release.Commands, c => c.Op == PointerOp.LeftUp);
    }

    [Fact]
    public void CenterRezerosAndInvertFlipsTheHorizontalAxis()
    {
        var fixture = Armed();
        Aim(fixture, 1, 0, 0);
        Aim(fixture, 1.2, 10, 4);
        fixture.Engine.ReceiveFrame("c1", "os-1", true, [0x07]);
        var recentered = fixture.Engine.ReceiveFrame("c1", "os-1", true, Frames.Aim(1.4, 10, 4));
        var center = Assert.Single(recentered.Commands);
        Assert.Equal(960, center.X, 3);
        Assert.Equal(540, center.Y, 3);

        fixture.Engine.SetTuning(1, 1, true, false);
        var inverted = fixture.Engine.ReceiveFrame("c1", "os-1", true, Frames.Aim(1.6, 18, 4));
        Assert.True(inverted.Commands.Single().X > 960);
    }

    [Fact]
    public void ASecondDeviceCannotInjectAndApprovalReleasesTheFirst()
    {
        var fixture = Armed();
        Aim(fixture, 1, 0, 0);
        Hold(fixture);
        fixture.Engine.ReceiveFrame("c2", "os-2", true, Frames.Hello("Other"));
        var sneaky = fixture.Engine.ReceiveFrame("c2", "os-2", true, Frames.Aim(1, 20, 0));
        Assert.Empty(sneaky.Commands);
        var swapped = fixture.Engine.Approve("c2");
        Assert.True(swapped.ForceRelease);
        Assert.Equal(PeerPhase.Pending, fixture.Engine.Peers().Single(p => p.ConnectionId == "c1").Phase);
        Assert.Equal(PeerPhase.Approved, fixture.Engine.Peers().Single(p => p.ConnectionId == "c2").Phase);
    }

    [Fact]
    public void RevokeRemovesOnlyTheSecureIdentity()
    {
        var fixture = Armed();
        Assert.Contains("os-1", fixture.Store.All);
        fixture.Engine.Revoke("c1");
        Assert.DoesNotContain("os-1", fixture.Store.All);
        Assert.Equal(PeerPhase.Denied, fixture.Engine.Peers().Single().Phase);
        var again = fixture.Engine.ReceiveFrame("c1", "os-1", true, Frames.Aim(3, 1, 0));
        Assert.Empty(again.Commands);
    }

    [Fact]
    public void RelativeMoveScrollAndRightClickAreGated()
    {
        var fixture = Fixture.Create();
        fixture.Engine.Start();
        fixture.Engine.ReceiveFrame("c1", "os-1", true, Frames.Hello());
        Assert.Empty(fixture.Engine.ReceiveFrame("c1", "os-1", true, Frames.Delta(0x0A, 10, 0)).Commands);
        fixture.Engine.Approve("c1");
        fixture.Engine.Arm();
        var move = fixture.Engine.ReceiveFrame("c1", "os-1", true, Frames.Delta(0x0A, 12, -4));
        Assert.Equal(PointerOp.Move, move.Commands.Single().Op);
        Assert.Equal(972, move.Commands[0].X, 3);
        var scroll = fixture.Engine.ReceiveFrame("c1", "os-1", true, Frames.Delta(0x06, 0, 30));
        Assert.Equal(PointerOp.Scroll, scroll.Commands.Single().Op);
        Assert.Equal(30, scroll.Commands[0].Dy);
        var right = fixture.Engine.ReceiveFrame("c1", "os-1", true, Frames.Timed(0x05, 2));
        Assert.Equal(PointerOp.RightClick, right.Commands.Single().Op);
    }

    [Fact]
    public void DevicePauseReleasesAndDropsLaterAimUntilResume()
    {
        var fixture = Armed();
        Hold(fixture);
        var paused = fixture.Engine.ReceiveFrame("c1", "os-1", true, [0x08, 1]);
        Assert.True(paused.ForceRelease);
        var aim = fixture.Engine.ReceiveFrame("c1", "os-1", true, Frames.Aim(2, 12, 0));
        Assert.Empty(aim.Commands);
        fixture.Engine.ReceiveFrame("c1", "os-1", true, [0x08, 0]);
        var resumed = fixture.Engine.ReceiveFrame("c1", "os-1", true, Frames.Aim(2.2, 12, 0));
        Assert.Contains(resumed.Commands, c => c.Op == PointerOp.Move);
    }

    [Fact]
    public void StopThenLateFrameDoesNotRecreateAController()
    {
        var fixture = Armed();
        fixture.Engine.Stop();
        var late = fixture.Engine.ReceiveFrame("c1", "os-1", true, Frames.Aim(4, 1, 0));
        Assert.True(late.ForceRelease);
        Assert.Empty(fixture.Engine.Peers());
    }

    [Fact]
    public void FifthConnectionIsIgnored()
    {
        var fixture = Fixture.Create();
        fixture.Engine.Start();
        for (var i = 0; i < 4; i++)
        {
            fixture.Engine.OnConnected("c" + i, "os-" + i, true);
        }

        var extra = fixture.Engine.OnConnected("c4", "os-4", true);
        Assert.Contains("Rejected", extra.Notes[0]);
        Assert.Equal(4, fixture.Engine.Peers().Count);
    }

    [Fact]
    public void CorruptApprovalFileFailsClosed()
    {
        var path = Path.Combine(Path.GetTempPath(), "aimmac-test-" + Guid.NewGuid().ToString("n") + ".json");
        File.WriteAllText(path, "{not-json");
        var store = new JsonApprovalStore(path);
        Assert.Empty(store.All);
        store.Add("os-7");
        var reloaded = new JsonApprovalStore(path);
        Assert.Equal(["os-7"], reloaded.All);
        Assert.False(reloaded.Remove("missing"));
        Assert.True(reloaded.Remove("os-7"));
        Assert.Empty(new JsonApprovalStore(path).All);
        store.Add("bad\nid");
        Assert.DoesNotContain(store.All, id => id.Contains('\n'));
    }

    private static void AssertReleases(Func<ReceiverEngine, SessionOutcome> end)
    {
        var fixture = Armed();
        var down = Hold(fixture);
        Assert.Contains(down.Commands, command => command.Op == PointerOp.LeftDown);
        var outcome = end(fixture.Engine);
        Assert.True(outcome.ForceRelease);
        var sink = new RecordingSink();
        sink.Hold();
        sink.Consume(outcome, fixture.Engine);
        Assert.False(sink.LeftHeld);
        Assert.Contains("safety-up", sink.Events);
    }

    private static SessionOutcome Hold(Fixture fixture)
    {
        Aim(fixture, 1, 0, 0);
        fixture.Engine.ReceiveFrame("c1", "os-1", true, Frames.Timed(0x03, 1));
        fixture.Clock.Seconds += 0.33;
        return fixture.Engine.Tick();
    }

    private static void Aim(Fixture fixture, double time, float azimuth, float elevation) =>
        fixture.Engine.ReceiveFrame("c1", "os-1", true, Frames.Aim(time, azimuth, elevation));

    private static Fixture Armed()
    {
        var fixture = Fixture.Create();
        fixture.Engine.Start();
        fixture.Engine.ReceiveFrame("c1", "os-1", true, Frames.Hello("Phone"));
        fixture.Engine.Approve("c1");
        fixture.Engine.Arm();
        return fixture;
    }
}

public class RecordingSink
{
    public List<string> Events { get; } = [];
    public bool LeftHeld { get; private set; }

    public void Hold() => LeftHeld = true;

    public void Consume(SessionOutcome outcome, ReceiverEngine engine)
    {
        if (outcome.ForceRelease)
        {
            Release();
        }

        foreach (var command in outcome.Commands)
        {
            if (!engine.IsCommandCurrent(command) && !outcome.ForceRelease)
            {
                Events.Add("drop");
                continue;
            }

            if (command.Op == PointerOp.LeftDown)
            {
                LeftHeld = true;
            }

            if (command.Op == PointerOp.LeftUp)
            {
                LeftHeld = false;
            }

            Events.Add(command.Op.ToString());
        }
    }

    public void Release()
    {
        if (!LeftHeld)
        {
            return;
        }

        LeftHeld = false;
        Events.Add("safety-up");
    }
}

public sealed class Fixture
{
    private Fixture(ReceiverEngine engine, MemoryApprovalStore store, ManualClock clock)
    {
        Engine = engine;
        Store = store;
        Clock = clock;
    }

    public ReceiverEngine Engine { get; }
    public MemoryApprovalStore Store { get; }
    public ManualClock Clock { get; }

    public static Fixture Create()
    {
        var store = new MemoryApprovalStore();
        var clock = new ManualClock { Seconds = 10 };
        var displays = new FixedDisplays();
        var engine = new ReceiverEngine(store, displays, clock, hostName: "Ironfist");
        return new Fixture(engine, store, clock);
    }

    private sealed class FixedDisplays : IDisplaySource
    {
        public DisplayRect Primary { get; } = new(0, 0, 1920, 1080);
        public IReadOnlyList<DisplayRect> All { get; } = [new(0, 0, 1920, 1080)];
    }
}
