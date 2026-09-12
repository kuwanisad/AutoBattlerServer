using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;

namespace Shared.Common.Hosting;

/// <summary>
/// gRPC(주 포트)/REST(보조 포트) 두 개를 한 번에 구성한다. TLS 여부는 코드가 아니라
/// shared.appsettings.json의 "UseTls" 값으로 결정한다 - 서버 전체가 이 값을 같이 보고
/// 일괄로 켜지거나 꺼지므로, 로컬 개발 중엔 false로 빠르게 테스트하다가 인증서가
/// 준비되면 그 값만 true로 바꾸면 된다(코드 재배포 불필요).
///
/// MainServer.Common이 아니라 Shared.Common에 두는 이유: MainServer.Common은 "마스터한테
/// 접속하는 메인 서버들" 전용이라 마스터 서버 자신은 참조하지 않는데, Kestrel 호스팅은
/// 마스터 서버를 포함해 전부 필요하다. Shared.Common은 이미 모든 프로젝트가 참조하고
/// 있어서 새 프로젝트 참조를 추가할 필요가 없다.
///
/// UseHttps()를 인자 없이 부르면 .NET 개발용 인증서를 쓴다 - 로컬에서 한 번
/// `dotnet dev-certs https --trust`로 신뢰해두면, localhost로 뜨는 서버들이 전부 이
/// 인증서 하나를 공유해서 쓸 수 있다.
/// </summary>
public static class KestrelEndpointExtensions
{
    public static bool ConfigureGameServerEndpoints(
        this WebApplicationBuilder builder, int grpcPort, int httpPort)
    {
        var useTls = builder.Configuration.GetValue<bool>("UseTls");

        builder.WebHost.ConfigureKestrel(options =>
        {
            ConfigureHttp2KeepAlive(options);

            options.ListenAnyIP(grpcPort, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http2;
                if (useTls) listenOptions.UseHttps();
            });

            options.ListenAnyIP(httpPort, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1;
                if (useTls) listenOptions.UseHttps();
            });
        });

        return useTls;
    }

    /// <summary>
    /// REST 보조 포트가 없는 서버용 오버로드(예: MatchingServer - gRPC 포트 하나만 씀).
    /// 로직은 완전히 같고 포트 하나만 덜 여는 것뿐이라, 별도로 베껴 쓰지 않고 여기 묶어둔다.
    /// </summary>
    public static bool ConfigureGameServerEndpoints(this WebApplicationBuilder builder, int grpcPort)
    {
        var useTls = builder.Configuration.GetValue<bool>("UseTls");

        builder.WebHost.ConfigureKestrel(options =>
        {
            ConfigureHttp2KeepAlive(options);

            options.ListenAnyIP(grpcPort, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http2;
                if (useTls) listenOptions.UseHttps();
            });
        });

        return useTls;
    }

    /// <summary>
    /// 서버-스트리밍 gRPC 호출(StayConnected, RequestMatch 등)은 서버가 클라에게 계속
    /// 뭔가를 "쓰려고 시도"할 때만 끊긴 연결을 알아챈다 - 조용히 대기만 하는 스트림은
    /// (예: Task.Delay(Timeout.Infinite, context.CancellationToken)) 그 CancellationToken이
    /// 언제 취소되는지 전적으로 OS 수준 TCP 타임아웃에 의존하게 되는데, 이게 명시적으로
    /// 설정 안 하면 몇 분이 걸릴지 예측이 안 된다(실제로 겪음 - 어떤 때는 1분,
    /// 어떤 때는 그보다 훨씬 오래 걸림, KNOWN_ISSUES.md 참고). HTTP/2 PING 기반
    /// keepalive를 명시적으로 켜서, 일정 시간 안에 응답이 없으면 확실하게 연결을 끊긴
    /// 것으로 판정하게 만든다.
    /// </summary>
    private static void ConfigureHttp2KeepAlive(KestrelServerOptions options)
    {
        // 이 시간 동안 아무 통신도 없으면 PING을 보낸다.
        options.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(15);
        // PING을 보낸 뒤 이 시간 안에 응답이 없으면 연결을 끊긴 것으로 판정한다.
        options.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(15);
    }
}