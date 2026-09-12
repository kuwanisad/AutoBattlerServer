namespace Shared.Common.Redis;

/// <summary>
/// 클라이언트 상태를 중앙에서 관리하는 Session Registry.
/// Redis에 구현되며 모든 서버가 동일한 상태를 조회할 수 있다.
///
/// 이 저장소는 "관리 권한 기록부"다 - 지금 이 플레이어를 누가 책임지고 있는지만 기록한다.
/// 매치 토큰이나 방 상태처럼 각 서버가 스스로 알고 있어야 할 업무 데이터는 여기 두지 않는다
/// (그런 건 각 서버가 자기 프로세스 메모리에 들고 있으면 된다 - 예: unordered_map<플레이어ID, 방ID>).
///
/// 클라이언트가 서버를 이동할 때 상태를 업데이트해서
/// "이 클라이언트가 어디에 있는지"를 모든 서버가 알 수 있게 한다.
/// 해커가 타인의 토큰을 들고 다른 서버에 접속하려 해도,
/// Registry에 해당 클라이언트의 이동 기록이 없으면 접속을 차단할 수 있다.
/// </summary>
public interface ISessionRegistry
{
    // === 플레이어 세션 (조회/일반 갱신) ===

    /// <summary>플레이어 세션을 조회한다. 없으면 null을 반환한다.</summary>
    Task<PlayerSession?> GetAsync(string playerId);

    /// <summary>
    /// 플레이어 세션을 업데이트한다. 소유권 변경이 아닌 일반 상태 갱신(Status 전이 등)에 쓴다.
    /// 소유권을 다른 서버로 넘기는 작업은 TryClaimOwnershipAsync 계열을 써야 한다.
    /// </summary>
    Task UpdateAsync(string playerId, PlayerSession session);

    /// <summary>플레이어 세션을 삭제한다. 로그아웃 시 호출한다. OwnerServerId도 함께 삭제된다.</summary>
    Task RemoveAsync(string playerId);

    /// <summary>플레이어가 등록되어 있는지 확인한다.</summary>
    Task<bool> ExistsAsync(string playerId);

    // === 서버 등록/생존 ===

    /// <summary>
    /// 서버 인스턴스를 최초 등록한다. 서버 기동 시 한 번만 호출한다.
    /// ID와 주소를 같이 기록한다 (하트비트는 생존 갱신만 담당하고 주소는 안 건드린다).
    /// Redis key: server:{serverId}, value: address
    /// </summary>
    Task RegisterServerAsync(string serverId, string address);

    /// <summary>
    /// 서버 생존 TTL을 갱신한다. 메인 서버가 5초마다 호출한다. TTL=10초.
    /// 값(주소)은 건드리지 않고 TTL만 갱신하는 게 기본이지만, 키가 아예 없으면(Redis가
    /// 재시작돼 데이터가 날아간 경우 등) address로 다시 등록한다 - 그렇지 않으면 서버
    /// 프로세스 자체는 멀쩡한데 영영 "등록 안 된 서버"로 남아버린다.
    /// TTL이 만료되면 해당 서버가 죽은 것으로 간주한다.
    /// </summary>
    Task RefreshServerHeartbeatAsync(string serverId, string address);

    /// <summary>서버가 살아있는지 확인한다.</summary>
    Task<bool> IsServerAliveAsync(string serverId);

    /// <summary>서버 ID로 등록된 주소를 조회한다. 없거나 죽어있으면 null.</summary>
    Task<string?> GetServerAddressAsync(string serverId);

    // === 객체 권한 관리 ===

    /// <summary>
    /// 플레이어의 현재 소유 서버 정보를 조회한다 (부작용 없음).
    /// 소유자가 없으면 null.
    /// </summary>
    Task<OwnerInfo?> GetOwnerAsync(string playerId);

    /// <summary>
    /// 플레이어 관리 권한을 이 서버로 이전 요청한다. Lua 스크립트로 원자적 처리.
    /// 세션이 없으면(최초 등록) 무조건 승인. 기존 소유 서버의 TTL이 만료됐으면 승인.
    /// 기존 소유 서버가 아직 살아있으면 거부.
    /// 승인되면 newSession 내용으로 세션을 덮어쓰고(OwnerServerId는 이 함수가 serverId로 설정),
    /// 거부되면 기존 세션은 그대로 유지된다.
    /// </summary>
    Task<bool> TryClaimOwnershipAsync(string serverId, string playerId, PlayerSession newSession);

    /// <summary>
    /// TryClaimOwnershipAsync와 동일하게 동작하되, 거부된 경우 기존 소유자 정보를 함께 반환한다.
    /// "권한을 못 받으면 기존 소유자가 어디인지 클라에게 안내해야 하는" 경우(예: 로비 EnterLobby)에 쓴다.
    /// 매번 조회 + 클레임을 따로 호출하는 대신 한 번의 왕복으로 처리하기 위한 것.
    /// </summary>
    Task<ClaimResult> TryClaimOwnershipWithPreviousOwnerAsync(string serverId, string playerId, PlayerSession newSession);
}

/// <summary>플레이어 현재 상태.</summary>
public enum PlayerStatus
{
    // 로비 서버에 있음. 매칭 대기 중(RequestMatch 응답 기다리는 중)도 이 상태
    // 그대로 유지한다 - 예전엔 별도 Matching 상태가 있었는데, 그 상태를 실제로
    // 검사하는 곳이 코드베이스 어디에도 없었고, 오히려 매칭 대기 중 끊긴 세션이
    // HandleStayConnectedEndedAsync의 정리 조건(Status==Lobby)에 안 걸려서 영영
    // 안 지워지는 리크만 만들고 있었다 - 그래서 제거함(KNOWN_ISSUES.md 참고).
    Lobby,
    Moving,     // 서버 이동 중 (취약점 방지용 상태)
    Ingame,     // 인게임 서버에 있음
}

/// <summary>
/// 플레이어 세션 정보.
/// Redis에 저장된다.
/// OwnerServerId를 포함하여 세션과 권한이 단일 키로 관리된다.
/// </summary>
public sealed class PlayerSession
{
    /// <summary>플레이어 ID.</summary>
    public string PlayerId { get; set; } = string.Empty;

    /// <summary>현재 상태.</summary>
    public PlayerStatus Status { get; set; }

    /// <summary>현재 접속 중인 서버 주소.</summary>
    public string CurrentServer { get; set; } = string.Empty;

    /// <summary>
    /// 이동 예정인 서버 주소.
    /// Status가 Moving일 때만 유효하다.
    /// </summary>
    public string NextServer { get; set; } = string.Empty;

    /// <summary>
    /// 이 플레이어의 관리 권한을 보유한 서버 ID.
    /// 로그인 시 로비 서버가 설정하고, 인게임 접속 시 게임 에이전트로 이전된다.
    /// TryClaimOwnershipAsync 계열로 원자적으로 변경된다.
    /// 이 서버만 아래 토큰을 갱신할 권한을 갖는다(1번 결정).
    /// </summary>
    public string OwnerServerId { get; set; } = string.Empty;

    /// <summary>
    /// 소유 서버의 타입(Lobby/Ingame 등). 우편함 시스템에서 하위 서버가 "이 플레이어를
    /// 지금 관리 중인 서버가 로비인지 게임 에이전트인지" 구분해서 서로 다른 RPC를
    /// 걸어야 하는데, 주소만으로는 타입을 알 수 없어서 추가함. OwnerServerId와 함께
    /// 원자적으로 갱신된다.
    ///
    /// 참고: ServerType은 원래 범용 서버 구분용 열거형이지만, 여기선 "권한을 가질 수
    /// 있는 서버 타입"이라는 좁은 의미로도 쓰인다 - 예를 들어 Login은 아주 찰나만
    /// 권한을 갖는 특수 상태라, 그 순간 제3자가 이 값을 보고 로그인 서버한테 뭔가
    /// 요청하면 안 됨(보안). Chat/Matching/Account/SubServer는 애초에 권한을 가질
    /// 개념 자체가 없어서 이 필드에 설정될 일이 원천적으로 없다.
    /// </summary>
    public GameServer.Common.ServerType OwnerServerType { get; set; }

    /// <summary>
    /// 현재 유효한 토큰. 서버 이동 시 클라가 이 값을 제출하면 통과한다.
    /// </summary>
    public string CurrentToken { get; set; } = string.Empty;

    /// <summary>
    /// 바로 직전 토큰. 서버 이동 도중 토큰이 막 갱신된 그 찰나에 클라가 아직 옛 토큰을
    /// 들고 있을 수 있어서(경합), 한 세대 여유를 둔다. 클라가 이 값을 제출하면 통과시키고
    /// 새 토큰을 다시 발급한다(1번 결정).
    /// </summary>
    public string PreviousToken { get; set; } = string.Empty;

    /// <summary>
    /// CurrentToken이 발급된 시각 (Unix ms). TTL 판단에 사용 - 실제 만료 체크 로직은
    /// 아직 미구현(TODO, 재접속 정책과 함께 처리 예정).
    /// </summary>
    public long TokenIssuedAtUnixMs { get; set; }

    /// <summary>마지막 업데이트 시각 (Unix ms).</summary>
    public long UpdatedAtUnixMs { get; set; }
}

/// <summary>플레이어를 현재 소유한 서버 정보.</summary>
public sealed record OwnerInfo(string ServerId, string? Address, bool IsAlive, GameServer.Common.ServerType ServerType);

/// <summary>권한 이전 시도 결과. 거부된 경우 기존 소유자 정보를 담을 수 있다.</summary>
public sealed record ClaimResult(bool Claimed, OwnerInfo? PreviousOwner);