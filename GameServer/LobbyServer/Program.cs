using Confluent.Kafka;
using LobbyServer.Auth;
using LobbyServer.Debug;
using LobbyServer.Services;
using MainServer.Common.Heartbeat;
using MainServer.Common.MasterConnection;
using MainServer.Common.MatchConnection;
using MainServer.Common.Messaging;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Shared.Common.Redis;

var builder = WebApplication.CreateBuilder(args);

var sharedConfigPath = Path.Combine(AppContext.BaseDirectory, "shared.appsettings.json");
builder.Configuration.AddJsonFile(sharedConfigPath, optional: false, reloadOnChange: true);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5200, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
    options.ListenAnyIP(5201, listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
});

builder.Services.Configure<MasterConnectionOptions>(
    builder.Configuration.GetSection(MasterConnectionOptions.SectionName));
builder.Services.AddSingleton<MasterConnectionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MasterConnectionService>());
builder.Services.AddMainServerMessaging(builder.Configuration);

// 매칭 서버 접속 - 로비는 즉시 접속을 시도한다(MatchServerConnection이 자체적으로 준비된 것으로 봄).
// 매칭 서버가 push하는 매칭 확정/취소 알림은 MatchNotificationDispatcher가 받아서 처리한다.
builder.Services.AddSingleton<MatchNotificationDispatcher>();
builder.Services.AddSingleton<IMatchNotificationHandler>(sp => sp.GetRequiredService<MatchNotificationDispatcher>());
builder.Services.AddSingleton<MatchServerConnection>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MatchServerConnection>());

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
app.Lifetime.ApplicationStarted.Register(() =>
{
    masterConnection.UpdateStatus(currentLoad: 0, capacity: 100); // TODO: 실제 수용량으로 교체
});

app.MapGrpcService<LobbyControlService>();
app.MapGet("/", () => "Lobby Server.");

// curl -X POST "http://localhost:5201/test/kafka?topic=game-events&message=hello"
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

// curl -X POST "http://localhost:5201/test/rabbitmq/queue?queue=reward-queue&message=hello"
app.MapPost("/test/rabbitmq/queue", (
    RabbitMqProducer producer,
    string queue,
    string message) =>
{
    producer.PublishToQueue(queue, message);
    return Results.Ok($"RabbitMQ 발행 완료: queue={queue} message={message}");
});

// curl -X POST "http://localhost:5201/test/rabbitmq/fanout?exchange=ops-announcements&message=hello"
app.MapPost("/test/rabbitmq/fanout", (
    RabbitMqProducer producer,
    string exchange,
    string message) =>
{
    producer.PublishToFanout(exchange, message);
    return Results.Ok($"RabbitMQ fanout 발행 완료: exchange={exchange} message={message}");
});

app.Run();