using LobbyServer.MasterConnection;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);

// 마스터 서버와 같은 이유로 포트를 분리해둔다 - 지금은 SelfAddress(5200)를 실제로 쓰는 곳이
// 없지만, 나중에 게임 클라이언트용 gRPC 서비스를 이 포트에 추가할 걸 미리 대비해서
// HTTP_1_1_REQUIRED 버그를 다시 겪지 않게 한다.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5200, listenOptions => listenOptions.Protocols = HttpProtocols.Http2); // 추후 gRPC용
    options.ListenAnyIP(5201, listenOptions => listenOptions.Protocols = HttpProtocols.Http1); // REST/헬스체크용
});

builder.Services.Configure<MasterConnectionOptions>(
    builder.Configuration.GetSection(MasterConnectionOptions.SectionName));
builder.Services.AddHostedService<MasterConnectionService>();

var app = builder.Build();

app.MapGet("/", () => "Lobby Server. 지금은 마스터 서버 접속 유지만 담당합니다.");

app.Run();
