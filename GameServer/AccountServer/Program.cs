using Harness.Agent;
using AccountServer;
using AccountServer.Debug;
using AccountServer.Services;
using MainServer.Common.Heartbeat;
using Shared.Common.Hosting;
using MainServer.Common.MasterConnection;
using MainServer.Common.Messaging;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Shared.Common.Auth;
using Shared.Common.Endpoints;
using Shared.Common.Redis;

var builder = WebApplication.CreateBuilder(args);

var sharedConfigPath = Path.Combine(AppContext.BaseDirectory, "shared.appsettings.json");
builder.Configuration.AddJsonFile(sharedConfigPath, optional: false, reloadOnChange: true);

builder.ConfigureGameServerEndpoints(grpcPort: ServerPorts.AccountGrpc, httpPort: ServerPorts.AccountHttp);

builder.Services.Configure<MasterConnectionOptions>(
    builder.Configuration.GetSection(MasterConnectionOptions.SectionName));
builder.Services.AddSingleton<MasterConnectionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MasterConnectionService>());

// Redis - 계정 서버는 세션 관리 권한이랑 무관하지만(로그인 서버가 그 역할), 마스터가
// 다른 메인 서버들과 동일하게 세션 레지스트리 주소를 브로드캐스트하므로 받는 배관은
// 그냥 갖춘다 (실제로 ISessionRegistry를 쓰지는 않음).
builder.Services.AddSessionRegistry();
builder.Services.AddHostedService<ServerHeartbeatService>();

// 계정 저장소 - 지금은 인메모리 더미. 진짜 SQL 서버가 생기면 이 등록만 교체.
builder.Services.AddSingleton<IAccountRepository, InMemoryAccountRepository>();

// 우편함 - 하위 서버가 처리 결과를 영속 기록하는 곳.
builder.Services.AddSingleton<IMailRepository, InMemoryMailRepository>();
builder.Services.AddSingleton<MailboxEvictor>();

// 비밀번호는 절대 원문으로 저장 안 함 - PBKDF2 해시만 저장.
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();

// 친구 삭제 이벤트를 채팅 서버에 알리기 위한 RabbitMQ - 아웃박스(쌓아뒀다가 브로커 확인을 받아야 빼기)는
// RabbitMqBroker가 맡는다(예전 FriendEventOutbox/FriendEventPublisher를 대체).
builder.Services.AddMainServerMessaging(builder.Configuration);

builder.Services.AddGrpc();
builder.Services.AddScoped<AccountControlService>();

// 테스트 하네스 에이전트(테스트 전용) - HARNESS_ADDRESS 환경 변수가 없으면 아무것도 안 한다.
builder.Services.AddHarnessAgent();

var app = builder.Build();

var masterConnection = app.Services.GetRequiredService<MasterConnectionService>();
app.Lifetime.ApplicationStarted.Register(() =>
{
    masterConnection.UpdateStatus(currentLoad: 0, capacity: 1000); // 조회/생성 저장소라 수용량 개념이 느슨함
});

app.MapGrpcService<AccountControlService>();
app.MapGet("/", () => "Account Server.");

app.Run();