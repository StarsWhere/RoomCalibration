using System;
using BeatSaber.Settings;
using Unity.Mathematics;

namespace RoomCalibration
{
    public enum ControllerButton { StickClick, Grip, Trigger, Primary, Secondary, Disabled }
    public enum KeyboardShortcut { Disabled, F6, F7, F8, F9, F10, F11, F12 }
    internal static class InputSchema
    {
        internal static float Duration(float value)
        {
            return Geometry.Finite(value) ? Math.Max(0.5f,Math.Min(5f,value)) : 1.5f;
        }
    }
    // RoomSettings remains the shared representation of every room transform.
    internal static class Geometry
    {
        internal static bool Finite(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }
        internal static bool Valid(RoomSettings room)
        {
            return Finite(room.rotation) && Finite(room.center.x) && Finite(room.center.y) && Finite(room.center.z);
        }
        internal static float Wrap(float degrees)
        {
            double value = ((double)degrees + 180.0) % 360.0;
            if (value < 0) value += 360.0;
            return (float)(value - 180.0);
        }
        internal static float3 Rotate(float3 point, float degrees)
        {
            double angle = degrees * Math.PI / 180.0;
            double cosine = Math.Cos(angle), sine = Math.Sin(angle);
            return new float3((float)(cosine * point.x + sine * point.z), point.y,
                (float)(-sine * point.x + cosine * point.z));
        }
        internal static RoomSettings Compose(RoomSettings outer, RoomSettings inner)
        {
            float3 rotated = Rotate(inner.center, outer.rotation);
            return new RoomSettings {
                center = new float3(rotated.x + outer.center.x, rotated.y + outer.center.y, rotated.z + outer.center.z),
                rotation = Wrap(outer.rotation + inner.rotation)
            };
        }
        internal static RoomSettings Inverse(RoomSettings room)
        {
            return new RoomSettings {
                center = Rotate(new float3(-room.center.x, -room.center.y, -room.center.z), -room.rotation),
                rotation = Wrap(-room.rotation)
            };
        }
        internal static bool TryCalibrate(RoomSettings current, float3 position, float4 orientation,
            float targetYaw, out RoomSettings result, out string error)
        {
            result = current;
            error = null;
            if (!Valid(current) || !Finite(position.x) || !Finite(position.y) || !Finite(position.z)
                || !Finite(orientation.x) || !Finite(orientation.y) || !Finite(orientation.z)
                || !Finite(orientation.w) || !Finite(targetYaw)) {
                error = "Invalid headset pose; try again";
                return false;
            }
            double length = (double)orientation.x * orientation.x + (double)orientation.y * orientation.y
                + (double)orientation.z * orientation.z + (double)orientation.w * orientation.w;
            if (length < 0.000001) { error = "Invalid headset orientation; try again"; return false; }
            double x = 2.0 * (orientation.x * (double)orientation.z + orientation.w * (double)orientation.y) / length;
            double z = 1.0 - 2.0 * (orientation.x * (double)orientation.x + orientation.y * (double)orientation.y) / length;
            if (x * x + z * z < 0.01) {
                error = "Look straight ahead and try again";
                return false;
            }
            float yaw = (float)(Math.Atan2(x, z) * 180.0 / Math.PI);
            result.rotation = Wrap(targetYaw - yaw);
            float3 rotated = Rotate(position, result.rotation);
            result.center = new float3(-rotated.x, current.center.y, -rotated.z);
            if (!Valid(result)) { result = current; error = "Invalid calibration offset; try again"; return false; }
            return true;
        }
    }

    internal struct SessionSnapshot
    {
        internal RoomSettings Basis, Correction;
        internal bool Active, NeedsCalibration;
    }

    internal sealed class SessionTransform
    {
        readonly object sync = new object();
        SessionSnapshot state;
        internal bool Active { get { lock (sync) return state.Active; } }
        internal bool NeedsCalibration { get { lock (sync) return state.NeedsCalibration; } }
        internal SessionSnapshot Snapshot { get { lock (sync) return state; } }
        internal void Restore(SessionSnapshot value) { lock (sync) state = value; }
        internal RoomSettings Attach(RoomSettings basis)
        {
            lock (sync) {
                state.Basis = basis;
                return state.Active ? Geometry.Compose(state.Correction, basis) : basis;
            }
        }
        internal RoomSettings PersistentRoom(RoomSettings effective)
        {
            lock (sync) return state.Active ? Geometry.Compose(Geometry.Inverse(state.Correction), effective) : effective;
        }
        internal void Observe(RoomSettings effective)
        {
            lock (sync) state.Basis = state.Active
                ? Geometry.Compose(Geometry.Inverse(state.Correction), effective) : effective;
        }
        internal void Commit(RoomSettings effective, RoomSettings previous)
        {
            lock (sync) {
                RoomSettings basis = state.Active
                    ? Geometry.Compose(Geometry.Inverse(state.Correction), previous) : previous;
                state.Basis = basis;
                state.Correction = Geometry.Compose(effective, Geometry.Inverse(basis));
                state.Active = true;
                state.NeedsCalibration = false;
            }
        }
        internal RoomSettings Clear(RoomSettings effective)
        {
            lock (sync) {
                RoomSettings basis = state.Active
                    ? Geometry.Compose(Geometry.Inverse(state.Correction), effective) : effective;
                state = new SessionSnapshot { Basis = basis };
                return basis;
            }
        }
        internal void NativeReset() { lock (sync) state = new SessionSnapshot(); }
        internal void OriginChanged() { lock (sync) { if (state.Active) state.NeedsCalibration = true; } }
    }

    internal sealed class StartupCalibrationGate
    {
        internal bool Pending { get; private set; }
        internal bool MenuPresented { get; private set; }
        internal void Begin(bool enabled) { Pending = enabled; MenuPresented = false; }
        internal void Complete() { Pending = false; }
        internal bool BeforeFirstMenuFrame()
        {
            if (MenuPresented) return false;
            MenuPresented = true;
            bool apply = Pending;
            Complete();
            return apply;
        }
    }

    internal sealed class ButtonChord
    {
        bool armed;
        internal void Reset() { armed = false; }
        internal bool Tick(bool left, bool right, bool eligible)
        {
            if (!eligible) { Reset(); return false; }
            if (!left && !right) { armed = true; return false; }
            if (!armed) return false;
            if (!left || !right) return false;
            Reset();
            return true;
        }
    }

    internal enum RequestAction { None, Preparing, Apply, Cancel, Timeout }
    internal sealed class RequestGate
    {
        internal bool Pending { get; private set; }
        internal bool Clear { get; private set; }
        int generation;
        double deadline;
        double duration, readyAt;
        bool preparing;
        internal double Remaining(double now) { return Math.Max(0,readyAt-now); }
        internal bool Begin(bool clear, int contextGeneration, double now, double preparationSeconds = 0)
        {
            if (Pending) return false;
            Pending = true; Clear = clear; generation = contextGeneration; deadline = now + 2.0;
            duration=preparationSeconds; preparing=false;
            return true;
        }
        internal void Cancel() { Pending = false; }
        internal RequestAction Tick(int contextGeneration, bool eligible, bool safeToApply, double now)
        {
            if (!Pending) return RequestAction.None;
            if (contextGeneration != generation || !eligible) { Cancel(); return RequestAction.Cancel; }
            if (!preparing && now >= deadline) { Cancel(); return RequestAction.Timeout; }
            if (!safeToApply) {
                if(preparing) { Cancel(); return RequestAction.Cancel; }
                return RequestAction.None;
            }
            if(!preparing) { preparing=true; readyAt=now+duration; }
            if(now<readyAt) return RequestAction.Preparing;
            Cancel();
            return RequestAction.Apply;
        }
    }
}
