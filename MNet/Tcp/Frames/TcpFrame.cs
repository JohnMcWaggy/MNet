using Serilog;

namespace MNet.Tcp.Frames;

public sealed class TcpFrame : ITcpFrame {
    private const int DataLengthBufferSize = 4;

    private Memory<byte> _data;
    private IMemoryOwner<byte>? _dataOwner;
    private IMemoryOwner<byte>? _dataOwnerTemp;
    private int _lengthData;
    private uint _messageType;

    private int _steps;

    public uint? MessageType { get; set; }
    public ReadOnlyMemory<byte> Data { get; set; }

    public bool IsRawOnly { get; set; } = false;
    public bool IsSending { get; set; } = false;

    public int GetBinarySize() {
        // 2 bytes for MessageType + 4 bytes for Data length + actual Data length
        return 2 + DataLengthBufferSize + Data.Length;
    }

    public void Write(ref Span<byte> buffer) {
        if (MessageType == null) {
            return;
        }

        // Write the MessageType as ushort (2 bytes)
        BinaryPrimitives.WriteUInt16BigEndian(buffer[..2], (ushort)MessageType);
        // Write the length of the data (4 bytes)
        BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(2, 4), Data.Length);
        // Write the actual data
        Data.Span.CopyTo(buffer.Slice(2 + 4, Data.Length));
    }

    public SequencePosition Read(ref ReadOnlySequence<byte> buffer) {
        var reader = new SequenceReader<byte>(buffer);
        var position = buffer.Start;

        if (_steps == 0) {
            if (!ReadMessageType(ref reader, ref buffer, out position, out _messageType)) {
                return position;
            }

            _steps = 1;
        }

        if (_steps == 1) {
            if (!ReadLengthDataBuffer(ref reader, ref buffer, out position, out _lengthData)) {
                return position;
            }

            if (_lengthData > TcpConstants.MaxFrameDataLength) {
                throw new ArgumentOutOfRangeException(nameof(_lengthData));
            }

            _steps = 2;
        }

        if (_steps == 2) {
            if (!ReadData(ref reader, ref buffer, out position)) {
                return position;
            }

            _steps = 3;
        }

        MessageType = _messageType;

        return position;
    }

    public void Reset() {
        _dataOwner?.Dispose();
        _dataOwnerTemp?.Dispose();

        // Reset all private fields
        _steps = 0;
        _lengthData = 0;
        _dataOwner = null;
        _dataOwnerTemp = null;
        _messageType = 0;
    }

    private bool ReadData(ref SequenceReader<byte> reader, ref ReadOnlySequence<byte> buffer,
        out SequencePosition position) {
        var targetSize = (int)Math.Min(reader.Remaining, _lengthData); // remaining or buffer.length

        if (_data.IsEmpty) {
            var earlyExit = targetSize == _lengthData; // everything already here, no batching

            if (earlyExit) {
                Data = _data = new byte[_lengthData];
            } else {
                _dataOwner = MemoryPool<byte>.Shared.Rent(targetSize);

                _data = _dataOwner.Memory[..targetSize];
                //_Data = new byte[targetSize];
            }

            reader.TryCopyTo(_data.Span);
            buffer = buffer.Slice(_data.Length);

            position = buffer.Start;
            _lengthData -= targetSize;

            return earlyExit;
        }

        var finished = targetSize == _lengthData;

        if (finished) {
            Memory<byte> tempResult = new byte[targetSize + _data.Length];

            _data.CopyTo(tempResult.Slice(0, _data.Length));

            var slice = buffer.Slice(0, targetSize);
            slice.CopyTo(tempResult.Span.Slice(_data.Length, targetSize));

            Data = tempResult;
            _data = null;

            _dataOwner?.Dispose();
            _dataOwner = null;
        } else {
            _dataOwnerTemp = MemoryPool<byte>.Shared.Rent(targetSize + _data.Length);

            var memory = _dataOwnerTemp.Memory.Slice(0, targetSize + _data.Length);
            //Memory<byte> memory = new byte[targetSize + _Data.Length];

            _data.CopyTo(memory.Slice(0, _data.Length));

            _dataOwner?.Dispose();
            _dataOwner = null;

            _dataOwner = _dataOwnerTemp;
            _dataOwnerTemp = null;

            var tempBuffer = buffer.Slice(0, targetSize);
            var tempTarget = memory.Span.Slice(_data.Length, targetSize);

            _data = memory;
            _lengthData -= targetSize;

            tempBuffer.CopyTo(tempTarget);
        }

        buffer = buffer.Slice(targetSize);
        position = buffer.Start;

        return finished;
    }

    private bool ReadLengthDataBuffer(ref SequenceReader<byte> reader, ref ReadOnlySequence<byte> buffer,
        out SequencePosition position, out int length) {
        Span<byte> span = stackalloc byte[4];

        if (!reader.TryCopyTo(span)) {
            length = 0;
            position = buffer.Start;

            return false;
        }

        reader.Advance(span.Length);
        buffer = buffer.Slice(span.Length);

        position = buffer.Start;
        length = BinaryPrimitives.ReadInt32BigEndian(span);
        Log.Information("Length: {Length}", length);

        return true;
    }

    private bool ReadMessageType(ref SequenceReader<byte> reader, ref ReadOnlySequence<byte> buffer,
        out SequencePosition position, out uint messageType) {
        Span<byte> span = stackalloc byte[2];

        if (!reader.TryCopyTo(span)) {
            messageType = 0;
            position = buffer.Start;

            return false;
        }

        reader.Advance(span.Length);
        buffer = buffer.Slice(span.Length);

        position = buffer.Start;
        messageType = BinaryPrimitives.ReadUInt16BigEndian(span);

        return true;
    }
}