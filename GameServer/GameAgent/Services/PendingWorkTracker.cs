using System.Collections.Concurrent;

namespace GameAgent.Services;

/// <summary>
/// 게임 서버와 주고받는 비동기 왕복 업무(요청 보내고 응답 올 때까지 대기)를 추적한다.
/// 업무 종류가 늘어나도(RoomOpened 외에 다른 응답 타입이 생겨도) 이 클래스 하나로 다 담는다 -
/// 종류별로 딕셔너리를 따로 만들고 연결 끊김 시 정리 코드를 하나씩 늘리지 않기 위함이다.
///
/// GameServerHandler가 게임 서버와의 연결이 끊겼다고 판단하면(하트비트 타임아웃 포함)
/// Cleanup() 한 번만 호출하면, 업무가 몇 종류로 늘어나든 대기 중인 전부가 일괄 실패 처리된다.
/// </summary>
public sealed class PendingWorkTracker
{
    private readonly ConcurrentDictionary<string, IPendingWorkEntry> _pending = new();

    /// <summary>
    /// key로 업무를 등록하고, TryComplete(key, ...)가 호출되거나 timeout이 지날 때까지 대기한다.
    /// timeout이 지나면 OperationCanceledException으로 끝난다.
    /// </summary>
    public async Task<T> WaitForAsync<T>(string key, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pending.TryAdd(key, new Entry<T>(tcs)))
        {
            throw new InvalidOperationException($"이미 진행 중인 요청: {key}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        await using var registration = timeoutCts.Token.Register(
            () => tcs.TrySetCanceled(timeoutCts.Token));

        try
        {
            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// key로 대기 중인 업무를 결과로 완료시킨다. 대기 중이 아니거나(이미 타임아웃/정리됨)
    /// 타입이 안 맞으면 조용히 false를 반환한다.
    /// </summary>
    public bool TryComplete<T>(string key, T result)
    {
        if (_pending.TryRemove(key, out var entry) && entry is Entry<T> typed)
        {
            typed.Tcs.TrySetResult(result);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 연결이 끊겼을 때 호출한다. 업무 종류 상관없이 대기 중인 전부를 일괄 실패 처리한다 -
    /// 계산이 끝났든 안 끝났든, 보고할 곳이 없어졌으면 전부 무효로 본다.
    /// </summary>
    public void Cleanup(string reason = "연결이 끊겼습니다.")
    {
        var exception = new InvalidOperationException(reason);

        foreach (var key in _pending.Keys.ToList())
        {
            if (_pending.TryRemove(key, out var entry))
            {
                entry.Fail(exception);
            }
        }
    }

    private interface IPendingWorkEntry
    {
        void Fail(Exception exception);
    }

    private sealed class Entry<T> : IPendingWorkEntry
    {
        public TaskCompletionSource<T> Tcs { get; }

        public Entry(TaskCompletionSource<T> tcs)
        {
            Tcs = tcs;
        }

        public void Fail(Exception exception) => Tcs.TrySetException(exception);
    }
}