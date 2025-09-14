using MemoryPack;
using Serilog;

namespace MNet.Tcp.Serializers;

public class TcpMemoryPackSerializer : ITcpSerializer {
    public ReadOnlySpan<byte> SerializeAsSpan<T>(T target) where T : class {
        var data = MemoryPackSerializer.Serialize(target);
        Log.Information("Serialized {Size} bytes", data.Length);
        return data;
    }

    public ReadOnlyMemory<byte> SerializeAsMemory<T>(T target) where T : class {
        var data = MemoryPackSerializer.Serialize(target);
        Log.Information("Serialized {Size} bytes", data.Length);
        return data;
    }

    public T? Deserialize<T>(ReadOnlySpan<byte> source) where T : class {
        Log.Information("Deserializing {Size} bytes", source.Length);
        return MemoryPackSerializer.Deserialize<T>(source);
    }
}