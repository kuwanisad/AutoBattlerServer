using ChatContracts;
using System.Net.WebSockets;
using Google.Protobuf;
using MainServer.Common.MasterConnection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Shared.Common.Auth;
using Shared.Common.Redis;

namespace ChatServer;

/// <summary>
/// 클라이언트 WebSocket 연결을 처리한다. Protobuf 바이너리로 송수신한다.
///
/// 인증: 핸드셰이크 시점에 playerId+token을 쿼리로 받아 딱 한 번만 검증한다
/// (TokenVerification.IsValid - 세션 레지스트리를 읽기 전용으로만 씀). 채팅 서버는
/// 절대 토큰을 회전시키지 않는다 - "이 클라를 계속 서비스해도 되는지"는 전적으로
/// 메인 서버(로비/게임 에이전트)의 판단 영역이고, 채팅 서버는 그 판단을 그대로
/// 따른다(회전 자체가 "내가 새 주인이다"라는 선언인데, 채팅 서버는 주인이 될 자격이
/// 없다). 연결이 살아있는 동안은 재검증하지 않고, 재연결마다 새로 검증한다.
///
/// 메인 서버가 세션을 지우면(SessionInvalidatedSubscriber) 채팅 서버가 스스로 판단하지
/// 않고 그 신호로만 연결을 끊는다 - 같은 원칙의 반대편.
/// </summary>
public sealed class WebSocketHandler
{
    private readonly ISessionRegistry _sessionRegistry;
    private readonly IChatRoomStore _roomStore;
    private readonly IChatMessageStore _messageStore;
    private readonly IChatFilter _chatFilter;
    private readonly ChatConnectionRegistry _connections;
    private readonly MasterConnectionService _masterConnection;
    private readonly ILogger<WebSocketHandler> _logger;

    public WebSocketHandler(
        ISessionRegistry sessionRegistry,
        IChatRoomStore roomStore,
        IChatMessageStore messageStore,
        IChatFilter chatFilter,
        ChatConnectionRegistry connections,
        MasterConnectionService masterConnection,
        ILogger<WebSocketHandler> logger)
    {
        _sessionRegistry = sessionRegistry;
        _roomStore = roomStore;
        _messageStore = messageStore;
        _chatFilter = chatFilter;
        _connections = connections;
        _masterConnection = masterConnection;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        var playerId = context.Request.Query["playerId"].ToString();
        var token = context.Request.Query["token"].ToString();

        if (string.IsNullOrEmpty(playerId) || string.IsNullOrEmpty(token))
        {
            context.Response.StatusCode = 400;
            return;
        }

        var session = await _sessionRegistry.GetAsync(playerId);
        if (!TokenVerification.IsValid(session, token))
        {
            _logger.LogWarning("채팅 접속 거부 - 토큰 불일치: {PlayerId}", playerId);
            context.Response.StatusCode = 401;
            return;
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();

        var connection = new ChatConnection { PlayerId = playerId, WebSocket = webSocket };
        _connections.Register(connection);
        _logger.LogInformation("채팅 서버 접속: {PlayerId}", playerId);

        try
        {
            await ReceiveLoopAsync(connection);
        }
        finally
        {
            _connections.Unregister(playerId);
            _logger.LogInformation("채팅 서버 퇴장: {PlayerId}", playerId);
        }
    }

    private async Task ReceiveLoopAsync(ChatConnection connection)
    {
        var buffer = new byte[4096];

        try
        {
            while (connection.WebSocket.State == WebSocketState.Open)
            {
                var result = await connection.WebSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await connection.WebSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure, "정상 종료", CancellationToken.None);
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Binary) continue;

                var message = ClientChatMessage.Parser.ParseFrom(buffer, 0, result.Count);
                await HandleMessageAsync(connection, message);
            }
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug("WebSocket 연결 종료: {PlayerId} - {Message}", connection.PlayerId, ex.Message);
        }
    }

    private async Task HandleMessageAsync(ChatConnection connection, ClientChatMessage message)
    {
        switch (message.PayloadCase)
        {
            case ClientChatMessage.PayloadOneofCase.Join:
                await HandleJoinAsync(connection, message.Join.ChannelId);
                break;

            case ClientChatMessage.PayloadOneofCase.Leave:
                // 이제 방 멤버십은 영속 저장소가 판단하고, Join/Leave는 실질적인 접근
                // 제어에 관여하지 않는다(그건 IChatRoomStore가 담당) - 그냥 확인 응답만.
                await SendAsync(connection, new ServerChatMessage
                {
                    LeaveAck = new LeaveChannelAck { ChannelId = message.Leave.ChannelId, Success = true },
                });
                break;

            case ClientChatMessage.PayloadOneofCase.Send:
                await HandleSendAsync(connection, message.Send);
                break;

            case ClientChatMessage.PayloadOneofCase.RequestChat:
                await HandleRequestChatAsync(connection, message.RequestChat);
                break;

            case ClientChatMessage.PayloadOneofCase.GetMyRooms:
                await HandleGetMyRoomsAsync(connection);
                break;

            case ClientChatMessage.PayloadOneofCase.GetHistory:
                await HandleGetHistoryAsync(connection, message.GetHistory);
                break;

            default:
                _logger.LogWarning("알 수 없는 메시지 타입: {PlayerId}", connection.PlayerId);
                break;
        }
    }

    private async Task HandleJoinAsync(ChatConnection connection, string channelId)
    {
        var other = await _roomStore.GetOtherMemberAsync(channelId, connection.PlayerId);
        var success = other != null;

        await SendAsync(connection, new ServerChatMessage
        {
            JoinAck = new JoinChannelAck { ChannelId = channelId, Success = success },
        });
    }

    private async Task HandleSendAsync(ChatConnection connection, SendMessageRequest request)
    {
        var other = await _roomStore.GetOtherMemberAsync(request.ChannelId, connection.PlayerId);
        if (other == null)
        {
            await SendAsync(connection, new ServerChatMessage
            {
                Error = new ChatError { Code = "NOT_A_MEMBER", Message = "이 방의 멤버가 아닙니다." },
            });
            return;
        }

        var filterResult = await _chatFilter.FilterAsync(connection.PlayerId, request.Content);
        if (!filterResult.Allowed)
        {
            await SendAsync(connection, new ServerChatMessage
            {
                Error = new ChatError { Code = "BLOCKED", Message = filterResult.Reason ?? "차단된 메시지" },
            });
            return;
        }

        var data = new ChatMessageData
        {
            MessageId = Guid.NewGuid().ToString("N"),
            ChannelId = request.ChannelId,
            SenderId = connection.PlayerId,
            Content = filterResult.FilteredContent ?? request.Content,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        await _messageStore.SaveAsync(data);

        var delivery = new ServerChatMessage
        {
            Message = new ChatMessageDelivery
            {
                MessageId = data.MessageId,
                ChannelId = data.ChannelId,
                SenderId = data.SenderId,
                Content = data.Content,
                TimestampUnixMs = data.TimestampUnixMs,
            },
        };

        // 1:1 방이라 "브로드캐스트"가 아니라 그냥 상대방한테 보낸다(온라인이면).
        var recipient = _connections.TryGet(other);
        if (recipient != null)
        {
            await SendAsync(recipient, delivery);
        }

        // 보낸 사람 화면에도 그대로 반영(에코) - 여러 기기/창에서 봐도 일관되게.
        await SendAsync(connection, delivery);
    }

    private async Task HandleRequestChatAsync(ChatConnection connection, RequestChatRequest request)
    {
        var friendPlayerId = request.FriendPlayerId;

        // 채팅 서버가 내부적으로(읽기 전용) 진짜 친구 사이인지 확인한다 - 클라한테 이
        // 기능을 노출하는 게 아니라 자기 방 생성을 검증하려고 남의 데이터를 읽어오는
        // 것뿐이라 "친구 관리는 메인 서버 창구"라는 원칙과 안 어긋난다.
        var isFriend = await IsFriendAsync(connection.PlayerId, friendPlayerId);
        if (!isFriend)
        {
            await SendAsync(connection, new ServerChatMessage
            {
                RequestChatAck = new RequestChatAck { Success = false, ErrorMessage = "친구가 아닙니다." },
            });
            return;
        }

        var channelId = IChatRoomStore.ComputeChannelId(connection.PlayerId, friendPlayerId);
        await _roomStore.EnsureRoomAsync(channelId, connection.PlayerId, friendPlayerId);

        await SendAsync(connection, new ServerChatMessage
        {
            RequestChatAck = new RequestChatAck { Success = true, ChannelId = channelId },
        });

        // 상대방이 지금 온라인이면 실시간으로 알려준다. 오프라인이면 다음 접속 시
        // GetMyRooms로 자연스럽게 보인다 - 별도 오프라인 알림 큐는 필요 없다.
        var friendConnection = _connections.TryGet(friendPlayerId);
        if (friendConnection != null)
        {
            await SendAsync(friendConnection, new ServerChatMessage
            {
                RoomInvited = new RoomInvited { ChannelId = channelId, FriendPlayerId = connection.PlayerId },
            });
        }
    }

    private async Task HandleGetMyRoomsAsync(ChatConnection connection)
    {
        var rooms = await _roomStore.GetRoomsForPlayerAsync(connection.PlayerId);

        var response = new MyRoomsResponse();
        foreach (var room in rooms)
        {
            response.Rooms.Add(new ChatRoomInfo
            {
                ChannelId = room.ChannelId,
                FriendPlayerId = room.FriendPlayerId,
            });
        }

        await SendAsync(connection, new ServerChatMessage { MyRooms = response });
    }

    private async Task HandleGetHistoryAsync(ChatConnection connection, GetHistoryRequest request)
    {
        var other = await _roomStore.GetOtherMemberAsync(request.ChannelId, connection.PlayerId);
        if (other == null)
        {
            await SendAsync(connection, new ServerChatMessage
            {
                Error = new ChatError { Code = "NOT_A_MEMBER", Message = "이 방의 멤버가 아닙니다." },
            });
            return;
        }

        var count = request.Count > 0 ? request.Count : 50;
        var messages = request.BeforeTimestampUnixMs > 0
            ? await _messageStore.GetBeforeAsync(request.ChannelId, request.BeforeTimestampUnixMs, count)
            : await _messageStore.GetRecentAsync(request.ChannelId, count);

        var response = new HistoryResponse { ChannelId = request.ChannelId };
        foreach (var m in messages)
        {
            response.Messages.Add(new ChatMessageDelivery
            {
                MessageId = m.MessageId,
                ChannelId = m.ChannelId,
                SenderId = m.SenderId,
                Content = m.Content,
                TimestampUnixMs = m.TimestampUnixMs,
            });
        }

        await SendAsync(connection, new ServerChatMessage { History = response });
    }

    /// <summary>AccountServer.GetFriends를 직접(내부적으로, 읽기 전용) 호출해서 친구인지 확인한다.</summary>
    private async Task<bool> IsFriendAsync(string playerId, string friendPlayerId)
    {
        if (string.IsNullOrEmpty(_masterConnection.AccountServerAddress))
        {
            _logger.LogWarning("계정 서버 주소 없음 - 친구 확인 불가: {PlayerId}", playerId);
            return false;
        }

        try
        {
            var handler = new System.Net.Http.SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                KeepAlivePingDelay = TimeSpan.FromSeconds(10),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
                KeepAlivePingPolicy = System.Net.Http.HttpKeepAlivePingPolicy.WithActiveRequests,
            };

            using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(
                _masterConnection.AccountServerAddress,
                new Grpc.Net.Client.GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });

            var client = new AccountServer.AccountControl.AccountControlClient(channel);
            var response = await client.GetFriendsAsync(new AccountServer.GetFriendsRequest { PlayerId = playerId });

            return response.FriendPlayerIds.Contains(friendPlayerId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "친구 확인 실패: {PlayerId} ↔ {FriendPlayerId}", playerId, friendPlayerId);
            return false;
        }
    }

    private static async Task SendAsync(ChatConnection connection, ServerChatMessage message)
    {
        if (connection.WebSocket.State != WebSocketState.Open) return;

        var bytes = message.ToByteArray();
        await connection.WebSocket.SendAsync(
            new ArraySegment<byte>(bytes), WebSocketMessageType.Binary,
            endOfMessage: true, CancellationToken.None);
    }
}