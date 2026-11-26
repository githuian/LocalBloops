using System;
using System.Collections.Generic;
using System.IO;

namespace LocalBloops.Telemetry
{
    /// <summary>
    /// Reads an iRacing .ibt telemetry file: irsdk header, 32-byte disk sub-header,
    /// var headers, session YAML, then fixed-size records starting at varBuf[0].bufOffset.
    /// </summary>
    public sealed class IbtFile : IDisposable
    {
        private const int DiskHeaderSize = 32;
        private const int RecordCountOffset = IrsdkHeader.Size + 28;

        private readonly FileStream _fs;

        public string Path { get; }
        public IrsdkHeader Header { get; }
        public SessionInfo Session { get; }
        public VarLayout Layout { get; }
        public int RecordCount { get; }

        private IbtFile(string path, FileStream fs, IrsdkHeader header, SessionInfo session, VarLayout layout, int recordCount)
        {
            Path = path;
            _fs = fs;
            Header = header;
            Session = session;
            Layout = layout;
            RecordCount = recordCount;
        }

        public static IbtFile Open(string path)
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            try
            {
                var head = new byte[IrsdkHeader.Size + DiskHeaderSize];
                ReadExactly(fs, 0, head);
                var header = IrsdkHeader.Parse(head);
                if (header.NumVars <= 0 || header.BufLen <= 0)
                    throw new InvalidDataException("Not an iRacing telemetry file (bad header).");

                var vh = new byte[header.NumVars * IrsdkVarHeader.Size];
                ReadExactly(fs, header.VarHeaderOffset, vh);
                var layout = VarLayout.Build(IrsdkVarHeader.ParseAll(vh, header.NumVars));

                var session = new SessionInfo();
                if (header.SessionInfoLen > 0)
                {
                    var sb = new byte[header.SessionInfoLen];
                    ReadExactly(fs, header.SessionInfoOffset, sb);
                    session = SessionInfo.Parse(Bytes.SessionString(sb));
                }

                long dataStart = header.VarBufs[0].BufOffset;
                long fromLength = Math.Max(0, (fs.Length - dataStart) / header.BufLen);
                int recordCount = Bytes.I32(head, RecordCountOffset);
                if (recordCount <= 0 || recordCount > fromLength) recordCount = (int)Math.Min(int.MaxValue, fromLength);

                return new IbtFile(path, fs, header, session, layout, recordCount);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        public IEnumerable<TelemetryFrame> ReadFrames()
        {
            var rec = new byte[Header.BufLen];
            _fs.Position = Header.VarBufs[0].BufOffset;
            for (int i = 0; i < RecordCount; i++)
            {
                int read = 0;
                while (read < rec.Length)
                {
                    int n = _fs.Read(rec, read, rec.Length - read);
                    if (n <= 0) yield break;
                    read += n;
                }
                yield return Layout.Read(rec);
            }
        }

        public void Dispose() => _fs.Dispose();

        private static void ReadExactly(FileStream fs, long offset, byte[] buffer)
        {
            fs.Position = offset;
            int read = 0;
            while (read < buffer.Length)
            {
                int n = fs.Read(buffer, read, buffer.Length - read);
                if (n <= 0) throw new EndOfStreamException("Unexpected end of .ibt file.");
                read += n;
            }
        }
    }
}
