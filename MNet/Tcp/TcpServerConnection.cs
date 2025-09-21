using Serilog;

namespace MNet.Tcp;

public sealed class TcpServerConnection : IAsyncDisposable, ITcpSender {
    private bool _Disposed;

    public required Socket Socket { get; init; }
    public required IDuplexPipe DuplexPipe { get; init; }
    public required TcpServer Server { get; init; }
    public required string UniqueId { get; set; }
    public Stream? Stream { get; init; }
    public bool IsHandshaked { get; set; } = false;
    public CancellationTokenSource DisconnectToken { get; } = new();

    public Channel<ITcpFrame> OutgoingFramesQueue { get; } = Channel.CreateUnbounded<ITcpFrame>();

    public ITcpFramePool FramePool => Server.Options.FramePool;

    /// <summary>
    ///     Store arbitrary data related to this connection
    /// </summary>
    public object? Data { get; set; }

    public async ValueTask DisposeAsync() {
        if (_Disposed) return;

        try {
            if (DuplexPipe is SocketConnection socketConnection) {
                await socketConnection.DisposeAsync(); // does the socket dispose itself 
            }
            else {
                try {
                    Socket?.Shutdown(SocketShutdown.Both);
                }
                catch (Exception ex) {
                    Log.Error(ex, "Socket shutdown failed");
                }

                if (Stream != null) await Stream.DisposeAsync();

                Socket?.Dispose();
            }

            OutgoingFramesQueue.Writer.TryComplete();

            if (!DisconnectToken.IsCancellationRequested) await DisconnectToken.CancelAsync();

            DisconnectToken?.Dispose();
        }
        catch (Exception ex) {
            Log.Error(ex, "Disconnect failed");
        }

        _Disposed = true;
    }

    public void Send(Memory<byte> payload) {
        var frame = FramePool.Get();

        frame.IsRawOnly = true;
        frame.IsSending = true;

        frame.Data = payload;
        OutgoingFramesQueue.Writer.TryWrite(frame);

        FramePool.Return(frame);
    }

    void ITcpSender.Send(ITcpFrame frame) {
        OutgoingFramesQueue.Writer.TryWrite(frame);
    }

    // TODO: Return a boolean if the send was successful or not
    public void Send<T>(uint messageType, T payload) {
        var frame = FramePool.Get();

        frame.MessageType = messageType;
        frame.IsRawOnly = false;
        frame.IsSending = true;

        frame.Data = Server.Options.Serializer.SerializeAsMemory(payload);
        OutgoingFramesQueue.Writer.TryWrite(frame);

        FramePool.Return(frame);
    }

    // TODO: Return a boolean if the send was successful or not
    public void Send(uint messageType, Memory<byte> payload) {
        var frame = FramePool.Get();

        frame.MessageType = messageType;
        frame.IsRawOnly = false;
        frame.IsSending = true;

        frame.Data = payload;
        OutgoingFramesQueue.Writer.TryWrite(frame);

        FramePool.Return(frame);
    }

    public void Disconnect() {
        DisconnectToken.Cancel();
    }
}