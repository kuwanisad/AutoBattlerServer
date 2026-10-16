using GameServer.Common;

namespace MasterServer.Registry;

/// <summary>공지를 켠 대상 범위 - /admin/announce의 instanceId/serverType 인자와 같은 세 가지.</summary>
public readonly record struct AnnouncementScope(AnnouncementScopeKind Kind, ServerType ServerType, string InstanceId)
{
    public static AnnouncementScope All => new(AnnouncementScopeKind.All, ServerType.Unspecified, string.Empty);
    public static AnnouncementScope ForServerType(ServerType type) => new(AnnouncementScopeKind.ServerType, type, string.Empty);
    public static AnnouncementScope ForInstance(string instanceId) => new(AnnouncementScopeKind.Instance, ServerType.Unspecified, instanceId);

    public bool Matches(string instanceId, ServerType serverType) => Kind switch
    {
        AnnouncementScopeKind.All => true,
        AnnouncementScopeKind.ServerType => ServerType == serverType,
        AnnouncementScopeKind.Instance => InstanceId == instanceId,
        _ => false,
    };

    public override string ToString() => Kind switch
    {
        AnnouncementScopeKind.All => "전체",
        AnnouncementScopeKind.ServerType => $"타입={ServerType}",
        AnnouncementScopeKind.Instance => $"인스턴스={InstanceId}",
        _ => Kind.ToString(),
    };
}

public enum AnnouncementScopeKind
{
    All,
    ServerType,
    Instance,
}

/// <summary>
/// 지금 켜져 있는 공지(종류 + 대상 범위)를 기억한다. 공지 지시는 "켜짐/꺼짐" 상태라서, 그 순간
/// 끊겨 있던 인스턴스는 재접속해도 모르는 채로 남는다 - 그래서 인스턴스가 (재)접속하면
/// ConnectionHandler가 여기서 그 인스턴스가 대상인 켜진 공지를 찾아 한 번 다시 보낸다
/// (쌓아뒀다 재전송하는 게 아니라 "현재 상태"를 보내는 것 - 그 사이 꺼진 공지는 안 보냄).
///
/// 끄기 규칙: 같은 범위로 끄면 그 항목만 지운다. 전체 범위로 끄면 그 종류의 항목을 범위와
/// 무관하게 모두 지운다(전체를 껐는데 특정 타입 대상으로 켰던 게 남아 재접속 때 다시 켜지면
/// 운영자 의도와 어긋나므로).
///
/// 마스터 서버 메모리에만 있다 - 마스터가 재시작되면 사라진다(파일 저장은 하지 않음).
/// </summary>
public sealed class ActiveAnnouncements
{
    private readonly object _lock = new();
    private readonly HashSet<(AnnouncementType Type, AnnouncementScope Scope)> _active = new();

    public void Apply(AnnouncementType type, bool active, AnnouncementScope scope)
    {
        lock (_lock)
        {
            if (active)
            {
                _active.Add((type, scope));
            }
            else if (scope.Kind == AnnouncementScopeKind.All)
            {
                _active.RemoveWhere(e => e.Type == type);
            }
            else
            {
                _active.Remove((type, scope));
            }
        }
    }

    /// <summary>이 인스턴스가 대상인 켜진 공지 종류들(중복 없음).</summary>
    public IReadOnlyList<AnnouncementType> GetFor(string instanceId, ServerType serverType)
    {
        lock (_lock)
        {
            return _active
                .Where(e => e.Scope.Matches(instanceId, serverType))
                .Select(e => e.Type)
                .Distinct()
                .ToList();
        }
    }
}
