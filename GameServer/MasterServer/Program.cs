using Shared.Common.Hosting;
using GameServer.Common;
using MasterServer;
using MasterServer.Debug;
using MasterServer.Registry;
using MasterServer.Security;
using MasterServer.Services;
using Shared.Common.Redis;

var builder = WebApplication.CreateBuilder(args);

var sharedConfigPath = Path.Combine(AppContext.BaseDirectory, "shared.appsettings.json");
builder.Configuration.AddJsonFile(sharedConfigPath, optional: false, reloadOnChange: true);

builder.ConfigureGameServerEndpoints(grpcPort: 5100, httpPort: 5101);

// KeepAlivePing 설정은 공용 메서드가 다루는 범위 밖이라(마스터 서버 전용 튜닝) 별도로
// 이어붙인다 - ConfigureKestrel은 여러 번 호출해도 누적 적용된다.
builder.WebHost.ConfigureKestrel(options =>
{
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

app.MapPost("/admin/shutdown", (
    InstanceRegistry registry,
    string? instanceId,
    ServerType? serverType,
    long gracePeriodMs,
    string reason) =>
{
    var message = new MasterMessage
    {
        Instruction = new MasterServerInstruction
        {
            Shutdown = new ShutdownTask { GracePeriodMs = gracePeriodMs, Reason = reason },
        },
    };

    // "누구한테 보낼지"는 여기(발행하는 시점)에서만 판단한다 - 메시지 자체엔 target
    // 정보를 안 담는다. 받는 쪽은 자기가 어떻게 선택돼서 이 메시지를 받았는지 몰라도 됨.
    if (!string.IsNullOrEmpty(instanceId))
    {
        registry.Enqueue(instanceId, message);
        return Results.Ok($"인스턴스 {instanceId}에 종료 지시 전달");
    }

    if (serverType.HasValue)
    {
        var targets = registry.GetByType(serverType.Value).ToList();
        foreach (var instance in targets)
        {
            registry.Enqueue(instance.InstanceId, message);
        }
        return Results.Ok($"{serverType} 타입 {targets.Count}개 인스턴스에 종료 지시 전달");
    }

    registry.Broadcast(message);
    return Results.Ok($"접속 중인 전체 {registry.GetAll().Count}개 인스턴스에 종료 지시 전달");
});

app.Run();