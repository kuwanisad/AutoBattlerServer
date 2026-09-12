namespace AccountServer;

/// <summary>
/// AccountServer 전역 설정값. 지금은 우편함 상한선 하나뿐 - 나중에 늘어나면 그때
/// appsettings.json 같은 정식 설정 체계로 옮길 것.
/// </summary>
public static class AccountServerConfig
{
    /// <summary>플레이어 한 명의 우편함 최대 개수 - 초과 시 GetMailList 요청 시점에
    /// 밀어내기가 발동한다(MailboxEvictor 참고).</summary>
    public static int MailboxCapacity = 30;
}