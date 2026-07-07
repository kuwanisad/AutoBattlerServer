using DummySubServer;
using SubServer.Common.Messaging;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
});

var sharedConfigPath = Path.Combine(AppContext.BaseDirectory, "shared.appsettings.json");
builder.Configuration.AddJsonFile(sharedConfigPath, optional: false, reloadOnChange: true);

// 하위 서버는 HTTP 엔드포인트가 필요 없으므로 포트를 열지 않는다.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5300, listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
});

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

app.Run();
