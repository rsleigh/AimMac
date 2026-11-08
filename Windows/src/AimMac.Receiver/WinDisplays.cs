using AimMac.Core.Aim;

namespace AimMac.Receiver;

internal sealed class WinDisplays : IDisplaySource
{
    public DisplayRect Primary =>
        Screen.PrimaryScreen is Screen screen ? Map(screen) : Fallback;

    public IReadOnlyList<DisplayRect> All
    {
        get
        {
            var screens = Screen.AllScreens.Select(Map).Where(rect => rect.Width > 1 && rect.Height > 1).ToArray();
            return screens.Length == 0 ? [Fallback] : screens;
        }
    }

    private static DisplayRect Map(Screen screen) =>
        new(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);

    private static DisplayRect Fallback => new(0, 0, 1920, 1080);
}
