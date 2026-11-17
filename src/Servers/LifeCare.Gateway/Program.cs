using System.Security.Cryptography.X509Certificates;
using LifeCare.Gateway;

try
{
    var options = GatewayOptions.Parse(args);
    Directory.CreateDirectory(options.DataRoot);
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
    builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning).AddFilter("System.Net.Http", LogLevel.Warning);
    builder.WebHost.ConfigureKestrel(server =>
    {
        server.Limits.MaxRequestBodySize = 8192;
        server.Listen(options.ListenIp, options.HttpPort, listen =>
        {
            if (options.CertificatePath is null) return;
            var password = options.CertificatePasswordEnvironment is null ? null : Environment.GetEnvironmentVariable(options.CertificatePasswordEnvironment);
            listen.UseHttps(X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath, password));
        });
    });
    builder.Services.AddSingleton(options);
    var noRedirects = () => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
    builder.Services.AddHttpClient("accounts", c => { c.BaseAddress = new(options.AccountsUrl); c.Timeout = TimeSpan.FromSeconds(15); }).ConfigurePrimaryHttpMessageHandler(noRedirects);
    builder.Services.AddHttpClient("hosts", c => c.Timeout = TimeSpan.FromSeconds(15)).ConfigurePrimaryHttpMessageHandler(noRedirects);
    builder.Services.AddSingleton<GatewayDirectory>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<GatewayDirectory>());
    builder.Services.AddSingleton<GatewayRelay>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<GatewayRelay>());
    var app = builder.Build();
    GatewayHttp.Map(app);
    app.Lifetime.ApplicationStarted.Register(() => Console.WriteLine($"LifeCare Gateway ready: {options.PublicBase}; game port {options.GamePort}; accounts {options.AccountsUrl}"));
    await app.RunAsync();
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("LifeCare Gateway stopped: " + error.Message);
    return 1;
}
