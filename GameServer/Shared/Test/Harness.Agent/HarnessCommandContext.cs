namespace Harness.Agent;

/// <summary>
/// 지금 실행 중인 [HarnessCommand]에서 쓰는 것.
///
/// <see cref="AfterReply"/> - "이 명령의 결과가 하네스로 전송된 뒤에" 할 일을 등록한다. 결과를 보내기 전에 연결이
/// 사라질 수 있는 동작(예: 자기 프로세스 종료, 하네스 연결을 끊는 일)은 명령 함수에서 바로 하지 말고 여기에 등록한다.
/// 모든 명령이 같은 경로를 탄다: 실행 → 결과 전송 → 등록한 일 실행. 실행 시점은 HarnessAgentService가 정한다
/// (결과가 실제로 전송됨 또는 연결이 없어 2초 지남 - 하네스가 없을 때 영영 안 되는 일이 없게). 실행은 스레드 풀이고,
/// 명령이 실패(ok=false)하면 실행하지 않는다.
///
/// Unity 쪽(AutoBattler.Test.Harness.HarnessCommandContext)에도 같은 이름과 규칙이 있다 - 차이는 Unity는 메인 스레드에서 실행.
/// </summary>
public static class HarnessCommandContext
{
    // 서버 명령은 각자 비동기 작업으로 동시에 돈다 - 명령마다 자기 목록을 갖도록 AsyncLocal(await를 건너 따라간다).
    private static readonly AsyncLocal<AfterReplyList?> Current = new();

    /// <summary>이 명령의 결과가 하네스로 전송된 뒤 action을 실행한다. 명령 실행 중에만 부를 수 있다.</summary>
    public static void AfterReply(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var list = Current.Value ?? throw new InvalidOperationException("AfterReply는 [HarnessCommand] 실행 중에만 부를 수 있다");
        list.Add(action);
    }

    /// <summary>명령 실행 직전(HarnessAgentService) - 이 비동기 흐름에 새 목록을 건다.</summary>
    internal static AfterReplyList Begin()
    {
        var list = new AfterReplyList();
        Current.Value = list;
        return list;
    }

    /// <summary>명령 실행 직후(HarnessAgentService).</summary>
    internal static void End() => Current.Value = null;

    internal sealed class AfterReplyList
    {
        private readonly List<Action> _actions = new();

        public void Add(Action action)
        {
            lock (_actions) _actions.Add(action);
        }

        public Action[] ToArray()
        {
            lock (_actions) return _actions.ToArray();
        }
    }
}
