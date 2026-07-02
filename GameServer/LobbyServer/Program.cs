using MainServer.Common.MasterConnection;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5200, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
    options.ListenAnyIP(5201, listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
});

builder.Services.Configure<MasterConnectionOptions>(
    builder.Configuration.GetSection(MasterConnectionOptions.SectionName));
builder.Services.AddHostedService<MasterConnectionService>();

var app = builder.Build();

app.MapGet("/", () => "Lobby Server. 지금은 마스터 서버 접속 유지만 담당합니다.");

app.Run();
