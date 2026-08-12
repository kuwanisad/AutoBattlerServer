using CleanupServerContracts;
using Grpc.Core;
using RegistryCleanupServer.Services;

namespace RegistryCleanupServer.Services;

/// <summary>
/// 메인 서버(로비, 게임 에이전트 등)가 직접 호출하는 gRPC 서비스.
/// 관리 대상 등록/해제/소유권 이전/정상 종료를 처리한다.
/// </summary>
public sealed class MainServerCleanupService : MainServerCleanupConnection.MainServerCleanupConnectionBase
{
    private readonly OwnershipRegistry _registry;
    private readonly ILogger<MainServerCleanupService> _logger;

    public MainServerCleanupService(OwnershipRegistry registry, ILogger<MainServerCleanupService> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public override Task<RegisterResponse> Register(RegisterRequest request, ServerCallContext context)
    {
        var objectId = GetObjectId(request);
        if (objectId == null)
        {
            return Task.FromResult(new RegisterResponse
            {
                Success = false,
                ErrorMessage = "알 수 없는 객체 타입입니다.",
            });
        }

        _registry.Register(request.ServerId, objectId);
        _logger.LogInformation("등록: {ServerId} → {ObjectId}", request.ServerId, objectId);

        return Task.FromResult(new RegisterResponse { Success = true });
    }

    public override Task<UnregisterResponse> Unregister(UnregisterRequest request, ServerCallContext context)
    {
        var objectId = GetObjectId(request);
        if (objectId == null)
        {
            return Task.FromResult(new UnregisterResponse
            {
                Success = false,
                ErrorMessage = "알 수 없는 객체 타입입니다.",
            });
        }

        _registry.Unregister(request.ServerId, objectId);
        _logger.LogInformation("해제: {ServerId} → {ObjectId}", request.ServerId, objectId);

        return Task.FromResult(new UnregisterResponse { Success = true });
    }

    public override Task<TransferOwnershipResponse> TransferOwnership(
        TransferOwnershipRequest request, ServerCallContext context)
    {
        var success = _registry.TransferOwnership(
            request.FromServerId,
            request.ToServerId,
            request.ObjectIds);

        if (!success)
        {
            return Task.FromResult(new TransferOwnershipResponse
            {
                Success = false,
                ErrorMessage = $"소유권 이전 거부: {request.ToServerId}는 이미 죽은 서버입니다.",
            });
        }

        return Task.FromResult(new TransferOwnershipResponse { Success = true });
    }

    public override Task<ShutdownNoticeResponse> NotifyShutdown(
        ShutdownNoticeRequest request, ServerCallContext context)
    {
        var count = _registry.Shutdown(request.ServerId);
        _logger.LogInformation("정상 종료: {ServerId} ({Count}개 해제)", request.ServerId, count);

        return Task.FromResult(new ShutdownNoticeResponse
        {
            Success = true,
            RemovedCount = count,
        });
    }

    // === 헬퍼 ===

    private static string? GetObjectId(RegisterRequest request) =>
        request.ObjectCase switch
        {
            RegisterRequest.ObjectOneofCase.Player => request.Player.PlayerId,
            _ => null,
        };

    private static string? GetObjectId(UnregisterRequest request) =>
        request.ObjectCase switch
        {
            UnregisterRequest.ObjectOneofCase.Player => request.Player.PlayerId,
            _ => null,
        };
}
