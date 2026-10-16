namespace Harness.Agent;

/// <summary>
/// 테스트 하네스가 이름으로 호출할 수 있는 디버그 명령으로 등록한다.
///
/// 규칙:
/// - 이름은 "&lt;영역&gt;.&lt;동작&gt;" 소문자(예: "agent.status"). 프로세스 안에서 중복되면 시작할 때 예외.
/// - static이든 인스턴스든 된다. 인스턴스 메서드면 호출할 때마다 선언 클래스를 DI로 새로 만든다
///   (생성자에 서버 서비스를 주입받으면 됨 - 클래스는 상태 없이 가볍게).
/// - 파라미터는 하네스가 보낸 인자에서 이름으로(대소문자 무시) 채운다. 기본값이 있으면 생략 가능.
///   CancellationToken 파라미터는 인자가 아니라 하네스 연결이 끊기면 취소되는 토큰을 받는다.
/// - 반환값은 JSON으로 직렬화돼 하네스에 간다(void/Task면 값 없음). Task/Task&lt;T&gt;도 된다.
/// - 예외를 던지면 하네스는 ok=false와 예외 메시지를 받는다.
/// - 상태를 조회/조작하는 명령은 서버의 다른 스레드와 동시에 돈다 - 공유 상태를 건드릴 땐
///   그 상태를 원래 보호하는 방식(락 등)을 그대로 따를 것.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class HarnessCommandAttribute : Attribute
{
    public HarnessCommandAttribute(string name) => Name = name;

    public string Name { get; }

    /// <summary>하네스의 명령 목록(Hello)에 실리는 설명.</summary>
    public string Description { get; init; } = "";
}
