using MainServer.Common.Heartbeat;
using MainServer.Common.MasterConnection;
using MatchingServer.Registry;
using MatchingServer.Services;
using Shared.Common.Hosting;
using Shared.Common.Redis;

var builder = WebApplication.CreateBuilder(args);

var sharedConfigPath = Path.Combine(AppContext.BaseDirectory, "shared.appsettings.json");
builder.Configuration.AddJsonFile(sharedConfigPath, optional: false, reloadOnChange: true);

builder.ConfigureGameServerEndpoints(grpcPort: 6000);

// 마스터 서버 연결
builder.Services.Configure<MasterConnectionOptions>(
    builder.Configuration.GetSection(MasterConnectionOptions.SectionName));
builder.Services.AddSingleton<MasterConnectionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MasterConnectionService>());

// Redis
builder.Services.AddSessionRegistry();
builder.Services.AddHostedService<ServerHeartbeatService>();

// 로비/게임 에이전트 접속 유지 스트림 (MatchConnectionControl) - 예전 RegisterServer/
// UnregisterServer + Redis TTL 폴링을 대체한다. 접속 자체가 등록, 접속 종료가 즉시 해제.
builder.Services.AddSingleton<ConnectedServerRegistry>();

// 매칭 서버 핵심 서비스
var matchTimeoutSeconds = builder.Configuration.GetValue<int>("MatchingServer:MatchTimeoutSeconds", 30);
builder.Services.AddSingleton<MatchCallbackClient>();
builder.Services.AddSingleton(sp =>
    new MatchEngine(
        sp.GetRequiredService<MatchCallbackClient>(),
        sp.GetRequiredService<ConnectedServerRegistry>(),
        sp.GetRequiredService<ISessionRegistry>(),
        sp.GetRequiredService<ILogger<MatchEngine>>(),
        matchTimeoutSeconds));

// gRPC
builder.Services.AddGrpc();

var app = builder.Build();

app.MapGrpcService<MatchConnectionService>();
app.MapGrpcService<LobbyMatchService>();
app.MapGrpcService<InGameRelayService>();
app.MapGet("/", () => "Matching Server.");

app.Lifetime.ApplicationStarted.Register(() =>
{
    var masterConnection = app.Services.GetRequiredService<MasterConnectionService>();
    masterConnection.UpdateStatus(currentLoad: 0, capacity: 0);
    app.Logger.LogInformation("매칭 서버 시작 완료. (포트 6000)");
});

app.Run();
