using DummySubServer;
using Shared.Common.Account;
using GameServer.Common;
using MainServer.Common.Heartbeat;
using MainServer.Common.MasterConnection;
using Shared.Common.Hosting;
using SubServer.Common.Messaging;
using Shared.Common.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
});

var sharedConfigPath = Path.Combine(AppContext.BaseDirectory, "shared.appsettings.json");
builder.Configuration.AddJsonFile(sharedConfigPath, optional: false, reloadOnChange: true);

// 하위 서버는 gRPC 서비스를 직접 서빙하지 않으므로(AccountServer/로비/게임에이전트를
// 부르기만 함) 포트 하나로 충분하다 - 헬스체크 REST용.
builder.ConfigureGameServerEndpoints(grpcPort: 5300);

// 마스터 서버 연결 - 우편함 시스템 도입 전까지는 하위 서버가 아예 마스터에 등록조차
// 안 했었다(Kafka/RabbitMQ만 구독). 이제 세션 레지스트리 조회 + 계정/로비/게임에이전트
// 호출이 필요해져서 정식으로 등록한다.
builder.Services.Configure<MasterConnectionOptions>(
    builder.Configuration.GetSection(MasterConnectionOptions.SectionName));
builder.Services.AddSingleton<MasterConnectionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MasterConnectionService>());

// Redis - 세션 레지스트리(플레이어가 지금 어디 접속 중인지 조회용)
builder.Services.AddSessionRegistry();
builder.Services.AddHostedService<ServerHeartbeatService>();

// 우편 지급 + 소유 서버 알림 - 여러 컨슈머가 공유
builder.Services.AddSingleton<SubServerAccountServerIntermediary>();
// RemoteMailNotifier는 추상 타입(AccountServerIntermediary)을 요구하는데, 구체 타입
// 등록만으로는 DI가 자동으로 안 이어준다 - 명시적으로 매핑해줘야 함.
builder.Services.AddSingleton<AccountServerIntermediary>(sp =>
    sp.GetRequiredService<SubServerAccountServerIntermediary>());
builder.Services.AddSingleton<RemoteMailNotifier>();

// Kafka 컨슈머 설정
builder.Services.Configure<KafkaConsumerOptions>(options =>
{
    builder.Configuration.GetSection(KafkaConsumerOptions.SectionName).Bind(options);
    options.GroupId = "dummy-sub-server";
    options.Topics = ["game-events", "match-results"];
});

// RabbitMQ 컨슈머 설정
builder.Services.Configure<RabbitMqConsumerOptions>(options =>
{
    builder.Configuration.GetSection(RabbitMqConsumerOptions.SectionName).Bind(options);
    options.QueueName = "reward-queue";
});

builder.Services.AddHostedService<DummyKafkaConsumer>();
builder.Services.AddHostedService<DummyRabbitMqConsumer>();

var app = builder.Build();

app.MapGet("/", () => "DummySubServer is running.");

app.Lifetime.ApplicationStarted.Register(() =>
{
    var masterConnection = app.Services.GetRequiredService<MasterConnectionService>();
    masterConnection.UpdateStatus(currentLoad: 0, capacity: 0);
});

app.Run();
