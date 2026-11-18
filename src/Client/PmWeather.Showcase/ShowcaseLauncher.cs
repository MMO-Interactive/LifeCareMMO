using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

internal static class ShowcaseLauncher
{
    public static async Task<int> RunAsync(string[] args)
    {
        // Explicit command-line modes keep their existing capture/probe behavior.
        // Explorer supplies no arguments: launch account login and the authenticated game menu.
        if (args.Length != 0) return await Showcase.RunAsync(args);

        string dataRoot = Path.Combine(System.Environment.GetFolderPath(
            System.Environment.SpecialFolder.LocalApplicationData), "MMOI", "StormChase");
        string logPath = Path.Combine(dataRoot, "Logs", "launch-" +
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
            "-" + System.Environment.ProcessId + ".log");
        TextWriter previousOutput = Console.Out, previousError = Console.Error;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            using var log = new StreamWriter(new FileStream(logPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(log);
            Console.SetError(log);
            try
            {
                Console.WriteLine("Direct executable launch: LifeCare: Online login.");
                Console.WriteLine("Application directory: " + AppContext.BaseDirectory);
                Console.WriteLine("Runtime: " + RuntimeInformation.FrameworkDescription);
                return await Showcase.RunAsync(["--game", "--output", Path.Combine(dataRoot, "Captures")]);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                ShowLaunchError(exception.Message, logPath);
                return 1;
            }
            finally
            {
                Console.SetOut(previousOutput);
                Console.SetError(previousError);
            }
        }
        catch (Exception exception)
        {
            ShowLaunchError(exception.Message, logPath);
            return 1;
        }
    }

    private static void ShowLaunchError(string message, string logPath)
    {
        string text = "LifeCare: Online could not start.\n\n" + message + "\n\nLaunch log:\n" + logPath;
        if (OperatingSystem.IsWindows()) MessageBoxW(nint.Zero, text, "LifeCare: Online", 0x10);
        else Console.Error.WriteLine(text);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint window, string text, string caption, uint type);
}
