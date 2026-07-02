using MasterServer;
using MasterServer.Registry;
using MasterServer.Routing;
using MasterServer.Services;

var builder = WebApplication.CreateBuilder(args);

// TLS 없이는 같은 포트에서 HTTP/1.1과 HTTP/2를 같이 협상할 수 없다 (ALPN이 그 역할을
// 하는데 TLS가 없으면 ALPN 자체가 없음). Http1AndHttp2를 평문 포트에 쓰면 모든 연결이
// HTTP/1.1로 취급되어 gRPC 클라이언트가 HTTP_1_1_REQUIRED로 거부당한다 (실제로 겪은 버그).
// 그래서 gRPC 전용 포트와 REST 테스트용 포트를 분리한다.
builder.WebHost.ConfigureKestrel(options =>
{
    // gRPC 전용 포트 - HTTP/2만 허용
    options.ListenAnyIP(5100, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
    });

    // 테스트/헬스체크용 REST 포트 - HTTP/1.1만 허용 (gRPC와 절대 같은 포트에 두지 않는다)
    options.ListenAnyIP(5101, listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1;
    });

    // 애플리케이션 레벨 하트비트 타임아웃(MasterControlService)과는 별개로,
    // 전송 계층에서도 HTTP/2 PING 프레임으로 연결 생사를 주기적으로 확인한다.
    options.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(10);
    options.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(5);
});

builder.Services.AddGrpc();
builder.Services.AddSingleton<InstanceRegistry>();
builder.Services.AddSingleton<IRoutingStrategy, LeastLoadRoutingStrategy>();

var app = builder.Build();

app.MapGrpcService<MasterControlService>();
app.MapGet("/", () => "Master Server gRPC 엔드포인트입니다. gRPC 클라이언트로 접속하세요.");

// RabbitMQ 연동 전까지, Broadcast가 실제로 동작하는지 수동으로 찔러볼 수 있는 임시 엔드포인트.
// curl -X POST "http://localhost:5101/test/broadcast?message=점검 5분 후 시작"
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
