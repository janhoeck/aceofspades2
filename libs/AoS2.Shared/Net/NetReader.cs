using System;
using System.Text;

namespace AoS2.Shared.Net
{
    /// <summary>Eine empfangene Nachricht ist kaputt oder passt nicht zum Protokoll.</summary>
    public sealed class ProtocolException : Exception
    {
        public ProtocolException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Liest, was <see cref="NetWriter"/> geschrieben hat. Daten aus dem Netz sind nie vertrauenswürdig:
    /// Jeder Lesefehler (zu kurz, zu lang, ungültiger Wert) wirft <see cref="ProtocolException"/>, nie etwas anderes.
    /// </summary>
    public sealed class NetReader
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _position;

        public NetReader(byte[] data) : this(data, 0, data.Length)
        {
        }

        public NetReader(byte[] data, int offset, int count)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset + count > data.Length) throw new ArgumentOutOfRangeException(nameof(count));
            _data = data;
            _position = offset;
            _end = offset + count;
        }

        public int Remaining => _end - _position;

        public bool AtEnd => _position >= _end;

        public byte ReadByte()
        {
            Need(1);
            return _data[_position++];
        }

        public bool ReadBool()
        {
            var b = ReadByte();
            if (b > 1) throw new ProtocolException($"Ungültiger Wahrheitswert {b}.");
            return b == 1;
        }

        public ushort ReadUShort()
        {
            Need(2);
            var v = (ushort)(_data[_position] | _data[_position + 1] << 8);
            _position += 2;
            return v;
        }

        public short ReadShort() => (short)ReadUShort();

        public uint ReadUInt()
        {
            Need(4);
            var v = (uint)(_data[_position] | _data[_position + 1] << 8 | _data[_position + 2] << 16 | _data[_position + 3] << 24);
            _position += 4;
            return v;
        }

        public int ReadInt() => (int)ReadUInt();

        public float ReadFloat() => BitConverter.Int32BitsToSingle(ReadInt());

        /// <summary>Wie <see cref="ReadFloat"/>, lehnt aber NaN und Unendlich ab (die würden die Simulation vergiften).</summary>
        public float ReadFiniteFloat()
        {
            var v = ReadFloat();
            if (float.IsNaN(v) || float.IsInfinity(v)) throw new ProtocolException("Ungültige Kommazahl (NaN/Unendlich).");
            return v;
        }

        public uint ReadVarUInt()
        {
            uint result = 0;
            for (var shift = 0; shift < 35; shift += 7)
            {
                var b = ReadByte();
                if (shift == 28 && b > 0x0F) throw new ProtocolException("Varint zu groß.");
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
            }
            throw new ProtocolException("Varint zu lang.");
        }

        /// <summary>Varint, der höchstens <paramref name="max"/> sein darf (z. B. Anzahl von Einträgen).</summary>
        public int ReadCount(int max)
        {
            var v = ReadVarUInt();
            if (v > (uint)max) throw new ProtocolException($"Anzahl {v} ist größer als erlaubt ({max}).");
            return (int)v;
        }

        public string ReadString(int maxBytes)
        {
            var length = ReadCount(maxBytes);
            Need(length);
            try
            {
                var s = new UTF8Encoding(false, true).GetString(_data, _position, length);
                _position += length;
                return s;
            }
            catch (ArgumentException)
            {
                throw new ProtocolException("Text ist kein gültiges UTF-8.");
            }
        }

        public byte[] ReadBytes(int count)
        {
            Need(count);
            var result = new byte[count];
            Buffer.BlockCopy(_data, _position, result, 0, count);
            _position += count;
            return result;
        }

        /// <summary>Am Ende einer Nachricht: Übrige Bytes deuten auf ein anderes Protokoll hin.</summary>
        public void ExpectEnd()
        {
            if (!AtEnd) throw new ProtocolException($"{Remaining} Byte zu viel am Ende der Nachricht.");
        }

        private void Need(int count)
        {
            if (count < 0 || _position + count > _end) throw new ProtocolException("Nachricht ist zu kurz.");
        }
    }
}
