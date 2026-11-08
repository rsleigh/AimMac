using System.Runtime.InteropServices;
using AimMac.Core.Session;

namespace AimMac.Receiver;

/// <summary>
/// Sends cursor input for this process only. A button is released only if this
/// object pressed it, so a safety release does not click the user's mouse up
/// unless AimMac is the one holding the button.
/// The native struct layout is for 64-bit Windows.
/// </summary>
internal sealed class Win32PointerSink
{
    private readonly object _gate = new();
    private bool _left;
    private bool _right;

    public void Consume(SessionOutcome outcome, Func<PointerCommand, bool> stillCurrent)
    {
        lock (_gate)
        {
            if (outcome.ForceRelease)
            {
                ReleaseLocked();
            }

            foreach (var command in outcome.Commands)
            {
                if (!stillCurrent(command))
                {
                    continue;
                }

                switch (command.Op)
                {
                    case PointerOp.Move:
                        Move(command.X, command.Y, _left);
                        break;
                    case PointerOp.LeftDown:
                        Move(command.X, command.Y, dragging: false);
                        SetLeft(true);
                        break;
                    case PointerOp.LeftUp:
                        Move(command.X, command.Y, dragging: true);
                        SetLeft(false);
                        break;
                    case PointerOp.RightClick:
                        Move(command.X, command.Y, _left);
                        ClickRight();
                        break;
                    case PointerOp.Scroll:
                        Scroll(command.Dx, command.Dy);
                        break;
                }
            }
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            ReleaseLocked();
        }
    }

    private void ReleaseLocked()
    {
        if (_left)
        {
            SetLeft(false);
        }

        if (_right)
        {
            Send(0, 0, 0, Native.MouseEventRightUp);
            _right = false;
        }
    }

    private void SetLeft(bool down)
    {
        if (_left == down)
        {
            return;
        }

        Send(0, 0, 0, down ? Native.MouseEventLeftDown : Native.MouseEventLeftUp);
        _left = down;
    }

    private void ClickRight()
    {
        _right = true;
        Send(0, 0, 0, Native.MouseEventRightDown);
        Send(0, 0, 0, Native.MouseEventRightUp);
        _right = false;
    }

    private static void Move(double x, double y, bool dragging)
    {
        var pixelX = (int)Math.Round(x);
        var pixelY = (int)Math.Round(y);
        if (!dragging)
        {
            Native.SetCursorPos(pixelX, pixelY);
        }

        ToAbsolute(pixelX, pixelY, out var ax, out var ay);
        Send(ax, ay, 0, Native.MouseEventMove | Native.MouseEventAbsolute | Native.MouseEventVirtualDesk);
    }

    private static void Scroll(double dx, double dy)
    {
        var vertical = (int)Math.Round(Math.Clamp(dy, -4000, 4000));
        var horizontal = (int)Math.Round(Math.Clamp(dx, -4000, 4000));
        if (vertical != 0)
        {
            Send(0, 0, vertical, Native.MouseEventWheel);
        }

        if (horizontal != 0)
        {
            Send(0, 0, horizontal, Native.MouseEventHWheel);
        }
    }

    private static void ToAbsolute(int x, int y, out int ax, out int ay)
    {
        var originX = Native.GetSystemMetrics(Native.SmXVirtualScreen);
        var originY = Native.GetSystemMetrics(Native.SmYVirtualScreen);
        var width = Math.Max(1, Native.GetSystemMetrics(Native.SmCxVirtualScreen) - 1);
        var height = Math.Max(1, Native.GetSystemMetrics(Native.SmCyVirtualScreen) - 1);
        ax = (int)Math.Round((x - originX) * 65535.0 / width);
        ay = (int)Math.Round((y - originY) * 65535.0 / height);
        ax = Math.Clamp(ax, 0, 65535);
        ay = Math.Clamp(ay, 0, 65535);
    }

    private static void Send(int dx, int dy, int data, uint flags)
    {
        var input = new Native.Input
        {
            Type = Native.InputMouse,
            Mouse = new Native.MouseInput
            {
                Dx = dx,
                Dy = dy,
                MouseData = data,
                Flags = flags,
                Time = 0,
                Extra = 0,
            },
        };
        _ = Native.SendInput(1, [input], Marshal.SizeOf<Native.Input>());
    }

    private static class Native
    {
        public const int SmXVirtualScreen = 76;
        public const int SmYVirtualScreen = 77;
        public const int SmCxVirtualScreen = 78;
        public const int SmCyVirtualScreen = 79;
        public const int InputMouse = 0;
        public const uint MouseEventMove = 0x0001;
        public const uint MouseEventLeftDown = 0x0002;
        public const uint MouseEventLeftUp = 0x0004;
        public const uint MouseEventRightDown = 0x0008;
        public const uint MouseEventRightUp = 0x0010;
        public const uint MouseEventWheel = 0x0800;
        public const uint MouseEventHWheel = 0x1000;
        public const uint MouseEventAbsolute = 0x8000;
        public const uint MouseEventVirtualDesk = 0x4000;

        [StructLayout(LayoutKind.Sequential)]
        public struct MouseInput
        {
            public int Dx;
            public int Dy;
            public int MouseData;
            public uint Flags;
            public uint Time;
            public nint Extra;
        }

        [StructLayout(LayoutKind.Explicit, Size = 40)]
        public struct Input
        {
            [FieldOffset(0)] public int Type;
            [FieldOffset(8)] public MouseInput Mouse;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint count, Input[] inputs, int size);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);
    }
}
