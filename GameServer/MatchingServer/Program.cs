using MainServer.Common.Heartbeat;
using MainServer.Common.MasterConnection;
using MatchingServer.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Shared.Common.Redis;

var builder = WebApplication.CreateBuilder(args);

var sharedConfigPath = Path.Combine(AppContext.BaseDirectory, "shared.appsettings.json");
builder.Configuration.AddJsonFile(sharedConfigPath, optional: false, reloadOnChange: true);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(6000, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
});

// 마스터 서버 연결
builder.Services.Configure<MasterConnectionOptions>(
    builder.Configuration.GetSection(MasterConnectionOptions.SectionName));
builder.Services.AddSingleton<MasterConnectionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MasterConnectionService>());

// Redis
builder.Services.AddSessionRegistry();
builder.Services.AddHostedService<ServerHeartbeatService>();

// 매칭 서버 핵심 서비스
var matchTimeoutSeconds = builder.Configuration.GetValue<int>("MatchingServer:MatchTimeoutSeconds", 30);
builder.Services.AddSingleton<MatchCallbackClient>();
builder.Services.AddSingleton(sp =>
    new MatchEngine(
        sp.GetRequiredService<MatchCallbackClient>(),
        sp.GetRequiredService<ISessionRegistry>(),
        sp.GetRequiredService<ILogger<MatchEngine>>(),
        matchTimeoutSeconds));

// gRPC
builder.Services.AddGrpc();

var app = builder.Build();

app.MapGrpcService<LobbyMatchService>();
app.MapGrpcService<IngameMatchService>();
app.MapGet("/", () => "Matching Server.");

app.Lifetime.ApplicationStarted.Register(() =>
{
    var masterConnection = app.Services.GetRequiredService<MasterConnectionService>();
    masterConnection.UpdateStatus(currentLoad: 0, capacity: 0);
    app.Logger.LogInformation("매칭 서버 시작 완료. (포트 6000)");
});

app.Run();
