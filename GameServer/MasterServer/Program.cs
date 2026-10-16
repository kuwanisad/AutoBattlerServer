using Harness.Agent;
using Shared.Common.Hosting;
using GameServer.Common;
using MasterServer;
using MasterServer.Debug;
using MasterServer.Registry;
using MasterServer.Security;
using MasterServer.Services;
using Shared.Common.Endpoints;
using Shared.Common.Redis;

var builder = WebApplication.CreateBuilder(args);

var sharedConfigPath = Path.Combine(AppContext.BaseDirectory, "shared.appsettings.json");
builder.Configuration.AddJsonFile(sharedConfigPath, optional: false, reloadOnChange: true);

builder.ConfigureGameServerEndpoints(grpcPort: ServerPorts.MasterGrpc, httpPort: ServerPorts.MasterHttp);

// KeepAlivePing 설정은 공용 메서드가 다루는 범위 밖이라(마스터 서버 전용 튜닝) 별도로
// 이어붙인다 - ConfigureKestrel은 여러 번 호출해도 누적 적용된다.
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(10);
    options.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(5);
});

builder.Services.AddGrpc();
builder.Services.AddSingleton<InstanceRegistry>();
builder.Services.AddSingleton<ActiveAnnouncements>(); // 켜진 공지 기억 - 재접속한 인스턴스에 다시 보내기 위함
builder.Services.Configure<RedisOptions>(builder.Configuration.GetSection(RedisOptions.SectionName));

// TODO: mTLS 도입 시 CompositeServerAuthenticator(+ 개별 검증기)로 교체
builder.Services.AddSingleton<IServerAuthenticator, DummyServerAuthenticator>();

// 테스트 하네스 에이전트(테스트 전용) - HARNESS_ADDRESS 환경 변수가 없으면 아무것도 안 한다.
builder.Services.AddHarnessAgent();

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
    // 종료 지시는 1회성 명령이라 재접속 때 다시 보내지 않는다 - 대신 실제 전달 결과를
    // 그대로 응답해서, 실패한 대상은 운영자가 다시 보낼 수 있게 한다.
    if (!string.IsNullOrEmpty(instanceId))
    {
        return registry.Enqueue(instanceId, message)
            ? Results.Ok($"인스턴스 {instanceId}에 종료 지시 전달")
            : Results.NotFound($"인스턴스 {instanceId}에 종료 지시 전달 실패 - 접속 중이 아님");
    }

    var report = serverType.HasValue
        ? registry.EnqueueTo(registry.GetByType(serverType.Value), message)
        : registry.Broadcast(message);
    var targetName = serverType.HasValue ? $"{serverType} 타입" : "접속 중인 전체";
    return Results.Ok($"{targetName} 종료 지시: {report}");
});

app.MapPost("/admin/announce", (
    InstanceRegistry registry,
    ActiveAnnouncements activeAnnouncements,
    string? instanceId,
    ServerType? serverType,
    AnnouncementType announcementType,
    bool active) =>
{
    var masterMessage = new MasterMessage
    {
        Instruction = new MasterServerInstruction
        {
            Announcement = new AnnouncementTask { AnnouncementType = announcementType, Active = active },
        },
    };

    var scope = !string.IsNullOrEmpty(instanceId) ? AnnouncementScope.ForInstance(instanceId)
        : serverType.HasValue ? AnnouncementScope.ForServerType(serverType.Value)
        : AnnouncementScope.All;

    // 켜짐/꺼짐 상태를 먼저 기억해 둔다 - 지금 끊겨 있어서 못 받은 인스턴스도 재접속하면
    // ConnectionHandler가 이 기억을 보고 켜진 공지를 다시 보낸다(그래서 단일 대상이 지금
    // 접속 중이 아니어도 지시 자체는 유효하다).
    activeAnnouncements.Apply(announcementType, active, scope);

    var summary = $"(Type={announcementType}, Active={active}, 대상={scope})";

    if (!string.IsNullOrEmpty(instanceId))
    {
        return registry.Enqueue(instanceId, masterMessage)
            ? Results.Ok($"인스턴스 {instanceId}에 공지 지시 전달 {summary}")
            : Results.Ok($"인스턴스 {instanceId}가 접속 중이 아니라 지금은 전달 못 함 - " +
                         $"{(active ? "재접속하면 전달됨" : "기억에서 지움")} {summary}");
    }

    var report = serverType.HasValue
        ? registry.EnqueueTo(registry.GetByType(serverType.Value), masterMessage)
        : registry.Broadcast(masterMessage);
    return Results.Ok($"공지 지시: {report} {summary}");
});

app.Run();
