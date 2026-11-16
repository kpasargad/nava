using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Nava.Services;

namespace Nava.Tests;

/// <summary>In-memory transport races; never opens a socket or sends audio anywhere.</summary>
public static class RealtimeRaceRegression
{
    public static async Task DisposeCancelsActiveSendAndWaitersAsync()
    {
        var socket=new FixtureSocket(blockSends:true);
        var session=new RealtimeSession(socket,(_,_)=> { },null);
        Task sending=session.SendAudioAsync(new byte[3200],CancellationToken.None);
        await socket.SendEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task waiting=session.SendAudioAsync(new byte[3200],CancellationToken.None);
        await Task.WhenAll(session.DisposeAsync().AsTask(),session.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(3));
        await ExpectCancellationAsync(sending); await ExpectCancellationAsync(waiting);
        if(socket.DisposeCount!=1) throw new InvalidOperationException("Concurrent disposal released the transport more than once");
    }

    public static async Task DisposeCancelsPacingDelayAsync()
    {
        var socket=new FixtureSocket(blockSends:false);
        var session=new RealtimeSession(socket,(_,_)=> { },null);
        await session.SendAudioAsync(new byte[32000],CancellationToken.None);
        // The next frame is pacing behind one second of audio, not waiting on I/O.
        Task paced=session.SendAudioAsync(new byte[3200],CancellationToken.None);
        await session.DisposeAsync();
        await ExpectCancellationAsync(paced);
        if(socket.SendCount!=1) throw new InvalidOperationException("Disposed session sent a queued audio frame");
    }

    private static async Task ExpectCancellationAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch(OperationCanceledException) { return; }
        throw new InvalidOperationException("Disposal did not cancel the pending operation");
    }

    private sealed class FixtureSocket : WebSocket
    {
        private readonly bool blockSends;
        private volatile WebSocketState state=WebSocketState.Open;
        private int disposeCount,sendCount;
        public TaskCompletionSource SendEntered { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount=>Volatile.Read(ref disposeCount);
        public int SendCount=>Volatile.Read(ref sendCount);
        public override WebSocketCloseStatus? CloseStatus=>null;
        public override string? CloseStatusDescription=>null;
        public override string? SubProtocol=>null;
        public override WebSocketState State=>state;
        public FixtureSocket(bool blockSends)=>this.blockSends=blockSends;
        public override void Abort()=>state=WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus,string? statusDescription,CancellationToken cancellationToken) { state=WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus,string? statusDescription,CancellationToken cancellationToken)=>CloseAsync(closeStatus,statusDescription,cancellationToken);
        public override void Dispose() { Interlocked.Increment(ref disposeCount); state=WebSocketState.Closed; }
        public override Task SendAsync(ArraySegment<byte> buffer,WebSocketMessageType messageType,bool endOfMessage,CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref sendCount); SendEntered.TrySetResult();
            return blockSends?Task.Delay(Timeout.Infinite,cancellationToken):Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite,cancellationToken);
            return new WebSocketReceiveResult(0,WebSocketMessageType.Close,true);
        }
    }
}
