using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace LocalBloops.Telemetry
{
    public enum IrsdkVarType
    {
        Char = 0,
        Bool = 1,
        Int = 2,
        BitField = 3,
        Float = 4,
        Double = 5,
    }

    public readonly struct IrsdkVarBuf
    {
        public readonly int TickCount;
        public readonly int BufOffset;

        public IrsdkVarBuf(int tickCount, int bufOffset)
        {
            TickCount = tickCount;
            BufOffset = bufOffset;
        }
    }

    /// <summary>The 112-byte irsdk_header shared by the live memory map and .ibt files.</summary>
    public sealed class IrsdkHeader
    {
        public const int Size = 112;
        public const int MaxBufs = 4;
        public const int VarBufOffset = 48;
        public const int VarBufSize = 16;

        public int Ver;
        public int Status;
        public int TickRate;
        public int SessionInfoUpdate;
        public int SessionInfoLen;
        public int SessionInfoOffset;
        public int NumVars;
        public int VarHeaderOffset;
        public int NumBuf;
        public int BufLen;
        public readonly IrsdkVarBuf[] VarBufs = new IrsdkVarBuf[MaxBufs];

        public bool IsConnected => (Status & 1) != 0;

        public static IrsdkHeader Parse(ReadOnlySpan<byte> b)
        {
            if (b.Length < Size) throw new ArgumentException("irsdk header is too short", nameof(b));
            var h = new IrsdkHeader
            {
                Ver = Bytes.I32(b, 0),
                Status = Bytes.I32(b, 4),
                TickRate = Bytes.I32(b, 8),
                SessionInfoUpdate = Bytes.I32(b, 12),
                SessionInfoLen = Bytes.I32(b, 16),
                SessionInfoOffset = Bytes.I32(b, 20),
                NumVars = Bytes.I32(b, 24),
                VarHeaderOffset = Bytes.I32(b, 28),
                NumBuf = Bytes.I32(b, 32),
                BufLen = Bytes.I32(b, 36),
                // 40, 44: pad[2]
            };
            for (int i = 0; i < MaxBufs; i++)
            {
                int o = VarBufOffset + i * VarBufSize;
                h.VarBufs[i] = new IrsdkVarBuf(Bytes.I32(b, o), Bytes.I32(b, o + 4));
            }
            return h;
        }

        public int NewestBufferIndex()
        {
            int n = Math.Clamp(NumBuf, 1, MaxBufs);
            int best = 0;
            for (int i = 1; i < n; i++)
            {
                if (VarBufs[i].TickCount > VarBufs[best].TickCount) best = i;
            }
            return best;
        }
    }

    /// <summary>A 144-byte irsdk_varHeader.</summary>
    public sealed class IrsdkVarHeader
    {
        public const int Size = 144;

        public IrsdkVarType Type;
        public int Offset;
        public int Count;
        public bool CountAsTime;
        public string Name = "";
        public string Desc = "";
        public string Unit = "";

        public static IrsdkVarHeader Parse(ReadOnlySpan<byte> b)
        {
            return new IrsdkVarHeader
            {
                Type = (IrsdkVarType)Bytes.I32(b, 0),
                Offset = Bytes.I32(b, 4),
                Count = Bytes.I32(b, 8),
                CountAsTime = b[12] != 0,
                // 13..15 pad
                Name = Bytes.CStr(b, 16, 32),
                Desc = Bytes.CStr(b, 48, 64),
                Unit = Bytes.CStr(b, 112, 32),
            };
        }

        public static List<IrsdkVarHeader> ParseAll(ReadOnlySpan<byte> b, int count)
        {
            var list = new List<IrsdkVarHeader>(Math.Max(0, count));
            for (int i = 0; i < count && (i + 1) * Size <= b.Length; i++)
            {
                list.Add(Parse(b.Slice(i * Size, Size)));
            }
            return list;
        }
    }

    internal static class Bytes
    {
        public static int I32(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b.Slice(o, 4));

        public static string CStr(ReadOnlySpan<byte> b, int o, int len)
        {
            var s = b.Slice(o, len);
            int z = s.IndexOf((byte)0);
            if (z >= 0) s = s.Slice(0, z);
            return Encoding.Latin1.GetString(s);
        }

        /// <summary>Decodes the session-info YAML (Latin1, null-terminated).</summary>
        public static string SessionString(ReadOnlySpan<byte> b)
        {
            int z = b.IndexOf((byte)0);
            if (z >= 0) b = b.Slice(0, z);
            return Encoding.Latin1.GetString(b);
        }
    }
}
