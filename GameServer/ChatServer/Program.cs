using ChatServer;
using MainServer.Common.Heartbeat;
using Shared.Common.Hosting;
using MainServer.Common.MasterConnection;
using MainServer.Common.Messaging;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Shared.Common.Redis;

var builder = WebApplication.CreateBuilder(args);

var sharedConfigPath = Path.Combine(AppContext.BaseDirectory, "shared.appsettings.json");
builder.Configuration.AddJsonFile(sharedConfigPath, optional: false, reloadOnChange: true);

// 클라이언트 WebSocket 포트(8000, HTTP/1.1)/마스터 서버 gRPC 포트(8001, HTTP/2) -
// 다른 서버들과 포트 역할이 반대(주 포트가 gRPC가 아니라 WebSocket)라, 그대로 맞춰서
// 이름 있는 인자로 넘긴다.
builder.ConfigureGameServerEndpoints(grpcPort: 8001, httpPort: 8000);

builder.Services.Configure<ChatServerOptions>(
    builder.Configuration.GetSection(ChatServerOptions.SectionName));

// 마스터 서버 연결
builder.Services.Configure<MasterConnectionOptions>(
    builder.Configuration.GetSection(MasterConnectionOptions.SectionName));
builder.Services.AddSingleton<MasterConnectionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MasterConnectionService>());

// 브로커
builder.Services.AddMainServerMessaging(builder.Configuration);

// 계정 서버가 발행하는 친구 삭제 이벤트를 구독해서 둘만의 채팅방을 정리한다
// (수동 ack - 처리가 확실히 끝나야만 ack, 실패하면 재큐잉).
builder.Services.AddHostedService<FriendRemovedSubscriber>();

// 지금 접속 중인 클라 추적 (강제 종료 대상 찾기용) + 메인 서버가 세션을 지울 때
// 채팅 연결도 강제 종료 (SessionInvalidated 구독). "서비스 제공 가능 여부는 전적으로
// 메인 서버한테 맡긴다"는 원칙 - 채팅 서버가 스스로 판단하지 않는다.
builder.Services.AddSingleton<ChatConnectionRegistry>();
builder.Services.AddHostedService<SessionInvalidatedSubscriber>();

// Redis - 세션 레지스트리(토큰 검증 읽기 전용)
builder.Services.AddSessionRegistry();
builder.Services.AddHostedService<ServerHeartbeatService>();

// 채팅 서버 핵심 서비스 - 채팅방/메시지 저장소는 세션 레지스트리와 완전히 별개인
// 채팅 서버 전용 Redis 연결(ChatRedisConnection)을 쓴다.
builder.Services.AddSingleton<ChatRedisConnection>();
builder.Services.AddSingleton<IChatRoomStore, RedisChatRoomStore>();
builder.Services.AddSingleton<IChatMessageStore, RedisChatMessageStore>();
builder.Services.AddSingleton<IChatFilter, DummyChatFilter>();
builder.Services.AddSingleton<WebSocketHandler>();

// gRPC (마스터 서버와 통신)
builder.Services.AddGrpc();

var app = builder.Build();

// WebSocket 활성화
app.UseWebSockets();

// WebSocket 엔드포인트
app.Map("/ws", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        var handler = context.RequestServices.GetRequiredService<WebSocketHandler>();
        await handler.HandleAsync(context);
    }
    else
    {
        context.Response.StatusCode = 400;
    }
});

app.MapGet("/", () => "Chat Server.");

app.Lifetime.ApplicationStarted.Register(() =>
{
    var masterConnection = app.Services.GetRequiredService<MasterConnectionService>();
    var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ChatServerOptions>>().Value;
    masterConnection.UpdateStatus(currentLoad: 0, capacity: options.Capacity);

    app.Logger.LogInformation("채팅 서버 시작 완료. WebSocket 포트={WsPort} gRPC 포트={GrpcPort}",
        options.WebSocketPort, options.GrpcPort);
});

app.Run();
