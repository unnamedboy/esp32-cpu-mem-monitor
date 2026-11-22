using System.Security.Cryptography;

namespace ResourceMonitorDaemon.Provisioning
{
    /// <summary>Protocol Buffers wire types (https://protobuf.dev/programming-guides/encoding/).</summary>
    internal enum ProtoWireType
    {
        Varint = 0,
        Fixed64 = 1,
        LengthDelimited = 2,
        StartGroup = 3,
        EndGroup = 4,
        Fixed32 = 5,
    }

    /// <summary>
    /// Minimal protobuf encoder for the handful of ESP-IDF provisioning messages we send. The caller
    /// decides which fields to emit (proto3 omits default values; see ProvisioningMessages).
    /// The internal buffer is zeroed whenever it is grown or disposed because it may hold a Wi-Fi
    /// passphrase.
    /// </summary>
    internal sealed class ProtoWriter : IDisposable
    {
        private byte[] _buffer;
        private int _length;

        public ProtoWriter(int initialCapacity = 128)
        {
            _buffer = new byte[Math.Max(16, initialCapacity)];
        }

        public int Length => _length;

        public void WriteVarintField(int fieldNumber, ulong value)
        {
            WriteTag(fieldNumber, ProtoWireType.Varint);
            WriteVarint(value);
        }

        /// <summary>
        /// Writes an int32/enum field. Negative values are sign-extended to 64 bits (10 bytes), as
        /// the protobuf spec requires for int32.
        /// </summary>
        public void WriteInt32Field(int fieldNumber, int value) => WriteVarintField(fieldNumber, unchecked((ulong)(long)value));

        public void WriteBytesField(int fieldNumber, ReadOnlySpan<byte> value)
        {
            WriteTag(fieldNumber, ProtoWireType.LengthDelimited);
            WriteVarint((ulong)value.Length);
            WriteRaw(value);
        }

        /// <summary>Writes an embedded message field (length-delimited, encoded by <paramref name="writeBody"/>).</summary>
        public void WriteMessageField(int fieldNumber, Action<ProtoWriter> writeBody)
        {
            using var inner = new ProtoWriter(_buffer.Length);
            writeBody(inner);
            WriteBytesField(fieldNumber, inner.WrittenSpan);
        }

        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

        public byte[] ToArray() => WrittenSpan.ToArray();

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(_buffer);
            _length = 0;
        }

        private void WriteTag(int fieldNumber, ProtoWireType wireType)
        {
            if (fieldNumber <= 0 || fieldNumber > 0x1FFFFFFF)
            {
                throw new ArgumentOutOfRangeException(nameof(fieldNumber));
            }

            WriteVarint(((ulong)fieldNumber << 3) | (ulong)wireType);
        }

        private void WriteVarint(ulong value)
        {
            Span<byte> scratch = stackalloc byte[10];
            var count = 0;
            do
            {
                var b = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0)
                {
                    b |= 0x80;
                }

                scratch[count++] = b;
            }
            while (value != 0);

            WriteRaw(scratch[..count]);
        }

        private void WriteRaw(ReadOnlySpan<byte> data)
        {
            if (_length + data.Length > _buffer.Length)
            {
                var grown = new byte[Math.Max(_buffer.Length * 2, _length + data.Length)];
                _buffer.AsSpan(0, _length).CopyTo(grown);
                CryptographicOperations.ZeroMemory(_buffer);
                _buffer = grown;
            }

            data.CopyTo(_buffer.AsSpan(_length));
            _length += data.Length;
        }
    }

    /// <summary>
    /// Minimal forward-only protobuf decoder. Unknown fields are skipped with <see cref="SkipField"/>.
    /// Malformed input raises <see cref="ProvisioningProtocolException"/>.
    /// </summary>
    internal ref struct ProtoReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _position;

        public ProtoReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _position = 0;
        }

        public bool IsAtEnd => _position >= _data.Length;

        /// <summary>Reads the next field tag, or returns false at the end of the message.</summary>
        public bool TryReadTag(out int fieldNumber, out ProtoWireType wireType)
        {
            if (IsAtEnd)
            {
                fieldNumber = 0;
                wireType = default;
                return false;
            }

            // Tags are uint32 on the wire: field numbers 1..2^29-1, wire types 0..5. Validate before any
            // narrowing cast so hostile input can only ever raise ProvisioningProtocolException.
            var tag = ReadVarint();
            var rawFieldNumber = tag >> 3;
            var rawWireType = tag & 0x7;
            if (rawFieldNumber < 1 || rawFieldNumber > MaxFieldNumber)
            {
                throw new ProvisioningProtocolException("Malformed protobuf message: field number out of range.");
            }

            if (rawWireType > (ulong)ProtoWireType.Fixed32)
            {
                throw new ProvisioningProtocolException($"Malformed protobuf message: invalid wire type {rawWireType}.");
            }

            fieldNumber = (int)rawFieldNumber;
            wireType = (ProtoWireType)rawWireType;
            return true;
        }

        private const ulong MaxFieldNumber = 0x1FFFFFFF;

        public ulong ReadVarint()
        {
            ulong result = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (_position >= _data.Length)
                {
                    throw new ProvisioningProtocolException("Malformed protobuf message: truncated varint.");
                }

                var b = _data[_position++];
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return result;
                }
            }

            throw new ProvisioningProtocolException("Malformed protobuf message: varint longer than 10 bytes.");
        }

        /// <summary>Reads an int32/enum varint (truncating a sign-extended 64-bit value).</summary>
        public int ReadInt32() => unchecked((int)ReadVarint());

        public ReadOnlySpan<byte> ReadLengthDelimited()
        {
            var length = ReadVarint();
            if (length > (ulong)(_data.Length - _position))
            {
                throw new ProvisioningProtocolException("Malformed protobuf message: length exceeds buffer.");
            }

            var slice = _data.Slice(_position, (int)length);
            _position += (int)length;
            return slice;
        }

        public void SkipField(ProtoWireType wireType)
        {
            switch (wireType)
            {
                case ProtoWireType.Varint:
                    ReadVarint();
                    break;
                case ProtoWireType.Fixed64:
                    Advance(8);
                    break;
                case ProtoWireType.LengthDelimited:
                    ReadLengthDelimited();
                    break;
                case ProtoWireType.Fixed32:
                    Advance(4);
                    break;
                default:
                    // Groups are deprecated and never used by the ESP-IDF provisioning protos.
                    throw new ProvisioningProtocolException($"Malformed protobuf message: unsupported wire type {wireType}.");
            }
        }

        private void Advance(int count)
        {
            if (count > _data.Length - _position)
            {
                throw new ProvisioningProtocolException("Malformed protobuf message: truncated fixed-width field.");
            }

            _position += count;
        }
    }
}
