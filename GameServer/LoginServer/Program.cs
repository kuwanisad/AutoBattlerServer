using LoginServer.Services;
using MainServer.Common.Heartbeat;
using MainServer.Common.MasterConnection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Shared.Common.Redis;

var builder = WebApplication.CreateBuilder(args);

var sharedConfigPath = Path.Combine(AppContext.BaseDirectory, "shared.appsettings.json");
builder.Configuration.AddJsonFile(sharedConfigPath, optional: false, reloadOnChange: true);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5400, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
    options.ListenAnyIP(5401, listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
});

builder.Services.Configure<MasterConnectionOptions>(
    builder.Configuration.GetSection(MasterConnectionOptions.SectionName));
builder.Services.AddSingleton<MasterConnectionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MasterConnectionService>());

// Redis - 로그인 서버가 세션 레지스트리에 직접 쓰기 접근하는 첫 사례다(관리 권한 획득/이관).
builder.Services.AddSessionRegistry();
builder.Services.AddHostedService<ServerHeartbeatService>();

// 인증 없음 - 로그인 서버는 클라가 아직 아무 신원도 없는 상태에서 접속하는 최초
// 진입점이라 AuthInterceptor 자체가 없다.
builder.Services.AddGrpc();
builder.Services.AddScoped<LoginControlService>();

var app = builder.Build();

var masterConnection = app.Services.GetRequiredService<MasterConnectionService>();
app.Lifetime.ApplicationStarted.Register(() =>
{
    masterConnection.UpdateStatus(currentLoad: 0, capacity: 1000); // 접속은 짧게 스쳐가므로 수용량 개념이 느슨함
});

app.MapGrpcService<LoginControlService>();
app.MapGet("/", () => "Login Server.");

app.Run();
