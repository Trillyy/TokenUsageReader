namespace TokenUsageReader;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "TokenUsageReader.SingleInstance", out var isNew);
        if (!isNew) return;

        ApplicationConfiguration.Initialize();
        // A bug in an event handler must not silently kill a tray app: report it and keep running.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, Strings.ErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        // Forms, controls and title bars follow the Windows light/dark app mode.
        Application.SetColorMode(SystemColorMode.System);
        var config = AppConfig.Load(out var warning);
        Application.Run(new TrayContext(config, warning));
    }
}
