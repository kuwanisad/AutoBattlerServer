using MasterServer;
using MasterServer.Registry;
using MasterServer.Routing;
using MasterServer.Services;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton<IRoutingStrategy, LeastLoadRoutingStrategy>();

// RegistryCleanupServer 상시 연결
builder.Services.AddSingleton<CleanupServerConnection>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CleanupServerConnection>());

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
