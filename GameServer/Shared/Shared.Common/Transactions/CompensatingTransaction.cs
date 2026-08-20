namespace Shared.Common.Transactions;

/// <summary>
/// 여러 단계를 거치는 작업에서, 각 단계가 남긴 부작용을 되돌릴 방법을 스택으로 쌓아둔다.
/// 중단(연결 끊김 등)되면 Rollback()으로 쌓인 걸 역순(LIFO)으로 실행한다.
///
/// 나중에 쌓인 단계가 먼저 쌓인 단계에 의존하는 경우가 흔하므로(예: "매치 등록"이 먼저 있어야
/// "방 요청"이 의미가 있음) 역순으로 되돌리는 게 안전하다 - 큐(FIFO)로 처리하면 의존하는 대상이
/// 먼저 사라진 채로 뒤 단계를 되돌리려다 실패할 수 있다.
///
/// 특정 도메인(매칭, 방 생성 등)에 종속되지 않는다. 그 도메인의 워크아이템 클래스가
/// 이 클래스를 상속하지 않고 필드로 들고 쓴다(조합) - 그래야 여러 도메인이 각자
/// 다른 상속 계층을 갖고 있어도 이 유틸을 똑같이 재사용할 수 있다.
/// </summary>
public sealed class CompensatingTransaction
{
    private readonly Stack<Action> _compensations = new();

    /// <summary>한 단계를 밟으면서 그걸 되돌릴 방법을 등록한다.</summary>
    public void OnStep(Action compensate) => _compensations.Push(compensate);

    /// <summary>지금까지 쌓인 되돌리기를 전부, 나중에 쌓인 것부터 역순으로 실행한다.</summary>
    public void Rollback()
    {
        while (_compensations.TryPop(out var undo))
        {
            undo();
        }
    }
}