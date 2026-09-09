using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Shared.Common.Threading;

/// <summary>
/// 적응형 대기 상태 관리.
/// 메시지가 자주 오는 구간에서는 스핀 대기로 반응성을 높이고,
/// 뜸한 구간에서는 블로킹 대기로 전환해 리소스를 절약한다.
///
/// 주의: SingleReader = true인 Channel에서 사용해야 한다.
/// SingleReader = false라면 WaitToReadAsync가 true를 반환한 후
/// 다른 소비자가 먼저 꺼내가서 TryRead가 실패하는 케이스가 생길 수 있다.
/// 그 경우 메시지가 있는데도 없다고 판정해 다시 블로킹 대기로 빠질 수 있다.
/// </summary>
public struct AdaptiveWaiter
{
    private readonly int _spinThreshold;
    private int _spinCount;

    /// <param name="spinThreshold">
    /// 스핀 대기에서 블로킹 대기로 전환하기 전 유지할 활성 횟수.
    /// 값이 클수록 반응성은 높아지지만 CPU 사용량이 늘어난다.
    /// </param>
    public AdaptiveWaiter(int spinThreshold)
    {
        _spinThreshold = spinThreshold;
        _spinCount = 0;
    }

    /// <summary>
    /// 메시지를 성공적으로 처리했을 때 호출한다.
    /// 활성 구간으로 진입/유지해서 다음 메시지를 스핀으로 기다린다.
    /// </summary>
    public void OnMessageProcessed()
    {
        _spinCount = Math.Min(_spinCount + 1, _spinThreshold);
    }

    /// <summary>
    /// 메시지가 없을 때 호출한다.
    /// 최근 활성 이력이 있으면 스핀 대기(Task.Yield), 없으면 블로킹 대기(WaitToReadAsync)를 반환한다.
    /// 호출자는 반환된 Task를 await하면 된다.
    /// </summary>
    public Task WaitAsync<T>(ChannelReader<T> reader, CancellationToken cancellationToken)
    {
        if (_spinCount > 0)
        {
            _spinCount--;
            return YieldAsync();
        }

        return reader.WaitToReadAsync(cancellationToken).AsTask();
    }

    private static async Task YieldAsync() => await Task.Yield();
}