using GameAgent.Services;
using MainServer.Common.Heartbeat;
using Shared.Common.Hosting;
using MainServer.Common.MasterConnection;
using MainServer.Common.MatchConnection;
using MainServer.Common.Messaging;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Shared.Common.Auth;
using Shared.Common.Auth.Debug;
using Shared.Common.Redis;

var builder = WebApplication.CreateBuilder(args);

var sharedConfigPath = Path.Combine(AppContext.BaseDirectory, "shared.appsettings.json");
builder.Configuration.AddJsonFile(sharedConfigPath, optional: false, reloadOnChange: true);

// 게임 서버(Unity)가 gRPC로 접속하는 포트(7000) + REST 테스트용 포트(7001)
builder.ConfigureGameServerEndpoints(grpcPort: 7000, httpPort: 7001);

builder.Services.AddGrpc();

// MasterConnectionService - 게임 서버 준비 완료 후 연결 시작
// Capacity = 0으로 시작해서 게임 서버가 준비되면 실제값으로 업데이트
builder.Services.Configure<MasterConnectionOptions>(
    builder.Configuration.GetSection(MasterConnectionOptions.SectionName));
builder.Services.AddSingleton<MasterConnectionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MasterConnectionService>());

builder.Services.AddMainServerMessaging(builder.Configuration);

// 매칭 서버 접속 - 게임 서버(Unity)가 Ready를 보낼 때까지 대기했다가 시작한다
// (GameServerHandler.HandleReadyAsync가 SignalReady() 호출). IMatchNotificationHandler는
// 등록 안 함 - 게임 에이전트는 매칭 알림을 받을 일이 없다(로비 전용).
builder.Services.AddSingleton<MatchServerConnection>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MatchServerConnection>());

// Redis - Session Registry
builder.Services.AddSessionRegistry();
builder.Services.AddHostedService<ServerHeartbeatService>();

// 토큰 검증 - 로비의 EnterLobby와 같은 판단 기준(TokenVerification)을 ConnectionApproval에서
// 재사용한다. 지금은 검증(대조)만 하고 발급(회전)은 안 하지만, ITokenProvider도 미리
// 주입 가능하게 해뒀다 - 나중에 "권한 이전 시 새 토큰도 같이 발급"하게 될 수 있어서.
builder.Services.AddSingleton<ITokenProvider, DummyTokenProvider>();

// GameServerHandler - 게임 서버 연결 처리
builder.Services.AddSingleton<GameServerHandler>();
builder.Services.AddScoped<IngameControlService>();
builder.Services.AddScoped<IngameMatchService>();

var app = builder.Build();

app.MapGrpcService<IngameControlService>();
app.MapGrpcService<IngameMatchService>();
app.MapGet("/", () => "Game Agent. 게임 서버(Unity)의 gRPC 접속을 기다리는 중입니다.");

app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogInformation("게임 에이전트 시작 완료. 게임 서버(Unity) 연결 대기 중... (포트 7000)");
});

app.Run();