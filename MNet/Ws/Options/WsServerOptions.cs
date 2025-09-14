namespace MNet.Ws.Options;

public sealed class WsServerOptions : TcpServerOptions {
    public WsServerOptions() {
        Handshaker = new WsServerHandshaker();
        FramePool = new TcpFramePool(() => new WsFrame());
    }
}