using System.Globalization;
using System.Net;

namespace LifeCare.Gateway;

/// <summary>The Gateway is the only process clients reach: one HTTP port for accounts and server APIs, one encrypted UDP game port.</summary>
public sealed record GatewayOptions
{
    public int HttpPort { get; init; } = 5000;
    public int GamePort { get; init; } = 7000;
    public string ListenAddress { get; init; } = "127.0.0.1";
    public string AdvertiseHost { get; init; } = "127.0.0.1";
    public string? PublicUrl { get; init; }
    public string AccountsUrl { get; init; } = "http://127.0.0.1:5080";
    public string DataRoot { get; init; } = Path.Combine(AppContext.BaseDirectory, "gateway-data");
    public string? CertificatePath { get; init; }
    public string? CertificatePasswordEnvironment { get; init; }
    public string? LinkKeyEnvironment { get; init; }
    public int MaxPlayers { get; init; } = 128;
    public bool AllowInsecureLan { get; init; }

    public string PublicBase => (PublicUrl ?? (CertificatePath is null ? "http" : "https") + "://" + AdvertiseHost + ":" + HttpPort).TrimEnd('/');
    public string? LinkKey => LinkKeyEnvironment is null ? null : Environment.GetEnvironmentVariable(LinkKeyEnvironment);

    public static GatewayOptions Parse(string[] args)
    {
        string Read(string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        var options = new GatewayOptions
        {
            HttpPort = int.Parse(Read("--http-port", "5000"), CultureInfo.InvariantCulture),
            GamePort = int.Parse(Read("--game-port", "7000"), CultureInfo.InvariantCulture),
            ListenAddress = Read("--listen", "127.0.0.1"),
            AdvertiseHost = Read("--advertise-host", "127.0.0.1"),
            PublicUrl = Read("--public-url", "") is { Length: > 0 } url ? url : null,
            AccountsUrl = Read("--accounts", "http://127.0.0.1:5080").TrimEnd('/'),
            DataRoot = Path.GetFullPath(Read("--data-root", Path.Combine(AppContext.BaseDirectory, "gateway-data"))),
            CertificatePath = Read("--certificate", "") is { Length: > 0 } cert ? Path.GetFullPath(cert) : null,
            CertificatePasswordEnvironment = Read("--certificate-password-env", "") is { Length: > 0 } env ? env : null,
            LinkKeyEnvironment = Read("--link-key-env", "") is { Length: > 0 } key ? key : null,
            MaxPlayers = int.Parse(Read("--max-players", "128"), CultureInfo.InvariantCulture),
            AllowInsecureLan = args.Contains("--allow-insecure-lan"),
        };
        if (options.HttpPort is < 0 or > 65535 || options.GamePort is < 1 or > 65535 || options.MaxPlayers is < 1 or > 256)
            throw new ArgumentException("Ports must be 1..65535 and max players 1..256.");
        bool loopback = options.ListenAddress is "127.0.0.1" or "::1" or "localhost";
        if (!loopback && options.CertificatePath is null && !options.AllowInsecureLan)
            throw new ArgumentException("A public Gateway requires --certificate. --allow-insecure-lan is an explicit trusted LAN development option.");
        if (options.CertificatePath is not null && !File.Exists(options.CertificatePath)) throw new FileNotFoundException("TLS certificate not found.", options.CertificatePath);
        if (!Uri.TryCreate(options.AccountsUrl, UriKind.Absolute, out _)) throw new ArgumentException("--accounts must be an absolute URL.");
        return options;
    }

    public IPAddress ListenIp => ListenAddress == "localhost" ? IPAddress.Loopback : IPAddress.Parse(ListenAddress);
}
