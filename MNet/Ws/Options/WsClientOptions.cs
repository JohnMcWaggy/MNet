namespace MNet.Ws.Options;

public sealed class WsClientOptions : TcpClientOptions {
    public WsClientOptions() {
        Handshaker = new WsClientHandshaker();
        FramePool = new TcpFramePool(() => new WsFrame());
    }
}