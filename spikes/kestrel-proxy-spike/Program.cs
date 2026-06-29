using KestrelSpike;
using Microsoft.AspNetCore.Connections;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

var port = builder.Configuration.GetValue("port", 8080);

// Watched hosts: only these are MITM'd; everything else is blind-tunnelled.
// Defaults chosen for the spike test script.
var watchedCsv = builder.Configuration.GetValue("watch", "jsonplaceholder.typicode.com,localhost");
var watched = new WatchedHosts(watchedCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

var caPath = Path.Combine(AppContext.BaseDirectory, "spike-root-ca.pfx");

builder.Services.AddSingleton(_ => new CertificateAuthority(caPath));
builder.Services.AddSingleton(watched);
builder.Services.AddSingleton<ProxyConnectionHandler>();
builder.Services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
{
    UseProxy = false,
    AllowAutoRedirect = false,
    AutomaticDecompression = System.Net.DecompressionMethods.All,
}));

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(port, l => l.UseConnectionHandler<ProxyConnectionHandler>());
});

var app = builder.Build();
var ca = app.Services.GetRequiredService<CertificateAuthority>();

// Export the root CA as DER (.cer) so it can be trusted on the OS for browser tests.
var cerPath = Path.ChangeExtension(caPath, ".cer");
File.WriteAllBytes(cerPath, ca.RootCertificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert));

Console.WriteLine($"[spike] listening on http://127.0.0.1:{port}");
Console.WriteLine($"[spike] watched (MITM) hosts: {watchedCsv}");
Console.WriteLine($"[spike] root CA (DER) for trust: {cerPath}");

app.Run();
