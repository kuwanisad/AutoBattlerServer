using Confluent.Kafka;
using LobbyServer.Auth;
using LobbyServer.Services;
using MainServer.Common.Heartbeat;
using Shared.Common.Hosting;
using MainServer.Common.MasterConnection;
using MainServer.Common.MatchConnection;
using MainServer.Common.Messaging;
using Shared.Common.Messaging;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Shared.Common.Auth;
using Shared.Common.Auth.Debug;
using Shared.Common.Endpoints;
using Shared.Common.Redis;

var builder = WebApplication.CreateBuilder(args);

var sharedConfigPath = Path.Combine(AppContext.BaseDirectory, "shared.appsettings.json");
builder.Configuration.AddJsonFile(sharedConfigPath, optional: false, reloadOnChange: true);

builder.ConfigureGameServerEndpoints(grpcPort: ServerPorts.LobbyGrpc, httpPort: ServerPorts.LobbyHttp);

builder.Services.Configure<MasterConnectionOptions>(
    builder.Configuration.GetSection(MasterConnectionOptions.SectionName));
builder.Services.AddSingleton<MasterConnectionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MasterConnectionService>());
builder.Services.AddMainServerMessaging(builder.Configuration);

// 매칭 서버 접속 - 로비는 즉시 접속을 시도한다(MatchServerConnection이 자체적으로 준비된 것으로 봄).
// 매칭 서버가 push하는 매칭 확정/취소 알림은 MatchNotificationDispatcher가 받아서 처리한다.
builder.Services.AddSingleton<MatchNotificationDispatcher>();

// 우편함 - 연결 레지스트리 + AccountServerIntermediary 서브클래스 + 직접 대상 전달 클래스.
builder.Services.AddSingleton<LobbyConnectionRegistry>();
builder.Services.AddSingleton<LobbyAccountServerIntermediary>();
builder.Services.AddSingleton<LobbyMailDeliverer>();
builder.Services.AddSingleton<LobbyMainServerIntermediary>();
builder.Services.AddSingleton<IMatchNotificationHandler>(sp => sp.GetRequiredService<MatchNotificationDispatcher>());
builder.Services.AddSingleton<MatchServerConnection>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MatchServerConnection>());

// 공지 등 클라 전원 대상 작업 - 반드시 단일 인스턴스, 서버 시작 즉시 실행돼야 함
// (인스턴스가 여럿이면 각자 다른 Works/PendingWorks를 가져서 "하나의 순서로 직렬화"라는
// 목적이 깨지고, 지연 생성이면 시작 직후 온 지시가 유실될 수 있음 - GlobalWorker
// 클래스 주석 참고).
builder.Services.AddSingleton<GlobalWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GlobalWorker>());
builder.Services.AddSingleton<LobbyAnnouncementHandler>();

// Redis
builder.Services.AddSessionRegistry();
builder.Services.AddHostedService<ServerHeartbeatService>();

// 인증
builder.Services.AddSingleton<ITokenProvider, DummyTokenProvider>();

// gRPC - AuthInterceptor를 전역 인터셉터로 등록
builder.Services.AddGrpc(options =>
{
    options.Interceptors.Add<AuthInterceptor>();
});
builder.Services.AddScoped<LobbyControlService>();

var app = builder.Build();

// 로비 서버는 시작하자마자 개방 - 인게임 서버와 달리 별도 준비 단계가 없다.
var masterConnection = app.Services.GetRequiredService<MasterConnectionService>();

// LobbyAnnouncementHandler는 백그라운드 루프가 없는 순수 싱글톤이라, 아무도 안 쓰면
// 지연 생성되어 생성자의 OnAnnouncementRequested 구독 자체가 안 걸린다 - 여기서
// 강제로 인스턴스화해서 시작 즉시 구독이 걸리도록 보장한다.
app.Services.GetRequiredService<LobbyAnnouncementHandler>();

app.Lifetime.ApplicationStarted.Register(() =>
{
    masterConnection.UpdateStatus(currentLoad: 0, capacity: 100); // TODO: 실제 수용량으로 교체
});

app.MapGrpcService<LobbyControlService>();
app.MapGet("/", () => "Lobby Server.");

// curl -k -X POST "https://localhost:5201/test/kafka?topic=game-events&message=hello"
app.MapPost("/test/kafka", async (
    KafkaProducer<string, string> producer,
    string topic,
    string message) =>
{
    await producer.ProduceAsync(topic, new Message<string, string>
    {
        Key = Guid.NewGuid().ToString(),
        Value = message,
    });
    return Results.Ok($"Kafka 발행 완료: topic={topic} message={message}");
});

// curl -k -X POST "https://localhost:5201/test/rabbitmq/queue?queue=reward-queue&message=hello"
app.MapPost("/test/rabbitmq/queue", (
    RabbitMqBroker broker,
    string queue,
    string message) =>
{
    broker.PublishToQueue(queue, message);
    return Results.Ok($"RabbitMQ 발행 요청(버퍼에 넣음 - 연결돼 있으면 바로 발행): queue={queue} message={message}");
});

// curl -k -X POST "https://localhost:5201/test/rabbitmq/fanout?exchange=ops-announcements&message=hello"
app.MapPost("/test/rabbitmq/fanout", (
    RabbitMqBroker broker,
    string exchange,
    string message) =>
{
    broker.PublishToFanout(exchange, message);
    return Results.Ok($"RabbitMQ fanout 발행 요청(버퍼에 넣음 - 연결돼 있으면 바로 발행): exchange={exchange} message={message}");
});

app.Run();
