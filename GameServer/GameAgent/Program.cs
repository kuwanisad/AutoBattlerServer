using GameAgent.Services;
using MainServer.Common.MasterConnection;
using MainServer.Common.Messaging;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Shared.Common.Redis;

var builder = WebApplication.CreateBuilder(args);

var sharedConfigPath = Path.Combine(AppContext.BaseDirectory, "shared.appsettings.json");
builder.Configuration.AddJsonFile(sharedConfigPath, optional: false, reloadOnChange: true);

builder.WebHost.ConfigureKestrel(options =>
{
    // 게임 서버(Unity)가 gRPC로 접속하는 포트
    options.ListenAnyIP(7000, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
    // REST 테스트용 포트
    options.ListenAnyIP(7001, listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
});

builder.Services.AddGrpc();

// MasterConnectionService - 게임 서버 준비 완료 후 연결 시작
// Capacity = 0으로 시작해서 게임 서버가 준비되면 실제값으로 업데이트
builder.Services.Configure<MasterConnectionOptions>(
    builder.Configuration.GetSection(MasterConnectionOptions.SectionName));
builder.Services.AddSingleton<MasterConnectionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MasterConnectionService>());

builder.Services.AddMainServerMessaging(builder.Configuration);

// Redis - Session Registry
builder.Services.AddSessionRegistry(builder.Configuration);

// GameServerHandler - 게임 서버 연결 처리
builder.Services.AddSingleton<GameServerHandler>();
builder.Services.AddScoped<IngameControlService>();

var app = builder.Build();

app.MapGrpcService<IngameControlService>();
app.MapGet("/", () => "Game Agent. 게임 서버(Unity)의 gRPC 접속을 기다리는 중입니다.");

app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogInformation("게임 에이전트 시작 완료. 게임 서버(Unity) 연결 대기 중... (포트 7000)");
});

app.Run();