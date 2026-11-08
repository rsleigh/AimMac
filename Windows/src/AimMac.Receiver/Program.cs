namespace AimMac.Receiver;

internal static class Program
{
    private static Win32PointerSink? _pointer;

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        _pointer = new Win32PointerSink();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, _) => _pointer.Release();
        Application.ApplicationExit += (_, _) => _pointer.Release();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _pointer.Release();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => _pointer.Release();
        Application.Run(new MainForm(_pointer));
    }
}
