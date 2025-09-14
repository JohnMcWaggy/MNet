namespace MNet.Tcp.Interfaces;

public interface ITcpFramePool {
    public ITcpFrame Get();
    public void Return(ITcpFrame frame);
}