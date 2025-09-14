namespace MNet.Ws;

public sealed class WsFrame : ITcpFrame {
    private bool _BitFinSet;
    private bool _BitMaskSet;

    private byte _FirstByte;
    private bool _FirstFragment = true;
    private Memory<byte> _MaskKey;
    private WsOpcode _Opcode;
    private byte _SecondByte;

    private ulong _Size;

    private int _Steps;

    private TcpFrame InternalFrame { get; } = new();

    public WsOpcode Opcode { get; set; } = WsOpcode.BinaryFrame; //currently all messages treated as binary

    public uint? MessageType {
        get => InternalFrame?.MessageType;
        set => InternalFrame.MessageType = value;
    }

    public ReadOnlyMemory<byte> Data {
        get => InternalFrame.Data;
        set => InternalFrame.Data = value;
    }

    public bool IsRawOnly {
        get => InternalFrame.IsRawOnly;
        set => InternalFrame.IsRawOnly = value;
    }

    public bool IsSending {
        get => InternalFrame.IsSending;
        set => InternalFrame.IsSending = value;
    }

    public int GetBinarySize() {
        var size = InternalFrame.GetBinarySize();
        return 1 + GetLengthByteSize(size) + size;
    }

    public void Write(ref Span<byte> buffer) {
        byte bitFin = 0x80;
        var first = (byte)(bitFin | (byte)Opcode);

        buffer[0] = first;
        var size = InternalFrame.GetBinarySize();

        var sizeWrittenBytes = WriteLengthBytes(ref buffer, size);
        var rest = buffer[(1 + sizeWrittenBytes)..];

        InternalFrame.Write(ref rest);
    }

    public SequencePosition Read(ref ReadOnlySequence<byte> buffer) {
        // fragmented messages can be interrupted by ping pong close frames (not handled rn, clients normally wont do it)

        // buffer all fragmented messages
        // if all buffers receive, combine
        // give it to InternalFrame

        var reader = new SequenceReader<byte>(buffer);

        if (_Steps == 0) {
            if (buffer.Length < 2) {
                return buffer.Start;
            }

            byte bitFinFlag = 0x80;
            byte opcodeFlag = 0x0F;

            reader.TryRead(out var first); //should never fail, length >= 2

            _BitFinSet = (first & bitFinFlag) == bitFinFlag;
            var opcode = (WsOpcode)(first & opcodeFlag);

            if (_FirstFragment) {
                _Opcode = opcode;
                _FirstFragment = false;
            }

            byte bitMaskFlag = 0x80;
            reader.TryRead(out var second); //should never fail, length >= 2

            _BitMaskSet = (second & bitMaskFlag) == bitMaskFlag;
            _FirstByte = first;
            _SecondByte = second;

            buffer = buffer.Slice(2);
            _Steps = 1;
        }

        if (_Steps == 1) {
            if (!TryReadLength(ref reader, ref buffer, out var size)) {
                return buffer.Start;
            }

            _Size = size;
            _Steps = 2;
        }

        if (_Steps == 2) {
            if (_Size > 0) {
                //TODO: Reading
            } else {
                _Steps = 3;
            }
        }

        return buffer.Start;
    }

    public void Reset() {
        InternalFrame.Reset();

        _Steps = 0;
        _Size = 0;
        _FirstFragment = true;
    }

    private bool TryReadLength(ref SequenceReader<byte> reader, ref ReadOnlySequence<byte> buffer, out ulong size) {
        byte dataLengthFlag = 0x7F;
        var length = (uint)(_SecondByte & dataLengthFlag);

        if (length == 126) {
            Span<byte> bytesLength = stackalloc byte[2];

            if (!reader.TryCopyTo(bytesLength)) {
                size = 0;
                return false;
            }

            size = BinaryPrimitives.ReadUInt16BigEndian(bytesLength);
            buffer = buffer.Slice(2);
        } else if (length == 127) {
            Span<byte> bytesLength = stackalloc byte[8];

            if (!reader.TryCopyTo(bytesLength)) {
                size = 0;
                return false;
            }

            size = BinaryPrimitives.ReadUInt64BigEndian(bytesLength);
            buffer = buffer.Slice(8);
        } else {
            size = length;
        }

        return true;
    }

    private static int WriteLengthBytes(ref Span<byte> buffer, int size) {
        if (size <= 125) {
            buffer[1] = (byte)size;
            return 1;
        }

        if (size <= 65535) {
            buffer[1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(2, 2), (ushort)size);
            return 3;
        }

        buffer[1] = 127;
        BinaryPrimitives.WriteUInt64BigEndian(buffer.Slice(2, 8), (ulong)size);
        return 9;
    }

    private static int GetLengthByteSize(int innerSize) {
        return innerSize switch {
            <= 125 => 1,
            > 125 and <= 65535 => 3,
            _ => 9
        };
    }
}