using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Shared.Common.Messaging;

/// <summary>
/// "성공할 때까지 일정 간격으로 다시 시도"하는 접속 절차의 공통 구현. 접속 대상(RabbitMQ, 나중엔
/// 다른 브로커/서버 연결)과 무관하게 같은 규칙으로 동작하게 하려고 따로 뺐다.
///
/// 규칙:
/// - 실패하면 interval만큼 쉬고 다시 시도한다. 횟수 제한은 없다(stoppingToken이 취소될 때까지).
/// - 로그는 첫 실패 때 경고 한 번, 성공했을 때 "n회 시도, 몇 초 걸림" 한 번만 남긴다 -
///   대상이 오래 죽어 있어도 같은 경고가 interval마다 쌓이지 않게.
/// </summary>
public static class ReconnectPolicy
{
    public static async Task<T> ConnectUntilSuccessAsync<T>(
        Func<T> connect,
        TimeSpan interval,
        ILogger logger,
        string targetName,
        CancellationToken stoppingToken)
    {
        var attempt = 0;
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            stoppingToken.ThrowIfCancellationRequested();
            attempt++;

            try
            {
                var result = connect();
                if (attempt == 1)
                {
                    logger.LogInformation("{Target} 연결 성공", targetName);
                }
                else
                {
                    logger.LogInformation("{Target} 연결 성공 ({Attempt}회 시도, {Elapsed:0.#}초 걸림)",
                        targetName, attempt, stopwatch.Elapsed.TotalSeconds);
                }
                return result;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                if (attempt == 1)
                {
                    logger.LogWarning("{Target} 연결 실패 - 성공할 때까지 {Interval}초마다 재시도: {Error}",
                        targetName, interval.TotalSeconds, ex.Message);
                }
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}
