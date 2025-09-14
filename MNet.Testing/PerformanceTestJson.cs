using Microsoft.Extensions.Logging;
using MNet.Helpers;
using MNet.Tcp;
using MNet.Tcp.Options;

namespace MNet.Testing;

internal static class PerformanceTestJson {
    public static async Task Run(ILogger debugLogger, TcpUnderlyingConnectionType type) {
        long countPerSecond = 0;
        var server = new TcpServer(new TcpServerOptions {
            Address = "127.0.0.1",
            Port = 30300,
            ConnectionType = type
        });

        server.On<PerformanceTestJsonEntity>(0, (entity, conn) => {
            if (entity == null) {
                debugLogger.LogError("Entity null.");
                return;
            }

            conn.Send(1, new PerformanceTestJsonEntityResult {
                Sum = entity.A + entity.B
            });

            Interlocked.Increment(ref countPerSecond);
        });

        server.OnConnect += conn => { debugLogger.LogInformation("{Count} Connections", server.ConnectionCount); };

        server.Start();

        for (var e = 0; e < 200; e++) {
            new PerformanceTestJsonWorker(debugLogger, type);
        }

        while (true) {
            await Task.Delay(1000);
            debugLogger.LogInformation("{count} op/s", countPerSecond);

            Interlocked.Exchange(ref countPerSecond, 0);
        }
    }
}

internal class PerformanceTestJsonWorker {
    public PerformanceTestJsonWorker(ILogger debugLogger, TcpUnderlyingConnectionType type) {
        LastTask = GenerateTask();
        Client = new TcpClient(new TcpClientOptions {
            Address = "127.0.0.1",
            Port = 30300,
            ConnectionType = type
        });

        Client.On<PerformanceTestJsonEntityResult>(1, res => {
            if (res == null) {
                Console.WriteLine("Null found");
                return;
            }

            if (LastTask.A + LastTask.B != res.Sum) {
                debugLogger.LogError("Mismatch found: {LastTask.A} + {LastTask.B} = {res.Sum}", LastTask.A, LastTask.B,
                    res.Sum);
            }

            SendTask();
        });

        Client.OnConnect += () => { SendTask(); };

        Client.Connect();
    }

    private PerformanceTestJsonEntity LastTask { get; set; }

    private TcpClient Client { get; }

    private void SendTask() {
        LastTask = GenerateTask();
        Client.Send(0, LastTask);
    }

    private PerformanceTestJsonEntity GenerateTask() {
        return new PerformanceTestJsonEntity {
            A = RandomUtils.Next(1, 60_000),
            B = RandomUtils.Next(1, 60_000)
        };
    }
}

internal class PerformanceTestJsonEntity {
    public double A { get; set; }

    public double B { get; set; }
}

internal class PerformanceTestJsonEntityResult {
    public double Sum { get; set; }
}