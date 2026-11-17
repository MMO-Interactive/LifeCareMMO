using System.Text.Json;
using System.Text.Json.Serialization;

namespace StormChase.Networking;

/// <summary>Install the authority only from a verified HTTPS endpoint; this pins the native handshake to that authority and identity.</summary>
public sealed record TransportSecurity(string AuthorityCertificateBase64, string? LocalCertificateBase64 = null, string? ExpectedPeerIdentity = null)
{
    public override string ToString() => $"TransportSecurity {{ ExpectedPeerIdentity = {ExpectedPeerIdentity}, LocalCertificate = {(LocalCertificateBase64 is null ? "none" : "private")} }}";
}

/// <summary>Persistent dedicated-server native certificates. ServerCertificateBase64 contains a PRIVATE KEY and must never be returned by an HTTP API.</summary>
public sealed record TransportIdentity(string Identity, string AuthorityCertificateBase64, string ServerCertificateBase64)
{
    public override string ToString() => $"TransportIdentity {{ Identity = {Identity}, ServerCertificate = private }}";
    [JsonIgnore] public TransportSecurity ServerSecurity => new(AuthorityCertificateBase64, ServerCertificateBase64);
    [JsonIgnore] public TransportSecurity ClientSecurity => new(AuthorityCertificateBase64, ExpectedPeerIdentity: Identity);

    public static unsafe TransportIdentity Create()
    {
        var identity = "sc/" + Guid.NewGuid().ToString("N")[..24];
        var authority = new byte[4096]; var certificate = new byte[4096]; int authorityLength, certificateLength; int result;
        fixed (byte* a = authority) fixed (byte* c = certificate) lock (NativeMethods.Gate)
            result = NativeMethods.sc_generate_identity(identity, a, authority.Length, out authorityLength, c, certificate.Length, out certificateLength);
        if (result != 1) throw new InvalidOperationException("Generate a server certificate before starting any transport hosts in this process.");
        return new(identity, Convert.ToBase64String(authority, 0, authorityLength), Convert.ToBase64String(certificate, 0, certificateLength));
    }

    public static TransportIdentity LoadOrCreate(string path)
    {
        if (File.Exists(path)) return JsonSerializer.Deserialize<TransportIdentity>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid server identity file.");
        var identity = Create();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            JsonSerializer.Serialize(file, identity);
        }
        return identity;
    }
}
