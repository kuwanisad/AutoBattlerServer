using DummySubServer;
using SubServer.Common.Messaging;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, config) =>
    {
        var sharedConfigPath = Path.Combine(AppContext.BaseDirectory, "shared.appsettings.json");
        config.AddJsonFile(sharedConfigPath, optional: false, reloadOnChange: true);
    })
    .ConfigureServices((context, services) =>
    {
        services.Configure<HostOptions>(options =>
        {
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
        });

        // Kafka 컨슈머 설정
        services.Configure<KafkaConsumerOptions>(options =>
        {
            context.Configuration.GetSection(KafkaConsumerOptions.SectionName).Bind(options);
            options.GroupId = "dummy-sub-server";
            options.Topics = ["game-events", "match-results"];
        });

        // RabbitMQ 컨슈머 설정
        services.Configure<RabbitMqConsumerOptions>(options =>
        {
            context.Configuration.GetSection(RabbitMqConsumerOptions.SectionName).Bind(options);
            options.QueueName = "reward-queue";
        });

        services.AddHostedService<DummyKafkaConsumer>();
        services.AddHostedService<DummyRabbitMqConsumer>();
    })
    .Build();

host.Run();
