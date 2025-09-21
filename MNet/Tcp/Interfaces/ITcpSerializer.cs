namespace MNet.Tcp.Interfaces;

public interface ITcpSerializer {
    public ReadOnlySpan<byte> SerializeAsSpan<T>(T target);

    public ReadOnlyMemory<byte> SerializeAsMemory<T>(T target);

    public T? Deserialize<T>(ReadOnlySpan<byte> source);
}