namespace AccountServer;

/// <summary>
/// AccountServer 전역 설정값. 지금은 우편함 상한선 하나뿐 - 나중에 늘어나면 그때
/// appsettings.json 같은 정식 설정 체계로 옮길 것.
/// </summary>
public static class AccountServerConfig
{
    /// <summary>플레이어 한 명의 우편함 최대 개수 - 초과 시 GetMailList 요청 시점에
    /// 밀어내기가 발동한다(MailboxEvictor 참고). 테스트 중이라 일부러 작게(2) 잡아둠 -
    /// 밀어내기가 잘 동작하는지 확인 끝나면 30으로 바꿀 것.</summary>
    public static int MailboxCapacity = 2;
}
