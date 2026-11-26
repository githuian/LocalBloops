using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace LocalBloops.Telemetry
{
    /// <summary>irsdk_TrkLoc values for PlayerTrackSurface.</summary>
    public static class TrackSurface
    {
        public const int NotInWorld = -1;
        public const int OffTrack = 0;
        public const int InPitStall = 1;
        public const int ApproachingPits = 2;
        public const int OnTrack = 3;
    }

    /// <summary>The subset of telemetry the overlay and lap recorder care about.</summary>
    public struct TelemetryFrame
    {
        public double SessionTime;
        public int Lap;
        public float LapDistPct;
        public float LapDist;
        public float Speed;      // m/s
        public float Throttle;   // 0..1
        public float Brake;      // 0..1
        public int Gear;
        public float Steer;      // rad
        public bool OnPitRoad;
        public int TrackSurface;
        public bool IsOnTrack;
    }

    public readonly struct VarAccessor
    {
        public static readonly VarAccessor Missing = new VarAccessor(-1, IrsdkVarType.Float);

        public readonly int Offset;
        public readonly IrsdkVarType Type;

        public VarAccessor(int offset, IrsdkVarType type)
        {
            Offset = offset;
            Type = type;
        }

        public bool Exists => Offset >= 0;

        public double Read(ReadOnlySpan<byte> rec, double fallback)
        {
            if (Offset < 0) return fallback;
            int size = Type switch
            {
                IrsdkVarType.Char or IrsdkVarType.Bool => 1,
                IrsdkVarType.Double => 8,
                _ => 4,
            };
            if (Offset + size > rec.Length) return fallback;
            var s = rec.Slice(Offset, size);
            return Type switch
            {
                IrsdkVarType.Char or IrsdkVarType.Bool => s[0],
                IrsdkVarType.Int or IrsdkVarType.BitField => BinaryPrimitives.ReadInt32LittleEndian(s),
                IrsdkVarType.Float => BinaryPrimitives.ReadSingleLittleEndian(s),
                IrsdkVarType.Double => BinaryPrimitives.ReadDoubleLittleEndian(s),
                _ => fallback,
            };
        }
    }

    /// <summary>Resolves var offsets once per connection/file and decodes records into frames.</summary>
    public sealed class VarLayout
    {
        public VarAccessor SessionTime, Lap, LapDistPct, LapDist, Speed, Throttle, Brake, Gear,
            SteeringWheelAngle, OnPitRoad, PlayerTrackSurface, IsOnTrack;

        public static VarLayout Build(IEnumerable<IrsdkVarHeader> headers)
        {
            var map = new Dictionary<string, IrsdkVarHeader>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in headers) map[h.Name] = h;

            VarAccessor Get(string name) =>
                map.TryGetValue(name, out var h) ? new VarAccessor(h.Offset, h.Type) : VarAccessor.Missing;

            return new VarLayout
            {
                SessionTime = Get("SessionTime"),
                Lap = Get("Lap"),
                LapDistPct = Get("LapDistPct"),
                LapDist = Get("LapDist"),
                Speed = Get("Speed"),
                Throttle = Get("Throttle"),
                Brake = Get("Brake"),
                Gear = Get("Gear"),
                SteeringWheelAngle = Get("SteeringWheelAngle"),
                OnPitRoad = Get("OnPitRoad"),
                PlayerTrackSurface = Get("PlayerTrackSurface"),
                IsOnTrack = Get("IsOnTrack"),
            };
        }

        public TelemetryFrame Read(ReadOnlySpan<byte> rec)
        {
            return new TelemetryFrame
            {
                SessionTime = SessionTime.Read(rec, 0),
                Lap = (int)Lap.Read(rec, 0),
                LapDistPct = (float)LapDistPct.Read(rec, -1),
                LapDist = (float)LapDist.Read(rec, 0),
                Speed = (float)Speed.Read(rec, 0),
                Throttle = (float)Throttle.Read(rec, 0),
                Brake = (float)Brake.Read(rec, 0),
                Gear = (int)Gear.Read(rec, 0),
                Steer = (float)SteeringWheelAngle.Read(rec, 0),
                OnPitRoad = OnPitRoad.Read(rec, 0) != 0,
                // Missing vars default to "fine" so older files still produce laps.
                TrackSurface = (int)PlayerTrackSurface.Read(rec, TrackSurfaceDefault),
                IsOnTrack = IsOnTrack.Read(rec, 1) != 0,
            };
        }

        private const int TrackSurfaceDefault = Telemetry.TrackSurface.OnTrack;
    }
}
