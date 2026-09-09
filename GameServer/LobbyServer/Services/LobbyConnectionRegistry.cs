using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels;

namespace LobbyServer.Services;

/// <summary>
/// playerId → StayConnected 채널. NotifyMailUpdated 등 다른 요청 핸들러가 이미 열려있는
/// StayConnected 스트림에 뭔가를 push해야 할 때 이걸 통해서만 쓴다(직접 스트림에 안 씀 -
/// 동시 쓰기 방지). ChatServer의 ChatConnectionRegistry와 같은 역할, 같은 이유.
/// </summary>
public sealed class LobbyConnectionRegistry
{
    /// <summary>
    /// "지금 접속 중인 전체 명단"과 관련된 상태(접속자 등록/해제, 공지 활성화 목록)를
    /// 조작하려면 반드시 이 락을 잡아야 한다 - 안 그러면 "클라 접속 등록"과 "공지
    /// 활성화 시 전체 스냅샷"이 서로 겹쳐서, 그 찰나에 접속한 클라가 공지를 영영
    /// 못 받는 경합이 생긴다(KNOWN_ISSUES.md 참고).
    ///
    /// GlobalWorker(공지 등 클라 대상 작업 큐, 구현 예정)의 락과 중첩해서 걸어야 하는
    /// 경우, 항상 이 락을 먼저 잡고 그 안에서 GlobalWorker의 락을 잡을 것 - 순서가
    /// 뒤바뀌면 데드락 위험이 생긴다.
    /// </summary>
    internal readonly object SyncRoot = new();

    private readonly ConcurrentDictionary<string, Channel<global::LobbyServer.StayConnectedEvent>> _channels = new();

    public void Register(string playerId, Channel<global::LobbyServer.StayConnectedEvent> channel) =>
        _channels[playerId] = channel;

    public void Unregister(string playerId) => _channels.TryRemove(playerId, out _);

    public bool TryGet(string playerId, out Channel<global::LobbyServer.StayConnectedEvent> channel) =>
        _channels.TryGetValue(playerId, out channel!);

    /// <summary>지금 접속 중인 전체 playerId 스냅샷. 호출부가 SyncRoot를 이미 잡고 있는
    /// 상태에서만 호출해야 한다(그래야 스냅샷 뜨는 동안 새 접속이 못 끼어듦) - 이
    /// 메서드 자체는 락을 안 건다.</summary>
    public IReadOnlyList<string> SnapshotPlayerIdsUnderLock() => _channels.Keys.ToList();
}
