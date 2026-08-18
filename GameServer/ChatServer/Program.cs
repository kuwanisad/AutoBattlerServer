using MainServer.Common.Heartbeat;
using MainServer.Common.MasterConnection;
using MainServer.Common.Messaging;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Shared.Common.Redis;

var builder = WebApplication.CreateBuilder(args);

var sharedConfigPath = Path.Combine(AppContext.BaseDirectory, "shared.appsettings.json");
builder.Configuration.AddJsonFile(sharedConfigPath, optional: false, reloadOnChange: true);

builder.WebHost.ConfigureKestrel(options =>
{
    // 클라이언트 WebSocket 포트 (HTTP/1.1)
    options.ListenAnyIP(8000, listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
    // 마스터 서버 gRPC 포트 (HTTP/2)
    options.ListenAnyIP(8001, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
});

builder.Services.Configure<ChatServerOptions>(
    builder.Configuration.GetSection(ChatServerOptions.SectionName));

// 마스터 서버 연결
builder.Services.Configure<MasterConnectionOptions>(
    builder.Configuration.GetSection(MasterConnectionOptions.SectionName));
builder.Services.AddSingleton<MasterConnectionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MasterConnectionService>());

// 브로커
builder.Services.AddMainServerMessaging(builder.Configuration);

// Redis
builder.Services.AddSessionRegistry();
builder.Services.AddHostedService<ServerHeartbeatService>();

// 채팅 서버 핵심 서비스
builder.Services.AddSingleton<IChatMessageStore, InMemoryChatMessageStore>();
builder.Services.AddSingleton<IChatFilter, DummyChatFilter>();
builder.Services.AddSingleton<ChatChannelManager>();
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
