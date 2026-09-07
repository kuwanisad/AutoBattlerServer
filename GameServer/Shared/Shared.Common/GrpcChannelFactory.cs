using Grpc.Net.Client;

namespace Shared.Common;

/// <summary>
/// SocketsHttpHandler를 명시적으로 구성한 gRPC 채널을 만든다 - bare GrpcChannel.ForAddress로
/// 호출했을 때 원인 불명의 "HTTP_1_1_REQUIRED" 오류가 재현된 적이 있어서(KNOWN_ISSUES.md
/// 참고), 검증된 이 패턴을 공용으로 둔다. 특정 서버(AccountServer 등)와 무관한 범용
/// 유틸리티다 - 어느 서버로 향하는 채널이든 이걸로 만든다.
/// </summary>
public static class GrpcChannelFactory
{
    public static GrpcChannel Create(string address)
    {
        var handler = new System.Net.Http.SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(10),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
            KeepAlivePingPolicy = System.Net.Http.HttpKeepAlivePingPolicy.WithActiveRequests,
        };

        return GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
        });
    }
}
