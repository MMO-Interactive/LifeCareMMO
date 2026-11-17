using System.Diagnostics;
using System.Globalization;
using System.Net;
using StormChase.Networking;

// LifeCare climate process: owns the campaign clock and the weather model, and pushes one frame per 50 ms tick to
// every subscribed district host. It can run on its own machine; hosts never ask it anything during a tick.
string Read(string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
try
{
    int port = int.Parse(Read("--port", "5300"), CultureInfo.InvariantCulture);
    var listen = IPAddress.Parse(Read("--listen", "127.0.0.1"));
    int seed = int.Parse(Read("--world-seed", Read("--campaign-seed", "73019")), CultureInfo.InvariantCulture);
    string startHour = Read("--campaign-start-hour", "8");
    string dataRoot = Path.GetFullPath(Read("--data-root", Path.Combine(AppContext.BaseDirectory, "climate-data")));
    string? keyEnvironment = Read("--link-key-env", "") is { Length: > 0 } k ? k : null;
    Directory.CreateDirectory(dataRoot);
    string clockPath = Path.Combine(dataRoot, $"campaign-clock-{seed}.txt");
    double saved = File.Exists(clockPath) && double.TryParse(File.ReadAllText(clockPath), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0;

    string[] campaignArgs = ["--demo", "--campaign-seed", seed.ToString(CultureInfo.InvariantCulture), "--campaign-start-hour", startHour];
    var weather = new StormChaseCampaignWeather(new LiveStormController(campaignArgs), campaignArgs, saved);
    using var link = new ClimateLinkServer(listen, port, keyEnvironment is null ? null : Environment.GetEnvironmentVariable(keyEnvironment));
    Console.WriteLine($"LifeCare climate ready: seed {weather.Seed}, day {weather.DayNumber} {weather.Hour:00.00}h, pushing on {listen}:{link.Port}");

    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();
    const double Tick = 0.05;
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Tick));
    var saveClock = Stopwatch.StartNew(); var report = Stopwatch.StartNew(); double worst = 0;
    try
    {
        while (await timer.WaitForNextTickAsync(stop.Token))
        {
            long started = Stopwatch.GetTimestamp();
            weather.Update(Tick);
            link.Publish(new(weather.Seed, weather.ElapsedSeconds, weather.Hour, weather.DayNumber, weather.ForecastName, weather.StartHour, weather.Current, weather.Atmosphere));
            worst = Math.Max(worst, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (saveClock.ElapsedMilliseconds >= 1000) { File.WriteAllText(clockPath, weather.ElapsedSeconds.ToString("R", CultureInfo.InvariantCulture)); saveClock.Restart(); }
            if (report.Elapsed.TotalSeconds >= 60)
            { Console.WriteLine($"climate day {weather.DayNumber} {weather.Hour:00.00}h, {weather.ActiveStormCount} storms, {link.SubscriberCount} hosts, worst tick {worst:0.0} ms"); worst = 0; report.Restart(); }
        }
    }
    catch (OperationCanceledException) { }
    File.WriteAllText(clockPath, weather.ElapsedSeconds.ToString("R", CultureInfo.InvariantCulture));
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("LifeCare climate stopped: " + error.Message);
    return 1;
}
