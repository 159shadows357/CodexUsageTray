namespace CodexUsageTray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var singleInstance = new SingleInstanceCoordinator();
        if (!singleInstance.TryAcquire())
        {
            var command = args.Contains("--exit", StringComparer.OrdinalIgnoreCase)
                ? "exit"
                : args.Contains("--refresh", StringComparer.OrdinalIgnoreCase) ? "refresh" : "open";
            SingleInstanceCoordinator.SignalExistingAsync(command).GetAwaiter().GetResult();
            return;
        }

        if (args.Contains("--exit", StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var paths = new AppPaths();
        var store = new JsonStore();
        var logger = new AppLogger(paths);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, args) => logger.Error($"Unhandled UI error: {args.Exception.Message}");
        AppDomain.CurrentDomain.UnhandledException += (_, args) => logger.Error($"Unhandled process error: {args.ExceptionObject}");
        var uiSmoke = args.Contains("--ui-smoke", StringComparer.OrdinalIgnoreCase);
        var detailsSmoke = args.Contains("--details-smoke", StringComparer.OrdinalIgnoreCase);
        var menuSmoke = args.Contains("--menu-smoke", StringComparer.OrdinalIgnoreCase);
        var showOnStart = uiSmoke || args.Contains("--show", StringComparer.OrdinalIgnoreCase);
        using var context = new TrayApplicationContext(
            singleInstance,
            paths,
            store,
            logger,
            showOnStart,
            uiSmoke,
            detailsSmoke,
            menuSmoke);
        singleInstance.StartListening();
        Application.Run(context);
    }
}
