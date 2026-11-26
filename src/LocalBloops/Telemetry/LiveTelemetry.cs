using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace LocalBloops.Telemetry
{
    /// <summary>
    /// Reads the live iRacing shared-memory telemetry on a background thread.
    /// All events are raised on that thread.
    /// </summary>
    public sealed class LiveTelemetry : IDisposable
    {
        private const string MapName = @"Local\IRSDKMemMapFileName";
        private const string EventName = @"Local\IRSDKDataValidEvent";

        private readonly ManualResetEvent _stopEvent = new ManualResetEvent(false);
        private Thread? _thread;
        private volatile bool _stop;
        private volatile bool _connected;

        public event Action<TelemetryFrame>? FrameReceived;
        public event Action<SessionInfo>? SessionInfoChanged;
        public event Action<bool>? ConnectionChanged;

        public bool IsConnected => _connected;

        public void Start()
        {
            if (_thread != null) return;
            _thread = new Thread(Run) { IsBackground = true, Name = "irsdk reader" };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop = true;
            _stopEvent.Set();
            _thread?.Join(2000);
            _stopEvent.Dispose();
        }

        private void Run()
        {
            while (!_stop)
            {
                MemoryMappedFile? mmf = null;
                MemoryMappedViewAccessor? view = null;
                WaitHandle? dataValid = null;
                try
                {
                    mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
                    view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                    dataValid = OpenDataValidEvent();
                    RunConnected(view, dataValid);
                }
                catch (FileNotFoundException)
                {
                    // Sim not running.
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("LiveTelemetry: " + ex);
                }
                finally
                {
                    view?.Dispose();
                    mmf?.Dispose();
                    dataValid?.Dispose();
                    SetConnected(false);
                }

                if (!_stop) _stopEvent.WaitOne(1000);
            }
        }

        private void RunConnected(MemoryMappedViewAccessor view, WaitHandle? dataValid)
        {
            var headerBuf = new byte[IrsdkHeader.Size];
            var waits = dataValid != null ? new[] { dataValid, _stopEvent } : null;
            byte[] rec = Array.Empty<byte>();
            VarLayout? layout = null;
            int layoutNumVars = -1, layoutOffset = -1;
            int lastSessionUpdate = int.MinValue;
            int lastTick = int.MinValue;

            while (!_stop)
            {
                if (waits != null) WaitHandle.WaitAny(waits, 200);
                else _stopEvent.WaitOne(16);
                if (_stop) return;

                view.ReadArray(0, headerBuf, 0, headerBuf.Length);
                var h = IrsdkHeader.Parse(headerBuf);
                if (!h.IsConnected) return; // caller disposes and retries in 1s
                SetConnected(true);

                if (layout == null || h.NumVars != layoutNumVars || h.VarHeaderOffset != layoutOffset)
                {
                    var vh = new byte[Math.Max(0, h.NumVars) * IrsdkVarHeader.Size];
                    view.ReadArray(h.VarHeaderOffset, vh, 0, vh.Length);
                    layout = VarLayout.Build(IrsdkVarHeader.ParseAll(vh, h.NumVars));
                    layoutNumVars = h.NumVars;
                    layoutOffset = h.VarHeaderOffset;
                }

                if (h.SessionInfoUpdate != lastSessionUpdate && h.SessionInfoLen > 0)
                {
                    var sb = new byte[h.SessionInfoLen];
                    view.ReadArray(h.SessionInfoOffset, sb, 0, sb.Length);
                    lastSessionUpdate = h.SessionInfoUpdate;
                    SessionInfoChanged?.Invoke(SessionInfo.Parse(Bytes.SessionString(sb)));
                }

                if (h.BufLen <= 0) continue;
                if (rec.Length != h.BufLen) rec = new byte[h.BufLen];

                // Copy the newest buffer, then make sure the sim didn't overwrite it mid-copy.
                bool ok = false;
                int tick = 0;
                for (int attempt = 0; attempt < 3 && !ok; attempt++)
                {
                    int i = h.NewestBufferIndex();
                    tick = h.VarBufs[i].TickCount;
                    view.ReadArray(h.VarBufs[i].BufOffset, rec, 0, h.BufLen);
                    int tickAfter = view.ReadInt32(IrsdkHeader.VarBufOffset + i * IrsdkHeader.VarBufSize);
                    if (tickAfter == tick)
                    {
                        ok = true;
                    }
                    else
                    {
                        view.ReadArray(0, headerBuf, 0, headerBuf.Length);
                        h = IrsdkHeader.Parse(headerBuf);
                    }
                }

                if (!ok || tick == lastTick) continue;
                lastTick = tick;
                FrameReceived?.Invoke(layout.Read(rec));
            }
        }

        private void SetConnected(bool connected)
        {
            if (_connected == connected) return;
            _connected = connected;
            ConnectionChanged?.Invoke(connected);
        }

        private static WaitHandle? OpenDataValidEvent()
        {
            IntPtr h = OpenEventW(Synchronize, false, EventName);
            if (h == IntPtr.Zero) return null;
            return new NativeWaitHandle(new SafeWaitHandle(h, ownsHandle: true));
        }

        private const uint Synchronize = 0x00100000;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenEventW(uint dwDesiredAccess, bool bInheritHandle, string lpName);

        private sealed class NativeWaitHandle : WaitHandle
        {
            public NativeWaitHandle(SafeWaitHandle handle)
            {
                SafeWaitHandle = handle;
            }
        }
    }
}
