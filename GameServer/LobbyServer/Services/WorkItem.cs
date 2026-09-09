namespace LobbyServer.Services;

public enum WorkItemType
{
    Announcement,
}

/// <summary>클라 대상 작업 하나 - 타입뿐 아니라 내용(Payload)까지 같이 담아야, 같은
/// 타입의 서로 다른 일감(예: 공지 A, 공지 B가 동시에 진행 중인 경우)을 HashSet 안에서
/// 서로 다른 값으로 구분할 수 있다. record struct라 값 기반 동등성 비교 - 완전히
/// 같은 내용의 작업만 자동으로 중복 제거되는 건 의도된 동작.
///
/// Payload는 int - 공지는 자유 문자열 대신 AnnouncementType(enum, 정수) 하나로
/// 퉁긴다(패킷 양 절감). enum이 아닌 다른 종류의 페이로드가 필요해지면 그때 다시
/// 설계할 것.</summary>
public readonly record struct WorkItem(WorkItemType Type, int Payload);
