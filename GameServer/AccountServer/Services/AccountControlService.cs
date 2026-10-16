using GameServer.Common;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Shared.Common.Auth;
using Shared.Common.Messaging;

namespace AccountServer.Services;

/// <summary>
/// 로그인 서버가 호출하는 gRPC 서비스. 계정 검증/생성만 담당하는 순수 조회/생성 저장소라
/// 매칭 서버처럼 접속 유지 스트림을 쓸 필요가 없다 - 단항 RPC로 충분하다.
/// </summary>
public sealed class AccountControlService : AccountControl.AccountControlBase
{
    // playerId(GUID) 충돌 시 재발급 최대 횟수. 122비트 무작위값이라 실질적으로 거의
    // 발생하지 않지만(사실상 0에 가까운 확률), 비용이 거의 안 드는 선에서 방어해둔다.
    private const int MaxPlayerIdRetries = 5;

    private readonly IAccountRepository _repository;
    private readonly RabbitMqBroker _rabbitMq;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IMailRepository _mailRepository;
    private readonly MailboxEvictor _mailboxEvictor;
    private readonly ILogger<AccountControlService> _logger;

    public AccountControlService(
        IAccountRepository repository,
        RabbitMqBroker rabbitMq,
        IPasswordHasher passwordHasher,
        IMailRepository mailRepository,
        MailboxEvictor mailboxEvictor,
        ILogger<AccountControlService> logger)
    {
        _repository = repository;
        _rabbitMq = rabbitMq;
        _passwordHasher = passwordHasher;
        _mailRepository = mailRepository;
        _mailboxEvictor = mailboxEvictor;
        _logger = logger;
    }

    public override async Task<VerifyAccountResponse> VerifyAccount(
        VerifyAccountRequest request, ServerCallContext context)
    {
        var record = await _repository.FindAsync(request.AccountId);

        // record가 없어도 Verify를 굳이 안 부르고 바로 false로 처리 - 어차피 대조할
        // 해시 자체가 없다. (참고: "계정이 있는지 없는지"를 응답 시간 차이로 추론하는
        // 타이밍 공격까지는 지금 범위에서 방어 안 함 - 필요해지면 더미 해시 대조를
        // 추가하는 식으로 시간을 맞출 수 있음.)
        if (record == null || !_passwordHasher.Verify(request.Password, record.PasswordHash))
        {
            _logger.LogInformation("계정 검증 실패: {AccountId}", request.AccountId);
            return new VerifyAccountResponse { Found = false };
        }

        _logger.LogInformation("계정 검증 성공: {AccountId} → {PlayerId}", request.AccountId, record.PlayerId);
        return new VerifyAccountResponse { Found = true, PlayerId = record.PlayerId };
    }

    public override async Task<CreateAccountResponse> CreateAccount(
        CreateAccountRequest request, ServerCallContext context)
    {
        if (string.IsNullOrEmpty(request.AccountId) || string.IsNullOrEmpty(request.Password))
        {
            return new CreateAccountResponse { Success = false, ErrorMessage = "계정 ID와 비밀번호를 입력하세요." };
        }

        if (string.IsNullOrEmpty(request.Nickname))
        {
            return new CreateAccountResponse { Success = false, ErrorMessage = "닉네임을 입력하세요." };
        }

        if (await _repository.NicknameExistsAsync(request.Nickname))
        {
            _logger.LogWarning("계정 생성 실패 - 이미 존재하는 닉네임: {Nickname}", request.Nickname);
            return new CreateAccountResponse { Success = false, ErrorMessage = "이미 사용 중인 닉네임입니다." };
        }

        var playerId = await GenerateUniquePlayerIdAsync();
        if (playerId == null)
        {
            // 이 분기는 사실상 도달 불가능하다(GUID 충돌 확률이 사실상 0) - 그래도 무한
            // 루프 없이 명시적으로 실패 처리한다.
            _logger.LogError("playerId 발급 실패 - {Retries}회 연속 충돌", MaxPlayerIdRetries);
            return new CreateAccountResponse { Success = false, ErrorMessage = "일시적인 오류입니다. 다시 시도하세요." };
        }

        var record = new AccountRecord
        {
            AccountId = request.AccountId,
            PasswordHash = _passwordHasher.Hash(request.Password), // 원문은 여기서 끝, 어디에도 저장 안 함
            PlayerId = playerId,
            Nickname = request.Nickname, // 계정 생성 시에만 입력 - 이후 변경 경로 없음
            CreatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        var created = await _repository.TryCreateAsync(record);
        if (!created)
        {
            _logger.LogWarning("계정 생성 실패 - 이미 존재하는 계정: {AccountId}", request.AccountId);
            return new CreateAccountResponse { Success = false, ErrorMessage = "이미 존재하는 계정입니다." };
        }

        _logger.LogInformation("계정 생성 성공: {AccountId} → {PlayerId}", request.AccountId, playerId);
        return new CreateAccountResponse { Success = true, PlayerId = playerId };
    }

    private async Task<string?> GenerateUniquePlayerIdAsync()
    {
        for (var i = 0; i < MaxPlayerIdRetries; i++)
        {
            var candidate = Guid.NewGuid().ToString("N");
            if (!await _repository.PlayerIdExistsAsync(candidate))
            {
                return candidate;
            }

            _logger.LogWarning("playerId 충돌(극히 드문 경우) - 재발급 시도 {Attempt}/{Max}", i + 1, MaxPlayerIdRetries);
        }

        return null;
    }

    public override async Task<GetFriendsResponse> GetFriends(
        GetFriendsRequest request, ServerCallContext context)
    {
        var friends = await _repository.GetFriendsAsync(request.PlayerId);
        var response = new GetFriendsResponse();
        response.FriendPlayerIds.AddRange(friends);
        return response;
    }

    public override async Task<GetNicknamesResponse> GetNicknames(
        GetNicknamesRequest request, ServerCallContext context)
    {
        var nicknames = await _repository.GetNicknamesAsync(request.PlayerIds);
        var response = new GetNicknamesResponse();

        // MapField<TKey,TValue>.Add(IDictionary<TKey,TValue>)는 IReadOnlyDictionary를
        // 안 받는다 - 직접 하나씩 채운다.
        foreach (var kvp in nicknames)
        {
            response.Nicknames[kvp.Key] = kvp.Value;
        }

        return response;
    }

    public override async Task<AddFriendResponse> AddFriend(
        AddFriendRequest request, ServerCallContext context)
    {
        var success = await _repository.AddFriendAsync(request.PlayerId, request.FriendPlayerId);
        if (!success)
        {
            return new AddFriendResponse { Result = FriendResult.AccountNotFound };
        }

        _logger.LogInformation("친구 추가: {PlayerId} ↔ {FriendPlayerId}", request.PlayerId, request.FriendPlayerId);
        return new AddFriendResponse { Result = FriendResult.Ok };
    }

    public override async Task<RemoveFriendResponse> RemoveFriend(
        RemoveFriendRequest request, ServerCallContext context)
    {
        var success = await _repository.RemoveFriendAsync(request.PlayerId, request.FriendPlayerId);
        if (!success)
        {
            return new RemoveFriendResponse { Result = FriendResult.AccountNotFound };
        }

        // 친구 목록 갱신(위 RemoveFriendAsync)과 이벤트 발행 요청을 최대한 붙여서 처리한다 -
        // PublishToQueue는 RabbitMqBroker의 버퍼에 넣기만 하는 순수 인메모리 연산이라, 그 사이 프로세스가
        // 죽을 위험 구간이 극히 짧다(트랜잭션 아웃박스 패턴). 실제 발행(네트워크 I/O)과 "브로커 확인을
        // 받아야만 버퍼에서 뺀다"는 RabbitMqBroker가 맡는다 - 예전 FriendEventOutbox/FriendEventPublisher를 대체.
        _rabbitMq.PublishToQueue(FriendRemovedEvent.QueueName, System.Text.Json.JsonSerializer.Serialize(new FriendRemovedEvent
        {
            PlayerId = request.PlayerId,
            FriendPlayerId = request.FriendPlayerId,
        }));

        _logger.LogInformation("친구 삭제: {PlayerId} ↔ {FriendPlayerId}", request.PlayerId, request.FriendPlayerId);
        return new RemoveFriendResponse { Result = FriendResult.Ok };
    }

    // === 우편함 ===

    public override async Task<GrantMailResponse> GrantMail(GrantMailRequest request, ServerCallContext context)
    {
        // 멱등성 - 같은 원본 이벤트로 이미 지급했으면 새로 안 만들고 그 mailId를 그대로 반환.
        if (!string.IsNullOrEmpty(request.SourceEventId))
        {
            var existingMailId = await _mailRepository.FindExistingBySourceEventIdAsync(
                request.PlayerId, request.SourceEventId);
            if (existingMailId != null)
            {
                _logger.LogInformation("우편 지급 - 이미 처리된 이벤트(멱등): {PlayerId} SourceEventId={SourceEventId}",
                    request.PlayerId, request.SourceEventId);
                return new GrantMailResponse { Success = true, MailId = existingMailId };
            }
        }

        var record = new MailRecord
        {
            PlayerId = request.PlayerId,
            SourceEventId = request.SourceEventId,
            MailType = request.MailType,
            DisplayContent = request.DisplayContent,
            PayloadJson = request.PayloadJson,
            CreatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ExpiresAtUnixMs = request.ExpiresAtUnixMs,
        };

        var mailId = await _mailRepository.CreateAsync(record);

        _logger.LogInformation("우편 지급: {PlayerId} MailType={MailType} MailId={MailId}",
            request.PlayerId, request.MailType, mailId);

        return new GrantMailResponse { Success = true, MailId = mailId };
    }

    public override async Task<GetMailListResponse> GetMailList(GetMailListRequest request, ServerCallContext context)
    {
        // 클라가 우편함을 조회하는 이 시점에 상한선 초과 여부를 확인해서 정리한다
        // (CreateAsync 시점이 아니라 - MailboxEvictor 주석 참고).
        await _mailboxEvictor.EvictIfOverCapacityAsync(request.PlayerId);

        var mails = await _mailRepository.GetForPlayerAsync(request.PlayerId);

        var response = new GetMailListResponse();
        foreach (var m in mails)
        {
            response.Mails.Add(new MailSummary
            {
                MailId = m.MailId,
                MailType = m.MailType,
                DisplayContent = m.DisplayContent,
                CreatedAtUnixMs = m.CreatedAtUnixMs,
                ExpiresAtUnixMs = m.ExpiresAtUnixMs,
                Claimed = m.Claimed,
            });
        }
        return response;
    }

    public override async Task<OpenMailResponse> OpenMail(OpenMailRequest request, ServerCallContext context)
    {
        // 실제 성패 판정은 TryClaimAsync 하나가 원자적으로 끝낸다 - 그 앞뒤로 별도 조회를
        // 끼워넣지 않는다(그 사이 다른 요청이 끼어드는 race를 만들지 않기 위함).
        var claimed = await _mailRepository.TryClaimAsync(request.PlayerId, request.MailId);

        if (!claimed)
        {
            // 실패했다는 것만 원자적으로 확정됐고, 정확한 사유는 여기서 최선을 다해
            // 재조회로 추정한다 - 그 사이 상태가 또 바뀌었을 수 있어 100% 정확하진
            // 않지만, 클라한테 "없는 메일입니다" 같은 뭉뚱그린 에러보다는 훨씬 낫다.
            var record = await _mailRepository.FindAsync(request.PlayerId, request.MailId);
            var reason = record switch
            {
                null => "NOT_FOUND",
                { Claimed: true } => "ALREADY_CLAIMED",
                _ when record.ExpiresAtUnixMs != 0 &&
                       record.ExpiresAtUnixMs < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() => "EXPIRED",
                _ => "UNKNOWN",
            };

            _logger.LogInformation("우편 수령 실패: {PlayerId} MailId={MailId} Reason={Reason}",
                request.PlayerId, request.MailId, reason);
            return new OpenMailResponse { Success = false, ErrorMessage = reason };
        }

        var mail = await _mailRepository.FindAsync(request.PlayerId, request.MailId);
        _logger.LogInformation("우편 수령 성공: {PlayerId} MailId={MailId}", request.PlayerId, request.MailId);

        return new OpenMailResponse { Success = true, PayloadJson = mail?.PayloadJson ?? string.Empty };
    }
}
