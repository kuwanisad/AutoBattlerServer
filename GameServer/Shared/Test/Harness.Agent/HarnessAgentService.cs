using Harness.Shared;
using System.Threading.Channels;
using Grpc.Core;
using Grpc.Net.Client;
using Harness.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harness.Agent;

public sealed record HarnessAgentOptions(string Address, string Name, string ProcessKind);

/// <summary>
/// 테스트 하네스에 접속해 연결을 유지하면서 명령을 실행하고 이벤트를 올려 보낸다.
/// AddHarnessAgent()가 HARNESS_ADDRESS가 있을 때만 등록한다.
///
/// - 하네스가 아직 안 떴거나 연결이 끊기면 1초마다 다시 접속한다(서버 자체 동작에는 영향 없음).
/// - 하네스가 Hello를 거부하면(버전 불일치, 이름 중복) 재시도하지 않고 오류 로그만 남긴다 -
///   다시 시도해도 같은 결과라서.
/// - 명령은 받는 대로 각자 별도 작업으로 실행한다(느린 명령이 다른 명령을 막지 않게).
/// - 연결이 없는 동안 생긴 이벤트는 최대 10000개까지 모아뒀다가 다시 접속하면 보낸다.
/// - 모든 명령이 같은 경로를 탄다: 실행 → 결과를 송신 큐에 넣음 → (명령이 HarnessCommandContext.AfterReply로
///   등록했으면) 그 결과가 실제로 전송된 뒤 등록한 일을 실행(Unity 쪽 HarnessConnection과 같은 규칙).
/// </summary>
public sealed class HarnessAgentService : BackgroundService, IHarnessEvents
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(HarnessSharedConstants.Connection.RetryDelaySeconds);

    // 결과를 이 시간 안에 못 보내면(연결 없음) 등록된 일을 그냥 실행한다 - 하네스가 없을 때 영영 안 되는 일이 없게.
    private static readonly TimeSpan AfterReplyTimeout = TimeSpan.FromSeconds(HarnessSharedConstants.Connection.AfterReplyTimeoutSeconds);

    private readonly HarnessAgentOptions _options;
    private readonly HarnessCommandRegistry _registry;
    private readonly IServiceProvider _services;
    private readonly ILogger<HarnessAgentService> _logger;

    // 송신 큐(이벤트 + 명령 결과). 연결이 바뀌어도 유지된다. 가득 차면 가장 오래된 것부터 버린다.
    private readonly Channel<Outgoing> _outbound = Channel.CreateBounded<Outgoing>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public HarnessAgentService(
        HarnessAgentOptions options, HarnessCommandRegistry registry,
        IServiceProvider services, ILogger<HarnessAgentService> logger)
    {
        _options = options;
        _registry = registry;
        _services = services;
        _logger = logger;
    }

    public void Emit(string name, string? playerId = null, object? data = null) =>
        _outbound.Writer.TryWrite(new Outgoing(new AgentMessage { Event = HarnessJson.CreateEvent(name, playerId, data) }, null));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("하네스 에이전트 시작: {Address} 이름={Name} 명령={Commands}",
            _options.Address, _options.Name, string.Join(",", _registry.Names.Order()));

        var everConnected = false;
        var failureLogged = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(() => { everConnected = true; failureLogged = false; }, stoppingToken);
                _logger.LogWarning("하네스 연결 끊김 - {Delay}초 뒤 재접속", RetryDelay.TotalSeconds);
            }
            catch (HarnessRejectedException ex)
            {
                _logger.LogError("하네스가 접속을 거부함 - 에이전트 중지: {Reason}", ex.Message);
                return;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // 하네스가 아직 안 떴을 때 1초마다 같은 경고가 쌓이지 않게 한 번만 남긴다.
                if (!failureLogged)
                {
                    _logger.LogWarning("하네스 접속 실패({State}) - {Delay}초마다 재시도: {Error}",
                        everConnected ? "재접속" : "최초", RetryDelay.TotalSeconds, ex.Message);
                    failureLogged = true;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try { await Task.Delay(RetryDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunConnectionAsync(Action onConnected, CancellationToken stoppingToken)
    {
        using var channel = GrpcChannel.ForAddress(_options.Address);
        var client = new HarnessControl.HarnessControlClient(channel);

        using var call = client.Attach(cancellationToken: stoppingToken);

        var hello = new Hello
        {
            ProtocolVersion = (int)ProtocolVersion.Current,
            Name = _options.Name,
            ProcessKind = _options.ProcessKind,
            Pid = Environment.ProcessId,
        };
        hello.Commands.AddRange(_registry.Describe());
        await call.RequestStream.WriteAsync(new AgentMessage { Hello = hello }, stoppingToken);

        if (!await call.ResponseStream.MoveNext(stoppingToken))
        {
            throw new IOException("Welcome을 받기 전에 하네스가 연결을 닫음");
        }

        var welcome = call.ResponseStream.Current.Welcome;
        if (welcome == null || !welcome.Accepted)
        {
            throw new HarnessRejectedException(welcome?.Reason ?? "Welcome 대신 다른 메시지가 옴");
        }

        onConnected();
        _logger.LogInformation("하네스 접속 완료: {Address} 이름={Name}", _options.Address, _options.Name);

        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var sendLoop = SendLoopAsync(call.RequestStream, connectionCts.Token);

        try
        {
            while (await call.ResponseStream.MoveNext(stoppingToken))
            {
                var command = call.ResponseStream.Current.Command;
                if (command == null) continue;

                _ = ExecuteCommandAsync(command, connectionCts.Token);
            }
        }
        catch (RpcException ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogWarning("하네스 스트림 오류: {Status} {Detail}", ex.StatusCode, ex.Status.Detail);
        }
        finally
        {
            connectionCts.Cancel();
            try { await sendLoop; } catch { /* 연결이 이미 끊김 */ }
        }
    }

    private async Task SendLoopAsync(IClientStreamWriter<AgentMessage> requestStream, CancellationToken ct)
    {
        await foreach (var item in _outbound.Reader.ReadAllAsync(ct))
        {
            await requestStream.WriteAsync(item.Message, ct);
            item.Sent?.TrySetResult(); // 결과를 보낸 뒤 할 일(AfterReply)의 실행 조건
        }
    }

    private async Task ExecuteCommandAsync(Command command, CancellationToken ct)
    {
        // 서버 로그에도 남긴다 - 타임라인에서 "이 시점에 하네스가 이걸 시켰다"가 보이게.
        _logger.LogInformation("하네스 명령 실행: {Command} id={Id} args={Args}", command.Name, command.Id, command.Args);

        var afterReply = HarnessCommandContext.Begin();
        CommandResult result;
        try
        {
            result = await _registry.InvokeAsync(command, _services, ct);
        }
        finally
        {
            HarnessCommandContext.End();
        }

        if (result.Ok)
        {
            _logger.LogInformation("하네스 명령 완료: {Command} id={Id}", command.Name, command.Id);
        }
        else
        {
            _logger.LogWarning("하네스 명령 실패: {Command} id={Id} {Error}", command.Name, command.Id, result.Error);
        }

        // 실패한 명령은 아무 일도 하지 않은 것으로 본다 - 등록한 일도 버린다.
        var actions = result.Ok ? afterReply.ToArray() : Array.Empty<Action>();
        if (actions.Length == 0)
        {
            _outbound.Writer.TryWrite(new Outgoing(new AgentMessage { Result = result }, null));
            return;
        }

        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _outbound.Writer.TryWrite(new Outgoing(new AgentMessage { Result = result }, sent));

        if (await Task.WhenAny(sent.Task, Task.Delay(AfterReplyTimeout)) != sent.Task)
        {
            _logger.LogWarning("하네스 명령 {Command} 결과를 {Seconds}초 안에 못 보냄(연결 없음) - 등록된 일을 그대로 실행",
                command.Name, AfterReplyTimeout.TotalSeconds);
        }

        foreach (var action in actions)
        {
            try { action(); }
            catch (Exception ex) { _logger.LogError(ex, "하네스 명령 {Command}의 결과 전송 후 작업 실패", command.Name); }
        }
    }

    /// <summary>송신 큐의 메시지 하나. Sent가 있으면 보내기 루프가 전송 후 완료시킨다(AfterReply 대기용).</summary>
    private sealed record Outgoing(AgentMessage Message, TaskCompletionSource? Sent);

    private sealed class HarnessRejectedException : Exception
    {
        public HarnessRejectedException(string reason) : base(reason)
        {
        }
    }
}
