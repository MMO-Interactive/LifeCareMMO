using System.Globalization;

namespace StormChase.Server;

public sealed record ServerOptions
{
    public int HttpPort { get; init; } = 5180;
    public int GamePort { get; init; } = 7777;
    public string ListenAddress { get; init; } = "127.0.0.1";
    public string AdvertiseHost { get; init; } = "127.0.0.1";
    public string DataRoot { get; init; } = Path.Combine(AppContext.BaseDirectory, "server-data");
    public string ServerName { get; init; } = "MMOI Storm Chase";
    public int MaxPlayers { get; init; } = 16;
    public int CampaignSeed { get; init; } = 73019;
    public float CampaignStartHour { get; init; } = 8;
    public string? CertificatePath { get; init; }
    public string? CertificatePasswordEnvironment { get; init; }
    public bool AllowInsecureLan { get; init; }
    public bool SelfTest { get; init; }
    public bool NativeSelfTest { get; init; }
    public bool NativeFixture {get;init;}
    public bool AccountsOnly {get;init;}
    public bool MasterSelfTest {get;init;}
    public string? MasterUrl {get;init;}
    public string? MasterKeyEnvironment {get;init;}
    public string? ApiAddress {get;init;}
    public string? ControlKey { get; init; }
    public string? TestOutput { get; init; }

    public static ServerOptions Parse(string[] args)
    {
        string Read(string name, string fallback) { int i=Array.IndexOf(args,name); return i>=0 && i+1<args.Length ? args[i+1] : fallback; }
        var options=new ServerOptions
        {
            HttpPort=int.Parse(Read("--http-port",args.Contains("--accounts-only")?"5080":"5180"),CultureInfo.InvariantCulture),
            GamePort=int.Parse(Read("--game-port","7777"),CultureInfo.InvariantCulture),
            ListenAddress=Read("--listen","127.0.0.1"), AdvertiseHost=Read("--advertise-host","127.0.0.1"),
            DataRoot=Path.GetFullPath(Read("--data-root",Path.Combine(AppContext.BaseDirectory,"server-data"))),
            ServerName=Read("--server-name","MMOI Storm Chase"),
            MaxPlayers=int.Parse(Read("--max-players","16"),CultureInfo.InvariantCulture),
            CampaignSeed=int.Parse(Read("--world-seed",Read("--campaign-seed","73019")),CultureInfo.InvariantCulture),
            CampaignStartHour=float.Parse(Read("--campaign-start-hour","8"),CultureInfo.InvariantCulture),
            CertificatePath=Read("--certificate","") is { Length: >0 } cert ? Path.GetFullPath(cert) : null,
            CertificatePasswordEnvironment=Read("--certificate-password-env","") is { Length: >0 } env ? env : null,
            AllowInsecureLan=args.Contains("--allow-insecure-lan"), SelfTest=args.Contains("--self-test"),NativeSelfTest=args.Contains("--native-self-test"),
            NativeFixture=args.Contains("--native-fixture"),
            AccountsOnly=args.Contains("--accounts-only"),MasterSelfTest=args.Contains("--master-self-test"),
            MasterUrl=Read("--master-url","") is{Length:>0} master?master.TrimEnd('/'):null,
            MasterKeyEnvironment=Read("--master-key-env","") is{Length:>0} masterKeyEnv?masterKeyEnv:null,
            ApiAddress=Read("--api-address","") is{Length:>0} api?api.TrimEnd('/'):null,
            ControlKey=Read("--control-key","") is { Length: >0 } key ? key : null,
            TestOutput=Read("--test-output","") is { Length: >0 } output ? Path.GetFullPath(output) : null
        };
        if(options.HttpPort is <0 or >65535 || options.GamePort is <0 or >65535 || options.MaxPlayers is <1 or >64)
            throw new ArgumentException("Ports must be 0..65535; max players must be 1..64.");
        if(options.ServerName.Length is <1 or >64 || !float.IsFinite(options.CampaignStartHour) || options.CampaignStartHour is <0 or >=24)
            throw new ArgumentException("Server name must be 1..64 characters; start hour must be 0..24 exclusive.");
        if(options.ListenAddress!="127.0.0.1" && options.ListenAddress!="localhost" && options.ListenAddress!="::1" &&
           options.CertificatePath is null && !options.AllowInsecureLan)
            throw new ArgumentException("External HTTP hosting requires --certificate. --allow-insecure-lan is an explicit trusted LAN development option.");
        if(options.CertificatePath is not null && !File.Exists(options.CertificatePath)) throw new FileNotFoundException("TLS certificate not found.",options.CertificatePath);
        if(options.ControlKey is not null && (options.ControlKey.Length!=64||options.ControlKey.Any(c=>!char.IsAsciiHexDigit(c)))) throw new ArgumentException("Control key must be exactly 32 bytes in hexadecimal.");
        if(options.AccountsOnly&&options.MasterUrl is not null)throw new ArgumentException("An accounts service does not delegate to another master.");
        if(options.MasterUrl is not null)
        {
            if(!Uri.TryCreate(options.MasterUrl,UriKind.Absolute,out var masterUri)||masterUri.Scheme is not ("http" or "https"))throw new ArgumentException("Master URL must be an absolute HTTP/HTTPS URL.");
            if(masterUri.Scheme!="https"&&!(masterUri.Host=="localhost"||System.Net.IPAddress.TryParse(masterUri.Host,out var address)&&System.Net.IPAddress.IsLoopback(address)))throw new ArgumentException("An external accounts service requires HTTPS.");
            if(options.MasterKeyEnvironment is null||string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(options.MasterKeyEnvironment)))throw new ArgumentException("Supply the private server registration key through --master-key-env.");
        }
        return options;
    }
}
