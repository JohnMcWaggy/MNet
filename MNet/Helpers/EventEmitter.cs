using System.Linq.Expressions;

namespace MNet.Helpers;

public sealed class EventEmitter(ITcpSerializer serializer) {
    private readonly ConcurrentDictionary<uint, Action<ITcpFrame>> _Handlers = new();

    private readonly ConcurrentDictionary<uint, Action<ITcpFrame, TcpServerConnection>> _HandlersServer = new();

    private readonly ITcpSerializer _Serializer = serializer;

    public void On<T>(uint messageType, Delegate callback) {
        if (_Handlers.ContainsKey(messageType)) {
            throw new InvalidOperationException("Only one handler per event");
        }

        var callbackInfo = callback.Method;
        var callbackParameters = callbackInfo.GetParameters();

        if (callbackParameters.Length <= 0 || callbackParameters.Length > 2) {
            throw new Exception("Invalid parameter length.");
        }

        var isSerialize = true;
        if (callbackParameters[0].ParameterType == typeof(ReadOnlyMemory<byte>)) {
            isSerialize = false;
        } else {
            ArgumentOutOfRangeException.ThrowIfNotEqual(callbackParameters[0].ParameterType.IsClass, true,
                "First parameter wrong type.");
        }

        var isServerCallback = false;
        var typeServerConnection = typeof(TcpServerConnection);

        if (callbackParameters.Length == 2) {
            // server needs TcpServerConnection 2nd parameter
            isServerCallback = true;
            if (callbackParameters[1].ParameterType != typeServerConnection) {
                throw new ArgumentException("Second parameter wrong type.");
            }
        }

        var parameters = callbackParameters
            .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name)).ToArray();

        var paraFrame = Expression.Parameter(typeof(ITcpFrame), "frame");

        var memberFrameData = Expression.Property(paraFrame, nameof(ITcpFrame.Data));
        var memberFrameDataSpan = Expression.Property(memberFrameData, nameof(ReadOnlyMemory<byte>.Span));

        var dataProperty = typeof(ITcpFrame).GetProperty(nameof(ITcpFrame.Data))!;
        var dataCall = Expression.Call(paraFrame, dataProperty.GetGetMethod()!);

        var dataSpanProperty = typeof(ReadOnlyMemory<byte>).GetProperty(nameof(ReadOnlyMemory<byte>.Span))!;
        var dataSpanCall = Expression.Call(memberFrameData, dataSpanProperty.GetGetMethod()!);

        var resultFirstCall = dataCall; // not serialize, just have the getter function to return readonlymemory

        if (isSerialize) {
            // if serializable parameter, we have to turn span call into deserialize call

            var deserializeMethod = typeof(ITcpSerializer).GetMethod(nameof(ITcpSerializer.Deserialize))!;
            deserializeMethod = deserializeMethod.MakeGenericMethod(typeof(T));

            resultFirstCall = Expression.Call(Expression.Constant(_Serializer), deserializeMethod, dataSpanCall);
        }

        var targetObject = callback.Target;

        if (isServerCallback) {
            var callbackCall = targetObject != null
                ? Expression.Call(Expression.Constant(targetObject), callbackInfo, resultFirstCall, parameters[1])
                : Expression.Call(callbackInfo, resultFirstCall, parameters[1]);

            var action = Expression.Lambda<Action<ITcpFrame, TcpServerConnection>>
                (callbackCall, paraFrame, parameters[1]).Compile();

            _HandlersServer.TryAdd(messageType, action);
        } else {
            var callbackCall = targetObject != null
                ? Expression.Call(Expression.Constant(targetObject), callbackInfo, resultFirstCall)
                : Expression.Call(callbackInfo, resultFirstCall);

            var action = Expression.Lambda<Action<ITcpFrame>>
                (callbackCall, paraFrame).Compile();

            _Handlers.TryAdd(messageType, action);
        }
    }


    public void Emit(uint messageType, ITcpFrame frame) {
        if (!_Handlers.TryGetValue(messageType, out var handler)) {
            return;
        }

        handler(frame);
    }

    public void ServerEmit(uint messageType, ITcpFrame frame, TcpServerConnection connection) {
        if (!_HandlersServer.TryGetValue(messageType, out var handler)) {
            return;
        }

        handler(frame, connection);
    }
}

public delegate Task EventDelegateAsync<T>(T? param);

public delegate void EventDelegate<T>(T? param);

public delegate Task ServerEventDelegateAsync<T>(T? param, TcpServerConnection connection);

public delegate void ServerEventDelegate<T>(T? param, TcpServerConnection connection);