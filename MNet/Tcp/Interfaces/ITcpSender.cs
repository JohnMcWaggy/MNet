namespace MNet.Tcp.Interfaces;

internal interface ITcpSender {
    public void Send<T>(uint messageType, T payload);

    public void Send(uint messageType, Memory<byte> payload);

    /// <summary>
    ///     Should only be used for handshaking
    /// </summary>
    /// <param name="payload"></param>
    public void Send(Memory<byte> payload);

    internal void Send(ITcpFrame frame);
}