using Serilog;

namespace MNet.Tcp;

public class TcpClient : TcpBase, IAsyncDisposable, ITcpSender {
    public delegate void ConnectHandler();

    public delegate void DisconnectHandler();

    public TcpClient(TcpClientOptions options) {
        if (options.IsSecure) {
            ArgumentNullException.ThrowIfNull(options.Host);
        }

        if (!IPAddress.TryParse(options.Address, out var address)) {
            throw new ArgumentException($"{nameof(options.Address)} is not a valid IP address.");
        }

        Options = options;
        Logger = Options.Logger;

        EndPoint = new IPEndPoint(address, options.Port);
        EventEmitter = new EventEmitter(Options.Serializer);

        InitFactory();
    }

    public TcpClientOptions Options { get; }

    private bool IsHandshaked { get; set; }
    private Channel<ITcpFrame> OutgoingFramesQueue { get; set; } = Channel.CreateUnbounded<ITcpFrame>();
    private IDuplexPipe? DuplexPipe { get; set; }
    private Stream? Stream { get; set; }

    public ITcpFramePool FramePool => Options.FramePool;

    public async ValueTask DisposeAsync() {
        ConnectionFactory?.Dispose();

        await Disconnect();
    }

    public void Send<T>(uint messageType, T payload) where T : class {
        Log.Information("Sending message of type {MessageType} with payload {Payload}", messageType, payload);
        var frame = FramePool.Get();

        frame.MessageType = messageType;
        frame.IsRawOnly = false;
        frame.IsSending = true;

        frame.Data = Options.Serializer.SerializeAsMemory(payload);
        OutgoingFramesQueue.Writer.TryWrite(frame);

        FramePool.Return(frame);
    }

    public void Send(uint messageType, Memory<byte> payload) {
        Log.Information("Sending message of type {MessageType} with payload size {PayloadSize}", messageType,
            payload.Length);
        var frame = FramePool.Get();

        frame.MessageType = messageType;
        frame.IsRawOnly = false;
        frame.IsSending = true;

        frame.Data = payload;
        OutgoingFramesQueue.Writer.TryWrite(frame);

        FramePool.Return(frame);
    }

    /// <summary>
    ///     Only use for handshaking
    /// </summary>
    /// <param name="payload"></param>
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

    public event ConnectHandler? OnConnect;
    public event DisconnectHandler? OnDisconnect;

    public void Connect() {
        if (RunTokenSource != null) {
            return;
        }

        Logger.LogDebug("{Source} Connecting the tcp client...", this);

        IsHandshaked = false;
        OutgoingFramesQueue = Channel.CreateUnbounded<ITcpFrame>();

        RunTokenSource = new CancellationTokenSource();
        var _ = DoConnect(RunTokenSource.Token);
    }

    public async Task Disconnect() {
        if (RunTokenSource == null) {
            return;
        }

        Logger.LogDebug("{Source} Disconnecting the tcp client...", this);

        IsHandshaked = false;

        try {
            if (RunTokenSource != null) {
                await RunTokenSource.CancelAsync();
            }

            RunTokenSource?.Dispose();
        } catch (Exception ex) {
            Log.Error(ex, "Run token cancel failed");
        }

        try {
            if (OutgoingFramesQueue.Writer.TryComplete()) { }
        } catch (Exception ex) {
            Log.Error(ex, "Outgoing frames queue failed");
        }

        RunTokenSource = null;

        if (DuplexPipe != null && DuplexPipe is SocketConnection conn) {
            await conn.DisposeAsync();
        } else {
            try {
                Socket?.Shutdown(SocketShutdown.Both);
            } catch (Exception ex) {
                Log.Error(ex, "Socket shutdown failed");
            }

            if (Stream != null) {
                await Stream.DisposeAsync();
            }

            Socket?.Dispose();
        }

        OnDisconnect?.Invoke();
        Logger.LogInformation("{Source} Tcp client disconnected. {Endpoint}", this, EndPoint);
    }

    private async Task InternalDisconnect() {
        await Disconnect();
        await Task.Delay(Options.ReconnectInterval);

        Connect();
    }

    public void On<T>(uint messageType, EventDelegate<T> handler) {
        InternalOn<T>(messageType, handler);
    }

    public void On<T>(uint messageType, EventDelegateAsync<T> handler) {
        InternalOn<T>(messageType, handler);
    }

    private void InternalOn<T>(uint messageType, Delegate handler) {
        EventEmitter.On<T>(messageType, handler);
    }

    private async Task DoConnect(CancellationToken token) {
        while (!token.IsCancellationRequested) {
            try {
                Socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                Socket.NoDelay = true;

                Socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);

                await Socket.ConnectAsync(EndPoint, token);

                if (ConnectionType == TcpUnderlyingConnectionType.FastSocket) {
                    DuplexPipe = ConnectionFactory.Create(Socket, null);
                } else {
                    Stream = await GetStream(Socket);
                    DuplexPipe = ConnectionFactory.Create(Socket, Stream);
                }

                var _ = DoReceive(token);
                _ = DoSend(token);

                Logger.LogInformation("{Source} Tcp client connected. {Endpoint}", this, EndPoint);
                return; // exit this connecting loop
            } catch (Exception ex) {
                Log.Error(ex, "Connect error");
            }

            await Task.Delay(Options.ReconnectInterval, token);
        }
    }

    private async Task DoReceive(CancellationToken token) {
        var frame = FramePool.Get();

        try {
            if (Options.Handshaker.StartHandshake(this)) {
                Logger.LogDebug("{Source} handshaked successfully", this);
                IsHandshaked = true;
                OnConnect?.Invoke();
            }

            while (!token.IsCancellationRequested
                   && DuplexPipe != null) {
                var result = await DuplexPipe.Input.ReadAsync(token);
                var position = ParseFrame(ref frame, ref result);

                DuplexPipe.Input.AdvanceTo(position);

                if (result.IsCanceled || result.IsCompleted) {
                    break;
                }
            }
        } catch (Exception ex) {
            Log.Error(ex, "{Source} Receive loop error", this);
        } finally {
            FramePool.Return(frame);
            await InternalDisconnect();
        }
    }

    private SequencePosition ParseFrame(ref ITcpFrame frame, ref ReadResult result) {
        var buffer = result.Buffer;

        if (buffer.Length == 0) {
            return buffer.Start;
        }

        if (IsHandshaked) {
            var endPosition = frame.Read(ref buffer);

            var messageType = frame.MessageType;
            if (messageType == null) {
                return endPosition;
            }

            EventEmitter.Emit(messageType.Value, frame);

            frame.Reset();

            return endPosition;
        }

        if (Options.Handshaker.Handshake(this, ref buffer, out var headerPosition)) {
            Logger.LogDebug("{Source} handshaked successfully", this);

            IsHandshaked = true;
            OnConnect?.Invoke();

            return headerPosition;
        }

        if (buffer.Length > Options.MaxHandshakeSizeBytes) {
            throw new Exception($"Handshake not valid and exceeded {nameof(Options.MaxHandshakeSizeBytes)}.");
        }

        return buffer.End;
    }

    private async Task DoSend(CancellationToken token) {
        while (!token.IsCancellationRequested && DuplexPipe != null
                                              && await OutgoingFramesQueue.Reader.WaitToReadAsync(token)) {
            try {
                var frame = await OutgoingFramesQueue.Reader.ReadAsync(token);
                Log.Information("Frame: {Frame}", frame);

                if (frame.Data.Length == 0) {
                    continue;
                }

                if (frame.IsRawOnly) {
                    await DuplexPipe.Output.WriteAsync(frame.Data, token); // flushes automatically
                } else {
                    SendFrame(frame);
                    await DuplexPipe.Output.FlushAsync(token); // not flushed inside frame
                }
            } catch (Exception ex) {
                Log.Error(ex, "Send frame error");
            }
        }
    }

    private void SendFrame(ITcpFrame frame) {
        var binarySize = frame.GetBinarySize();

        if (binarySize <= TcpConstants.SafeStackBufferSize) {
            Span<byte> span = stackalloc byte[binarySize];
            frame.Write(ref span);

            DuplexPipe!.Output.Write(span);
        } else {
            using var memoryOwner = MemoryPool<byte>.Shared.Rent(binarySize); // gives back memory at end of scope
            var span = memoryOwner.Memory.Span[..binarySize]; // rent memory can be bigger, clamp it

            frame.Write(ref span);

            DuplexPipe!.Output.Write(span);
        }
    }

    private void InitFactory() {
        var type = TcpUnderlyingConnectionType.NetworkStream;

        if (Options.ConnectionType != TcpUnderlyingConnectionType.Unset) {
            Logger.LogDebug("{Source} Underlying connection type overwritten, initialising with: {Type}", this,
                Options.ConnectionType);
            type = Options.ConnectionType;
        } else {
            if (Options.IsSecure) {
                type = TcpUnderlyingConnectionType.SslStream;
            } else {
                type = TcpUnderlyingConnectionType.FastSocket;
            }

            Logger.LogDebug("{Source} Underlying connection type chosen automatically: {Type}", this, type);
        }

        CreateFactory(type);
    }

    private void CreateFactory(TcpUnderlyingConnectionType type) {
        ConnectionType = type;

        switch (ConnectionType) {
            case TcpUnderlyingConnectionType.FastSocket:
                ConnectionFactory = new SocketConnectionFactory(Options.SocketConnectionOptions);
                break;

            case TcpUnderlyingConnectionType.SslStream:
                ConnectionFactory = new StreamConnectionFactory(Options.StreamConnectionOptions);
                break;

            case TcpUnderlyingConnectionType.NetworkStream:
                ConnectionFactory = new StreamConnectionFactory(Options.StreamConnectionOptions);
                break;
        }
    }

    private async Task<Stream?> GetStream(Socket socket) {
        NetworkStream stream = new(socket);

        if (ConnectionType == TcpUnderlyingConnectionType.NetworkStream) {
            return stream;
        }

        SslStream? sslStream = null;

        try {
            sslStream = new SslStream(stream, false);

            var task = sslStream.AuthenticateAsClientAsync(Options.Host!);
            await task.WaitAsync(TimeSpan.FromSeconds(60));

            return sslStream;
        } catch (Exception err) {
            if (sslStream != null) {
                await sslStream.DisposeAsync();
            }

            if (err is TimeoutException) {
                Logger.LogDebug("{Source} Ssl handshake timeouted.", this);
            }

            Logger.LogDebug("{Source} Certification fail get stream. {Error}", this, err);
            return null;
        }
    }
}