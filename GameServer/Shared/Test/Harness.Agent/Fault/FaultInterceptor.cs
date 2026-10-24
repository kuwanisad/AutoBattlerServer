using Google.Protobuf;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Harness.Agent.Fault;

/// <summary>
/// 경계 장애 규칙(FaultRuleSet)을 이 서버가 받는 모든 gRPC 호출에 적용하는 인터셉터(테스트 전용). 게임 코드에 넣지 않는다 - 하네스 모드에서만
/// 호스팅 시작(HarnessHostingStartup → AddHarnessAgent)이 GrpcServiceOptions에 PostConfigure로 맨 안쪽에 끼운다(서버 자신의 인증
/// 인터셉터 안쪽 - 그래서 인증이 남긴 UserState["playerId"]로 호출자를 안다).
///
/// 모든 호출의 받는 쪽이 우리 서버라, 서버마다 이것 하나로 gRPC 구간이 양방향으로 덮인다(보내는 쪽 채널은 게임 코드가 직접 만들어 끼울 자리가 없음).
/// 규칙이 없으면 메시지를 건드리지 않고 그대로 넘긴다.
/// </summary>
public sealed class FaultInterceptor : Interceptor
{
    private readonly FaultRuleSet _rules;

    public FaultInterceptor(FaultRuleSet rules) => _rules = rules;

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        request = await ApplyOne(request, FaultDirection.Request, context);
        var response = await continuation(request, context);
        return await ApplyOne(response, FaultDirection.Response, context);
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request, IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        request = await ApplyOne(request, FaultDirection.Request, context);
        await continuation(request, new Writer<TResponse>(responseStream, this, context), context);
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        var response = await continuation(new Reader<TRequest>(requestStream, this, context), context);
        return await ApplyOne(response, FaultDirection.Response, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream, ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation) =>
        continuation(new Reader<TRequest>(requestStream, this, context), new Writer<TResponse>(responseStream, this, context), context);

    /// <summary>단일 메시지(단일 요청/응답, 서버 스트림의 요청) - 버리기는 상대가 제한 시간까지 기다리게 한다.</summary>
    private async Task<T> ApplyOne<T>(T message, FaultDirection direction, ServerCallContext context) where T : class
    {
        var (outcome, result) = await Decide(message, direction, context);
        if (outcome != Decision.Drop) return result;

        try
        {
            await Task.Delay(Timeout.Infinite, context.CancellationToken); // 상대가 끊거나 제한 시간이 될 때까지
        }
        catch (OperationCanceledException)
        {
            // 의도한 결과(버린 메시지) - 처리기 예외로 서버 로그에 오류가 찍히지 않게 gRPC 상태로 끝낸다.
        }
        throw new RpcException(new Status(StatusCode.Cancelled, "harness fault - 버린 메시지(상대가 끊었거나 제한 시간)"));
    }

    private enum Decision { Pass, Drop }

    private async Task<(Decision, T)> Decide<T>(T message, FaultDirection direction, ServerCallContext context) where T : class
    {
        if (_rules.IsEmpty || message is not IMessage protobuf) return (Decision.Pass, message);

        var outcome = _rules.Apply(context.Method, direction, PlayerIdOf(context), protobuf);
        if (outcome.Rule is not { } rule) return (Decision.Pass, message);

        switch (rule.Action)
        {
            case FaultActionKind.Rewrite:
                return (Decision.Pass, (T)outcome.Message);
            case FaultActionKind.Delay:
                await Task.Delay(rule.DelayMs, context.CancellationToken);
                return (Decision.Pass, message);
            case FaultActionKind.Fail:
                throw new RpcException(new Status(rule.FailStatus, $"harness fault {rule.Id}"));
            default:
                return (Decision.Drop, message);
        }
    }

    private static string? PlayerIdOf(ServerCallContext context) =>
        context.UserState.TryGetValue("playerId", out var value) ? value?.ToString() : null;

    /// <summary>응답 스트림 감싸기 - 메시지마다 규칙 적용(버리기는 그 메시지만 건너뜀).</summary>
    private sealed class Writer<T> : IServerStreamWriter<T> where T : class
    {
        private readonly IServerStreamWriter<T> _inner;
        private readonly FaultInterceptor _owner;
        private readonly ServerCallContext _context;

        public Writer(IServerStreamWriter<T> inner, FaultInterceptor owner, ServerCallContext context)
        {
            _inner = inner;
            _owner = owner;
            _context = context;
        }

        public WriteOptions? WriteOptions
        {
            get => _inner.WriteOptions;
            set => _inner.WriteOptions = value;
        }

        public async Task WriteAsync(T message)
        {
            var (decision, result) = await _owner.Decide(message, FaultDirection.Response, _context);
            if (decision == Decision.Pass) await _inner.WriteAsync(result);
        }

        // 취소 토큰을 받는 겹도 반드시 그대로 넘긴다 - IAsyncStreamWriter의 기본 구현은 "지원 안 함" 예외를 던진다.
        // 서버 코드가 이 겹을 쓴다(MasterServer ConnectionHandler, LoginConnectionHandler, MatchConnectionHandler 등) - 빠뜨리면
        // 규칙이 없어도 스트림이 깨진다(2026-10-23 PC 실행에서 실제로 깨짐).
        public async Task WriteAsync(T message, CancellationToken cancellationToken)
        {
            var (decision, result) = await _owner.Decide(message, FaultDirection.Response, _context);
            if (decision == Decision.Pass) await _inner.WriteAsync(result, cancellationToken);
        }
    }

    /// <summary>요청 스트림 감싸기 - 메시지마다 규칙 적용(버리기는 그 메시지를 건너뛰고 다음 것을 읽음).</summary>
    private sealed class Reader<T> : IAsyncStreamReader<T> where T : class
    {
        private readonly IAsyncStreamReader<T> _inner;
        private readonly FaultInterceptor _owner;
        private readonly ServerCallContext _context;
        private T? _current;

        public Reader(IAsyncStreamReader<T> inner, FaultInterceptor owner, ServerCallContext context)
        {
            _inner = inner;
            _owner = owner;
            _context = context;
        }

        public T Current => _current ?? throw new InvalidOperationException("MoveNext 전");

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            while (await _inner.MoveNext(cancellationToken))
            {
                var (decision, result) = await _owner.Decide(_inner.Current, FaultDirection.Request, _context);
                if (decision == Decision.Drop) continue;
                _current = result;
                return true;
            }
            return false;
        }
    }
}
