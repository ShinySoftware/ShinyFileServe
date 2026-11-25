using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shiny;
using ShinyFileServe.Core;
using ShinyFileServe.Headless;
using ShinyFileServe.Server;
using ShinyFileServe.Server.Security;

// Everything here is the environment's to say, never the database's: these are facts about where the
// process runs. The rest - accounts, shares, the tunnel token - is set in the portal as usual.
//
//   FILESERVE_DATA            the database, cookie key, packages and partial uploads   (/data in the image)
//   FILESERVE_PORT            the port to listen on                                     (8080)
//   FILESERVE_ALLOWED_HOSTS   DNS names people reach it by, comma separated - tower.local, files.example.com
//   FILESERVE_PLACES          folders the share picker starts from, colon separated     (/shares in the image)
//   FILESERVE_TRUST_LOOPBACK  sign in requests from 127.0.0.1 without a password        (off)
//   FILESERVE_CLAMD ...       upload scanning - see AddFileScanningFromEnvironment

Directory.CreateDirectory(HeadlessPaths.DataDirectory);

var port = Int32.TryParse(Environment.GetEnvironmentVariable("FILESERVE_PORT"), out var p) && p is > 0 and < 65536 ? p : 8080;
var hosts = (Environment.GetEnvironmentVariable("FILESERVE_ALLOWED_HOSTS") ?? String.Empty)
    .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(AllowedHostsHolder.Normalize)
    .OfType<string>()
    .ToList();

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

builder.Services.AddSingleton(new HeadlessOptions { Port = port });

// Nobody sits at a container's machine, so setup is proved by reading the log instead. Registered
// before the server, whose own registration only adds the do-nothing one if none is there.
builder.Services.AddSingleton(SetupCode.Generate());

builder.Services.AddFileServeCore(o => o.DatabasePath = HeadlessPaths.DatabaseFile);
builder.Services.AddFileScanningFromEnvironment();
builder.Services.AddFileServeServer(o =>
{
    o.Address = IPAddress.Any;
    o.Port = port;
    o.TicketKeyPath = HeadlessPaths.TicketKeyFile;
    o.DataDirectory = HeadlessPaths.WebAppDirectory;
    o.PackagesDirectory = HeadlessPaths.PackagesDirectory;
    o.UploadsDirectory = HeadlessPaths.UploadsDirectory;

    // Off unless asked for. The desktop app trusts its own machine because whoever sits at it owns
    // the files anyway; behind a container's port mapping "loopback" is whatever the runtime's proxy
    // happens to connect from, which is not a person at all.
    o.TrustLoopback = Registration.IsOn(Environment.GetEnvironmentVariable("FILESERVE_TRUST_LOOPBACK"));

    foreach (var host in hosts)
        o.AllowedHosts.Add(host);
});

builder.Services.AddSingleton<IAppStartupInstaller, NoAppStartupInstaller>();
builder.Services.AddHostedService<HeadlessServer>();

await builder.Build().RunAsync();
