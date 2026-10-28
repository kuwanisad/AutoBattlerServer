namespace Harness.Agent;

/// <summary>
/// 서버 쪽 하네스 에이전트(Harness.Agent, &lt;서버&gt;.Harness)의 상수 - 값은 여기 한 곳에 둔다. 하네스·Unity 에이전트와 같아야 하는 값은
/// 여기가 아니라 Harness.Contracts/Shared/HarnessSharedConstants.cs(원본 TestHarness/HarnessShared - 빌드 때 복사됨).
/// </summary>
public static class HarnessAgentConstants
{
    /// <summary>경계 장애 규칙(Fault/).</summary>
    public static class Fault
    {
        /// <summary>fault.hit에 싣는 메시지 미리보기 길이(자).</summary>
        public const int PreviewChars = 400;

        /// <summary>기한이 지난 규칙을 끄는 검사 간격(초).</summary>
        public const int ExpiryCheckSeconds = 1;
    }
}
