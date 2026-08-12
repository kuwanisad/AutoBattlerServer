namespace Shared.Common.Redis;

/// <summary>
/// 클라이언트 상태를 중앙에서 관리하는 Session Registry.
/// Redis에 구현되며 모든 서버가 동일한 상태를 조회할 수 있다.
///
/// 역할:
/// 1. 플레이어 세션 관리 (로비/매칭/이동중/인게임 상태)
/// 2. 서버 생존 TTL 관리 (서버가 5초마다 갱신, TTL=10초)
/// 3. 객체 권한 관리 (PlayerSession.OwnerServerId로 통합)
///
/// 클라이언트가 서버를 이동할 때 상태를 업데이트해서
/// "이 클라이언트가 어디에 있는지"를 모든 서버가 알 수 있게 한다.
/// 해커가 타인의 토큰을 들고 다른 서버에 접속하려 해도,
/// Registry에 해당 클라이언트의 이동 기록이 없으면 접속을 차단할 수 있다.
/// </summary>
public interface ISessionRegistry
{
    // === 플레이어 세션 ===

    /// <summary>플레이어 세션을 등록한다. 로그인 시 호출한다.</summary>
    Task RegisterAsync(string playerId, PlayerSession session);

    /// <summary>플레이어 세션을 조회한다. 없으면 null을 반환한다.</summary>
    Task<PlayerSession?> GetAsync(string playerId);

    /// <summary>플레이어 세션을 업데이트한다. 서버 이동 및 권한 변경 시 호출한다.</summary>
    Task UpdateAsync(string playerId, PlayerSession session);

    /// <summary>플레이어 세션을 삭제한다. 로그아웃 시 호출한다. OwnerServerId도 함께 삭제된다.</summary>
    Task RemoveAsync(string playerId);

    /// <summary>플레이어가 등록되어 있는지 확인한다.</summary>
    Task<bool> ExistsAsync(string playerId);

    // === 서버 생존 TTL ===

    /// <summary>
    /// 서버 생존 TTL을 갱신한다.
    /// 메인 서버가 5초마다 호출한다. TTL=10초.
    /// TTL이 만료되면 해당 서버가 죽은 것으로 간주한다.
    /// Redis key: server:{serverId}
    /// </summary>
    Task RefreshServerHeartbeatAsync(string serverId);

    /// <summary>서버가 살아있는지 확인한다.</summary>
    Task<bool> IsServerAliveAsync(string serverId);

    // === 객체 권한 관리 ===

    /// <summary>
    /// 객체의 관리 권한 이전을 요청한다. Lua 스크립트로 원자적 처리.
    /// PlayerSession.OwnerServerId의 기존 서버 TTL이 만료됐으면
    /// OwnerServerId를 새 서버로 교체 후 true 반환.
    /// 기존 서버가 아직 살아있으면 false 반환 (접속 거부).
    /// 클라가 새 서버에 접속 시 호출한다.
    /// </summary>
    Task<bool> TryClaimOwnershipAsync(string serverId, string playerId);
}

/// <summary>플레이어 현재 상태.</summary>
public enum PlayerStatus
{
    Lobby,      // 로비 서버에 있음
    Matching,   // 매칭 대기 중
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
    /// 일회용 매치 토큰.
    /// 게임 서버 접속 시 검증 후 즉시 삭제된다.
    /// </summary>
    public string MatchToken { get; set; } = string.Empty;

    /// <summary>
    /// 이 플레이어의 관리 권한을 보유한 서버 ID.
    /// 로그인 시 로비 서버가 설정하고, 인게임 접속 시 게임 에이전트로 이전된다.
    /// TryClaimOwnershipAsync로 원자적으로 변경된다.
    /// </summary>
    public string OwnerServerId { get; set; } = string.Empty;

    /// <summary>마지막 업데이트 시각 (Unix ms).</summary>
    public long UpdatedAtUnixMs { get; set; }
}