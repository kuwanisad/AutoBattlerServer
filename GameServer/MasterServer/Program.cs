using MasterServer;
using MasterServer.Debug;
using MasterServer.Registry;
using MasterServer.Security;
using MasterServer.Services;
using Shared.Common.Redis;

var builder = WebApplication.CreateBuilder(args);

var sharedConfigPath = Path.Combine(AppContext.BaseDirectory, "shared.appsettings.json");
builder.Configuration.AddJsonFile(sharedConfigPath, optional: false, reloadOnChange: true);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5100, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
    });

    options.ListenAnyIP(5101, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
    });

    options.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(10);
    options.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(5);
});

builder.Services.AddGrpc();
builder.Services.AddSingleton<InstanceRegistry>();
builder.Services.Configure<RedisOptions>(builder.Configuration.GetSection(RedisOptions.SectionName));

// TODO: mTLS 도입 시 CompositeServerAuthenticator(+ 개별 검증기)로 교체
builder.Services.AddSingleton<IServerAuthenticator, DummyServerAuthenticator>();

var app = builder.Build();

app.MapGrpcService<MasterControlService>();
app.MapGet("/", () => "Master Server gRPC 엔드포인트입니다. gRPC 클라이언트로 접속하세요.");

app.MapPost("/test/broadcast", (InstanceRegistry registry, string message) =>
{
    var masterMessage = new MasterMessage
    {
        Command = new MasterCommand
        {
            Broadcast = new BroadcastRelay
            {
                Message = message,
                ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds(),
            },
        },
    };

    registry.Broadcast(masterMessage);
    return Results.Ok($"{registry.GetAll().Count}개 접속 중인 인스턴스 큐에 브로드캐스트 적재");
});

app.Run();
