namespace MNet.Tcp.Frames;

public class TcpFramePool(Func<ITcpFrame> frameFactory) : ITcpFramePool {

    private readonly ConcurrentQueue<ITcpFrame> _pool = new();

    public ITcpFrame Get() {
        return _pool.TryDequeue(out var frame) ? frame : frameFactory();
    }

    public void Return(ITcpFrame frame) {
        frame.Reset();
        _pool.Enqueue(frame);
    }
}