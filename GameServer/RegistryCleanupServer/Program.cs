using Microsoft.AspNetCore.Server.Kestrel.Core;
using RegistryCleanupServer.Services;
using Shared.Common.Redis;

var builder = WebApplication.CreateBuilder(args);

var sharedConfigPath = Path.Combine(AppContext.BaseDirectory, "shared.appsettings.json");
builder.Configuration.AddJsonFile(sharedConfigPath, optional: false, reloadOnChange: true);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(9000, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
});

// Redis - 세션 정리에 사용
builder.Services.AddSessionRegistry(builder.Configuration);

// 핵심 서비스
builder.Services.AddSingleton<OwnershipRegistry>();

// gRPC
builder.Services.AddGrpc();

var app = builder.Build();

app.MapGrpcService<MainServerCleanupService>();
app.MapGrpcService<MasterLinkService>();
app.MapGet("/", () => "RegistryCleanupServer. 죽은 서버의 관리 대상 상태를 정리합니다.");

app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogInformation("RegistryCleanupServer 시작 완료. (포트 9000)");
});

app.Run();
