using System.Buffers.Binary;
using Microsoft.Extensions.Logging;
using MNet.Helpers;
using MNet.Tcp;
using MNet.Tcp.Options;

namespace MNet.Testing;

internal static class PerformanceTestBinary {
    public static async Task Run(ILogger debugLogger, TcpUnderlyingConnectionType type) {
        long countPerSecond = 0;
        var server = new TcpServer(new TcpServerOptions {
            Address = "127.0.0.1",
            Port = 30300,
            ConnectionType = type
        });

        server.On<ReadOnlyMemory<byte>>(0, (buffer, conn) => {
            var a = BinaryPrimitives.ReadDoubleBigEndian(buffer.Span.Slice(0, 8));
            var b = BinaryPrimitives.ReadDoubleBigEndian(buffer.Span.Slice(8, 8));

            var sum = a + b;

            var payload = new Memory<byte>(new byte[8]);
            BinaryPrimitives.WriteDoubleBigEndian(payload.Span.Slice(0, 8), sum);

            conn.Send(1, payload);

            Interlocked.Increment(ref countPerSecond);
        });

        server.OnConnect += conn => { debugLogger.LogInformation("{Count} Connections", server.ConnectionCount); };

        server.OnDisconnect += conn => { debugLogger.LogInformation("{Count} Connections", server.ConnectionCount); };

        server.Start();

        for (var e = 0; e < 50; e++) {
            new PerformanceTestBinaryWorker(debugLogger, type);
        }

        while (true) {
            await Task.Delay(1000);
            debugLogger.LogInformation("{count} op/s", countPerSecond);

            Interlocked.Exchange(ref countPerSecond, 0);
        }
    }
}

internal class PerformanceTestBinaryWorker {
    public PerformanceTestBinaryWorker(ILogger debugLogger, TcpUnderlyingConnectionType type) {
        LastTask = GenerateTask();
        Client = new TcpClient(new TcpClientOptions {
            Address = "127.0.0.1",
            Port = 30300,
            ConnectionType = type
        });

        Client.On<ReadOnlyMemory<byte>>(1, res => {
            var sum = BinaryPrimitives.ReadDoubleBigEndian(res.Span.Slice(0, 8));

            //if (LastTask.A + LastTask.B != sum) {
            //    debugLogger.LogError("Mismatch found: {LastTaskA} + {LastTaskB} = {resSum}", LastTask.A, LastTask.B, sum);
            //}

            SendTask();
        });

        Client.OnConnect += () => { SendTask(); };

        Client.Connect();
    }

    private PerformanceTestJsonEntity LastTask { get; set; }

    private TcpClient Client { get; }

    private void SendTask() {
        LastTask = GenerateTask();

        var payload = new Memory<byte>(new byte[16]);
        BinaryPrimitives.WriteDoubleBigEndian(payload.Span.Slice(0, 8), LastTask.A);
        BinaryPrimitives.WriteDoubleBigEndian(payload.Span.Slice(8, 8), LastTask.B);

        Client.Send(0, payload);
    }

    private PerformanceTestJsonEntity GenerateTask() {
        return new PerformanceTestJsonEntity {
            A = RandomUtils.Next(1, 60_000),
            B = RandomUtils.Next(1, 60_000)
        };
    }
}