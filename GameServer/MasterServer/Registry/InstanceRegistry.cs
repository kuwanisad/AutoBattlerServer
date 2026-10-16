using System.Collections.Concurrent;
using GameServer.Common;

namespace MasterServer.Registry;

/// <summary>여러 인스턴스에 같은 메시지를 넣은 결과 - 관리자 API 응답과 로그에 쓴다.</summary>
public sealed record DeliveryReport(int Total, IReadOnlyList<string> Delivered, IReadOnlyList<string> Failed)
{
    public bool AllDelivered => Failed.Count == 0;

    public override string ToString() =>
        Failed.Count == 0
            ? $"대상 {Total}개 전부 전달"
            : $"대상 {Total}개 중 {Delivered.Count}개 전달, 실패: {string.Join(", ", Failed)}";
}

/// <summary>
/// 현재 마스터 서버에 접속 중인 모든 메인 서버 인스턴스를 추적하는 저장소.
/// 인스턴스 수가 적으므로(수십~수백 단위, 용량 계획 기반) 인메모리로 충분하다.
/// 마스터 서버 프로세스가 재시작되면 모든 메인 서버가 다시 접속하면서 자연스럽게 다시 채워진다.
/// </summary>
public sealed class InstanceRegistry
{
    private readonly ConcurrentDictionary<string, InstanceInfo> _instances = new();
    private readonly ILogger<InstanceRegistry> _logger;

    public InstanceRegistry(ILogger<InstanceRegistry> logger)
    {
        _logger = logger;
    }

    public InstanceInfo Upsert(InstanceInfo info)
    {
        return _instances.AddOrUpdate(
            info.InstanceId,
            info,
            (_, existing) =>
            {
                existing.ServerType = info.ServerType;
                existing.Address = info.Address;
                existing.CurrentLoad = info.CurrentLoad;
                existing.Capacity = info.Capacity;
                existing.ConnectionToken = info.ConnectionToken;
                return existing;
            });
    }

    /// <summary>
    /// 해당 instanceId의 항목이 "지금도 내가 등록했던 그 연결"일 때만 제거한다.
    /// 부하로 정리가 지연된 옛 Connect 호출이, 그 사이 재접속한 새 연결의 항목을
    /// 실수로 지우는 것을 막기 위한 조건부 삭제다. 무조건 지우는 Remove(instanceId)는
    /// 더 이상 Connect 정리 경로에서 쓰지 않는다.
    /// </summary>
    public void RemoveIfCurrent(string instanceId, Guid connectionToken)
    {
        if (_instances.TryGetValue(instanceId, out var existing) && existing.ConnectionToken == connectionToken)
        {
            _instances.TryRemove(instanceId, out _);
            existing.OutboundQueue.Writer.TryComplete(); // 큐를 닫아 소비 루프가 자연스럽게 끝나게 한다
        }
    }

    public IReadOnlyCollection<InstanceInfo> GetByType(ServerType type) =>
        _instances.Values.Where(i => i.ServerType == type).ToList();

    public IReadOnlyCollection<InstanceInfo> GetAll() => _instances.Values.ToList();

    /// <summary>instanceId로 인스턴스를 직접 조회한다 (O(1)). GetAll().FirstOrDefault(...)로
    /// 순회하지 말고 이걸 쓴다 - 이미 딕셔너리로 관리하고 있으니 그 장점을 그대로 쓰는 것뿐이다.</summary>
    public InstanceInfo? TryGet(string instanceId) =>
        _instances.TryGetValue(instanceId, out var info) ? info : null;

    /// <summary>
    /// 특정 인스턴스 하나에 메시지를 큐에 넣는다. 실제 전송(WriteAsync)은 Connect의
    /// 소비 루프가 전담하므로 여기서는 그냥 큐에 넣기만 하면 된다 - 동시 쓰기 직렬화를
    /// 호출자가 신경 쓸 필요가 없다.
    ///
    /// 넣지 못하면(접속 중이 아니거나 큐가 이미 닫힘) false를 돌려주고 Warning 로그를 남긴다 -
    /// 예전엔 조용히 무시해서, 관리자 API가 실제로는 못 보낸 지시도 "전달"이라고 응답했다.
    /// 쌓아뒀다 재전송하지는 않는다: 주소 정보와 켜진 공지는 재접속 때 현재 값을 다시 보내고
    /// (ConnectionHandler.HandleStatus), 종료 지시 같은 1회성 명령은 실패가 응답으로 드러나므로
    /// 운영자가 다시 보내면 된다.
    /// </summary>
    public bool Enqueue(string instanceId, global::MasterServer.MasterMessage message)
    {
        if (_instances.TryGetValue(instanceId, out var info)
            && info.OutboundQueue.Writer.TryWrite(message)) // Unbounded 채널이라 닫히지 않았으면 항상 성공
        {
            return true;
        }

        _logger.LogWarning("메시지 전달 실패 - 접속 중이 아닌 인스턴스: {InstanceId} (Message={Message})",
            instanceId, Describe(message));
        return false;
    }

    /// <summary>주어진 인스턴스들에 같은 메시지를 넣고 결과를 돌려준다.</summary>
    public DeliveryReport EnqueueTo(IEnumerable<InstanceInfo> targets, global::MasterServer.MasterMessage message)
    {
        var delivered = new List<string>();
        var failed = new List<string>();

        foreach (var target in targets)
        {
            (Enqueue(target.InstanceId, message) ? delivered : failed).Add(target.InstanceId);
        }

        return new DeliveryReport(delivered.Count + failed.Count, delivered, failed);
    }

    /// <summary>현재 접속 중인 모든 인스턴스의 큐에 같은 메시지를 넣는다 (운영 공지 릴레이 등에 사용).</summary>
    public DeliveryReport Broadcast(global::MasterServer.MasterMessage message) => EnqueueTo(GetAll(), message);

    /// <summary>로그용 메시지 종류 - 지시(Instruction)면 어떤 지시인지까지.</summary>
    public static string Describe(global::MasterServer.MasterMessage message) =>
        message.PayloadCase == global::MasterServer.MasterMessage.PayloadOneofCase.Instruction
            ? $"Instruction.{message.Instruction.TaskCase}"
            : message.PayloadCase.ToString();
}
