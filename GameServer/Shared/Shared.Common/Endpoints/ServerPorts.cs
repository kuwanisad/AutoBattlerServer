namespace Shared.Common.Endpoints;

/// <summary>
/// 로컬 개발 환경에서 각 서버가 사용하는 gRPC/REST 포트 번호를 한 곳에 모아둔다.
///
/// 예전엔 각 서버 프로젝트의 Program.cs에 포트 숫자가 리터럴로 직접 박혀 있어서,
/// 포트 하나를 바꾸려면 그 서버의 Program.cs뿐 아니라 그 포트를 참조하는 다른
/// 서버들의 appsettings.json(MasterAddress 등)까지 각각 찾아서 고쳐야 했다 -
/// 최소한 코드 쪽에 흩어져 있던 리터럴은 이제 여기 하나로 모인다.
///
/// 실제 배포 환경에서는 각 서버 폴더의 appsettings.json이 이 값들을 서버별로
/// 덮어쓴다(ENVIRONMENT.md 참고) - 여기 상수는 "로컬 한 대에서 그냥 띄웠을 때"의
/// 기본값이자, Program.cs가 ConfigureGameServerEndpoints를 호출할 때 쓰는 값이다.
/// </summary>
public static class ServerPorts
{
    /// <summary>로컬 개발 환경 기본 호스트.</summary>
    public const string LocalHost = "localhost";

    public const int MasterGrpc = 5100;
    public const int MasterHttp = 5101;

    public const int LobbyGrpc = 5200;
    public const int LobbyHttp = 5201;

    public const int DummySubServerGrpc = 5300;

    public const int LoginGrpc = 5400;
    public const int LoginHttp = 5401;

    public const int AccountGrpc = 5500;
    public const int AccountHttp = 5501;

    /// <summary>매칭 서버는 REST 보조 포트가 없다 - gRPC 포트 하나만 연다.</summary>
    public const int MatchingGrpc = 6000;

    public const int GameAgentGrpc = 7000;
    public const int GameAgentHttp = 7001;

    public const int ChatGrpc = 8001;
    public const int ChatHttp = 8000;
}
