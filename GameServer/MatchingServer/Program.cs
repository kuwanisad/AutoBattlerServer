using Harness.Agent;
using MainServer.Common.Heartbeat;
using MainServer.Common.MasterConnection;
using MatchingServer.Registry;
using MatchingServer.Services;
using Shared.Common.Endpoints;
using Shared.Common.Hosting;
using Shared.Common.Redis;

var builder = WebApplication.CreateBuilder(args);

var sharedConfigPath = Path.Combine(AppContext.BaseDirectory, "shared.appsettings.json");
builder.Configuration.AddJsonFile(sharedConfigPath, optional: false, reloadOnChange: true);

builder.ConfigureGameServerEndpoints(grpcPort: ServerPorts.MatchingGrpc);

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
// 매칭 타임아웃/루프 간격/GameType별 정원은 MatchingConstants에 있다(예전 설정 키 MatchingServer:MatchTimeoutSeconds는
// 실제로 설정한 곳이 없어서 제거).
builder.Services.AddSingleton<MatchCallbackClient>();
builder.Services.AddSingleton(sp =>
    new MatchEngine(
        sp.GetRequiredService<MatchCallbackClient>(),
        sp.GetRequiredService<ConnectedServerRegistry>(),
        sp.GetRequiredService<ISessionRegistry>(),
        sp.GetRequiredService<ILogger<MatchEngine>>()));

// gRPC
builder.Services.AddGrpc();

// 테스트 하네스 에이전트(테스트 전용) - HARNESS_ADDRESS 환경 변수가 없으면 아무것도 안 한다.
builder.Services.AddHarnessAgent();

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