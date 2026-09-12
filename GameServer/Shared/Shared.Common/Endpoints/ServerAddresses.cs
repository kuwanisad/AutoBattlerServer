namespace Shared.Common.Endpoints;

/// <summary>
/// MasterConnectionOptions 등 Options 클래스의 코드 기본값으로 쓰이는 완성된 주소
/// 문자열. ServerPorts의 숫자를 조합해서 만든다.
///
/// 이 값들은 appsettings.json이 없거나 해당 섹션이 비어있을 때만 실제로 쓰인다 -
/// 정상적인 환경에서는 각 서버 폴더의 appsettings.json(MasterConnection 섹션)이
/// 서버별로 항상 이 기본값을 덮어쓴다. 그래도 "설정이 없을 때 조용히 이상한 값으로
/// 시작하는" 상황을 막기 위해 의미 있는 기본값을 코드에도 남겨둔다.
/// </summary>
public static class ServerAddresses
{
    /// <summary>MasterConnectionOptions.MasterAddress 기본값 - 어느 메인 서버든
    /// 결국 마스터 서버 하나를 가리키므로 서버 종류와 무관하게 같은 값이다.</summary>
    public static readonly string DefaultMasterAddress =
        $"http://{ServerPorts.LocalHost}:{ServerPorts.MasterGrpc}";

    /// <summary>MasterConnectionOptions.SelfAddress 기본값. 서버 타입마다 실제 값이
    /// 다르므로(appsettings.json이 항상 덮어씀) 이 기본값 자체엔 특별한 의미가
    /// 없다 - 예전 코드가 쓰던 로비 포트를 그대로 유지해 동작 변화가 없게 했다.</summary>
    public static readonly string DefaultSelfAddress =
        $"{ServerPorts.LocalHost}:{ServerPorts.LobbyGrpc}";
}
