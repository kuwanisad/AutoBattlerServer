public sealed class ChatServerOptions
{
    public const string SectionName = "ChatServer";

    /// <summary>클라이언트가 WebSocket으로 접속하는 포트.</summary>
    public int WebSocketPort { get; set; } = 8000;

    /// <summary>마스터 서버와 gRPC로 통신하는 포트.</summary>
    public int GrpcPort { get; set; } = 8001;

    /// <summary>최대 WebSocket 연결 수.</summary>
    public int Capacity { get; set; } = 1000;
}
