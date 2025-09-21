using Serilog;

namespace MNet.Tcp;

public class TcpServer : TcpBase, IDisposable {
    public delegate void ConnectHandler(TcpServerConnection connection);

    public delegate void DisconnectHandler(TcpServerConnection connection);

    private readonly ConcurrentDictionary<string, TcpServerConnection> _Connections = new();

    public TcpServer(TcpServerOptions options) {
        if (options.IsSecure) ArgumentNullException.ThrowIfNull(options.Certificate, nameof(options.Certificate));

        if (!IPAddress.TryParse(options.Address, out var address))
            throw new ArgumentException($"{nameof(options.Address)} is not a valid IP address.");

        Options = options;
        EndPoint = new IPEndPoint(address, options.Port);

        Logger = Options.Logger;

        EventEmitter = new EventEmitter(Options.Serializer);

        InitFactory();
    }

    public TcpServerOptions Options { get; }

    public int ConnectionCount => _Connections.Count;

    public void Dispose() {
        ConnectionFactory?.Dispose();
    }

    public event ConnectHandler? OnConnect;
    public event DisconnectHandler? OnDisconnect;

    public void Start() {
        if (RunTokenSource != null) return;

        Logger.LogDebug("{Source} Starting the tcp server...", this);

        RunTokenSource = new CancellationTokenSource();
        BindSocket();

        var _ = DoAccept(RunTokenSource.Token);

        Logger.LogInformation("{Source} Server was started. {Endpoint}", this, EndPoint);
    }

    public void Stop() {
        if (RunTokenSource == null) return;

        Logger.LogDebug("{Source} Stopping the tcp server...", this);

        _Connections.Clear();

        RunTokenSource?.Cancel();
        RunTokenSource?.Dispose();

        RunTokenSource = null;

        try {
            Socket?.Shutdown(SocketShutdown.Both);
        }
        catch (Exception ex) {
            Log.Error(ex, "Socket shutdown failed");
        }

        Socket?.Dispose();
        Socket = null;

        Logger.LogInformation("{Source} Server was stopped. {Endpoint}", this, EndPoint);
    }

    public void Broadcast<T>(uint messageType, T payload) where T : class {
        foreach (var connection in _Connections.Values) connection.Send(messageType, payload);
    }

    public void Broadcast(uint messageType, Memory<byte> payload) {
        foreach (var connection in _Connections.Values) connection.Send(messageType, payload);
    }

    public void On<T>(uint messageType, ServerEventDelegate<T> handler) {
        InternalOn<T>(messageType, handler);
    }

    public void On<T>(uint messageType, ServerEventDelegateAsync<T> handler) {
        InternalOn<T>(messageType, handler);
    }

    private void InternalOn<T>(uint messageType, Delegate handler) {
        EventEmitter.On<T>(messageType, handler);
    }

    private async Task DoAccept(CancellationToken token) {
        while (!token.IsCancellationRequested) {
            var connection = await Accept(token);
            if (connection == null) return;

            Logger.LogInformation("{Source} New connection {identifier}", this, connection.UniqueId);

            var _ = DoReceive(connection, token);
            _ = DoSend(connection, token);
        }
    }

    private async ValueTask<TcpServerConnection?> Accept(CancellationToken token = default) {
        while (!token.IsCancellationRequested && Socket != null)
            try {
                var socket = await Socket.AcceptAsync(token);
                socket.NoDelay = true;

                if (ConnectionType == TcpUnderlyingConnectionType.FastSocket)
                    return new TcpServerConnection {
                        DuplexPipe = ConnectionFactory.Create(socket, null),
                        Server = this,
                        Socket = socket,
                        UniqueId = RandomUtils.RandomString(TcpConstants.UniqueIdLength)
                    };

                var stream = await GetStream(socket);
                if (stream == null) continue;

                return new TcpServerConnection {
                    DuplexPipe = ConnectionFactory.Create(socket, stream),
                    Server = this,
                    Socket = socket,
                    UniqueId = RandomUtils.RandomString(TcpConstants.UniqueIdLength)
                };
            }
            catch (ObjectDisposedException) {
                return null;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.OperationAborted) {
                return null;
            }
            catch (Exception ex) {
                // The connection got reset while it was in the backlog, so we try again.
                Logger.LogError(ex, "Error while accepting connection");
            }

        return null;
    }


    private async Task DoReceive(TcpServerConnection connection, CancellationToken token) {
        var frame = Options.FramePool.Get();
        var combinedToken = CancellationTokenSource.CreateLinkedTokenSource(connection.DisconnectToken.Token, token);

        try {
            Options.Handshaker.StartHandshake(connection);

            while (!token.IsCancellationRequested && !connection.DisconnectToken.IsCancellationRequested) {
                var result = await connection.DuplexPipe.Input.ReadAsync(combinedToken.Token);
                var position = ParseFrame(connection, ref frame, ref result);

                connection.DuplexPipe.Input.AdvanceTo(position);

                if (result.IsCanceled || result.IsCompleted) break;
            }
        }
        catch (Exception err) {
            Logger.LogError("{Source} DoReceive error on connection {UniqueId}, {Error}", this, connection.UniqueId,
                err);
        }
        finally {
            // disconnect

            Options.FramePool.Return(frame);
            await connection.DisposeAsync();

            try {
                combinedToken?.Dispose();
            }
            catch (Exception ex) {
                Logger.LogError(ex, "Error while receiving connection");
            }

            if (connection.UniqueId != null && _Connections.TryRemove(connection.UniqueId, out _))
                OnDisconnect?.Invoke(connection);
        }
    }

    private SequencePosition ParseFrame(TcpServerConnection connection, ref ITcpFrame frame, ref ReadResult result) {
        var buffer = result.Buffer;

        if (buffer.Length == 0) return buffer.Start;

        if (connection.IsHandshaked) {
            var endPosition = frame.Read(ref buffer);

            var messageType = frame.MessageType;
            if (messageType == null) return endPosition;

            EventEmitter.ServerEmit(messageType.Value, frame, connection);

            frame.Reset();

            return endPosition;
        }

        if (Options.Handshaker.Handshake(connection, ref buffer, out var headerPosition)) {
            connection.IsHandshaked = true;
            InsertNewConnection(connection);

            Logger.LogDebug("{Source} New connection handshaked {identifier}", this, connection.UniqueId);
            OnConnect?.Invoke(connection);

            return headerPosition;
        }

        if (buffer.Length > Options.MaxHandshakeSizeBytes)
            throw new Exception($"Handshake not valid and exceeded {nameof(Options.MaxHandshakeSizeBytes)}.");

        return buffer.End;
    }

    private void InsertNewConnection(TcpServerConnection connection) {
        while (!_Connections.TryAdd(connection.UniqueId, connection))
            connection.UniqueId = RandomUtils.RandomString(TcpConstants.UniqueIdLength);
    }

    private async Task DoSend(TcpServerConnection connection, CancellationToken token) {
        try {
            while (!token.IsCancellationRequested
                   && await connection.OutgoingFramesQueue.Reader.WaitToReadAsync(token))
                try {
                    var frame = await connection.OutgoingFramesQueue.Reader.ReadAsync(token);

                    if (frame.Data.Length == 0) continue;

                    if (frame.IsRawOnly) {
                        await connection.DuplexPipe.Output.WriteAsync(frame.Data, token); // flushes automatically
                    }
                    else {
                        SendFrame(connection, frame);
                        await connection.DuplexPipe.Output.FlushAsync(token); // not flushed inside frame
                    }
                }
                catch (Exception) {
                }
        }
        catch (Exception) {
        }
    }

    private void SendFrame(TcpServerConnection connection, ITcpFrame frame) {
        var binarySize = frame.GetBinarySize();

        if (binarySize <= TcpConstants.SafeStackBufferSize) {
            Span<byte> span = stackalloc byte[binarySize];
            frame.Write(ref span);

            connection.DuplexPipe.Output.Write(span);
        }
        else {
            using var memoryOwner = MemoryPool<byte>.Shared.Rent(binarySize); // gives back memory at end of scope
            var span = memoryOwner.Memory.Span[..binarySize]; // rent memory can be bigger, clamp it

            frame.Write(ref span);

            connection.DuplexPipe.Output.Write(span);
        }
    }

    private void BindSocket() {
        Socket listenSocket;
        try {
            listenSocket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);

            listenSocket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, false);
            listenSocket.NoDelay = true;

            listenSocket.Bind(EndPoint);

            Logger.LogDebug("{Source} Binding to following endpoint {Endpoint}", this, EndPoint);
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse) {
            throw new Exception(e.Message, e);
        }

        Socket = listenSocket;
        listenSocket.Listen();
    }

    private void InitFactory() {
        var type = TcpUnderlyingConnectionType.NetworkStream;

        if (Options.ConnectionType != TcpUnderlyingConnectionType.Unset) {
            Logger.LogDebug("{Source} Underlying connection type overwritten, initialising with: {Type}", this,
                Options.ConnectionType);
            type = Options.ConnectionType;
        }
        else {
            if (Options.IsSecure)
                type = TcpUnderlyingConnectionType.SslStream;
            else
                type = TcpUnderlyingConnectionType.FastSocket;

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

        if (ConnectionType == TcpUnderlyingConnectionType.NetworkStream) return stream;

        SslStream? sslStream = null;

        try {
            sslStream = new SslStream(stream, false);

            var task = sslStream.AuthenticateAsServerAsync(Options.Certificate!, false, SslProtocols.None, true);
            await task.WaitAsync(TimeSpan.FromSeconds(30));

            return sslStream;
        }
        catch (Exception err) {
            if (sslStream != null) await sslStream.DisposeAsync();

            if (err is TimeoutException) Logger.LogDebug("{Source} Ssl handshake timeout.", this);

            Logger.LogDebug("{Source} Certification fail get stream. {Error}", this, err);
            return null;
        }
    }
}