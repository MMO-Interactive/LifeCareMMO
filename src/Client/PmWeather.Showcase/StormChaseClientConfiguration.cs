using System.Text.Json;

/// <summary>Operator configuration; connection details are absent from player login.</summary>
internal static class ChaseClientConfiguration
{
    public const string DefaultAccountAddress = "http://localhost:5080";

    public static string AccountAddress
    {
        get
        {
            if (Environment.GetEnvironmentVariable("MMOI_STORM_CHASE_ACCOUNTS_API") is { Length: > 0 } configured)
                return Normalize(configured);
            string path = Path.Combine(AppContext.BaseDirectory, "client-config.json");
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("accountsApi", out var address) && address.GetString() is { Length: > 0 } value)
                    return Normalize(value);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            return DefaultAccountAddress;
        }
    }

    public static string Normalize(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath != "/" || uri.Scheme != "https" && !uri.IsLoopback)
            throw new InvalidDataException("The configured accounts service requires a root HTTPS URL, or loopback HTTP for local development.");
        return uri.GetLeftPart(UriPartial.Authority);
    }
}
