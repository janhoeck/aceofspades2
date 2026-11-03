using System;
using System.Text;

namespace AoS2.Shared.Net
{
    /// <summary>
    /// Schreibt Netzwerknachrichten Byte für Byte in einen wachsenden Puffer (alle Zahlen Little Endian).
    /// Bewusst ohne Reflection: Jede Nachricht schreibt ihre Felder selbst in fester Reihenfolge,
    /// <see cref="NetReader"/> liest sie in derselben Reihenfolge wieder.
    /// </summary>
    public sealed class NetWriter
    {
        private byte[] _buffer;

        public NetWriter(int capacity = 256)
        {
            _buffer = new byte[Math.Max(16, capacity)];
        }

        /// <summary>Anzahl geschriebener Bytes.</summary>
        public int Length { get; private set; }

        /// <summary>Interner Puffer; gültig sind nur die ersten <see cref="Length"/> Bytes.</summary>
        public byte[] Buffer => _buffer;

        public void Clear() => Length = 0;

        public byte[] ToArray()
        {
            var result = new byte[Length];
            System.Buffer.BlockCopy(_buffer, 0, result, 0, Length);
            return result;
        }

        public void WriteByte(byte value)
        {
            Ensure(1);
            _buffer[Length++] = value;
        }

        public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

        public void WriteUShort(ushort value)
        {
            Ensure(2);
            _buffer[Length++] = (byte)value;
            _buffer[Length++] = (byte)(value >> 8);
        }

        public void WriteShort(short value) => WriteUShort((ushort)value);

        public void WriteUInt(uint value)
        {
            Ensure(4);
            _buffer[Length++] = (byte)value;
            _buffer[Length++] = (byte)(value >> 8);
            _buffer[Length++] = (byte)(value >> 16);
            _buffer[Length++] = (byte)(value >> 24);
        }

        public void WriteInt(int value) => WriteUInt((uint)value);

        /// <summary>Exakt die 32 Bit des float: Die Simulation muss auf beiden Seiten mit genau denselben Werten rechnen.</summary>
        public void WriteFloat(float value) => WriteInt(BitConverter.SingleToInt32Bits(value));

        /// <summary>Ganzzahl mit variabler Länge: 7 Bit pro Byte, kleine Zahlen brauchen nur ein Byte.</summary>
        public void WriteVarUInt(uint value)
        {
            Ensure(5);
            while (value >= 0x80)
            {
                _buffer[Length++] = (byte)(value | 0x80);
                value >>= 7;
            }
            _buffer[Length++] = (byte)value;
        }

        /// <summary>Text: Länge in Bytes (Varint) und UTF-8.</summary>
        public void WriteString(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? "");
            WriteVarUInt((uint)bytes.Length);
            WriteBytes(bytes, 0, bytes.Length);
        }

        public void WriteBytes(byte[] data, int offset, int count)
        {
            Ensure(count);
            System.Buffer.BlockCopy(data, offset, _buffer, Length, count);
            Length += count;
        }

        private void Ensure(int extra)
        {
            if (Length + extra <= _buffer.Length) return;
            var size = _buffer.Length * 2;
            while (size < Length + extra) size *= 2;
            Array.Resize(ref _buffer, size);
        }
    }
}
