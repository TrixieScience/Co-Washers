using System;
using System.Collections.Generic;
using UnityEngine;

namespace DragNWashCoop;

/// <summary>One received pose of another player, stamped with the sender's clock.</summary>
internal struct PoseSnapshot
{
    internal double SenderTime;
    internal Vector3 Head;
    internal Quaternion HeadRotation;
    internal Vector3 Hand;
    internal Quaternion HandRotation;
    internal Vector3 Tool;
    internal Quaternion ToolRotation;
    internal bool HasFeet;
    internal Vector3 Feet;
    internal Quaternion FeetRotation;
    internal bool HoldingTool;

    /// <summary>Catmull-Rom between b and c (with neighbours a, d): position and velocity are continuous.</summary>
    internal static Vector3 CatmullRom(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return .5f * (2f * b + (c - a) * t + (2f * a - 5f * b + 4f * c - d) * t2 + (3f * b - a - 3f * c + d) * t3);
    }

    internal static PoseSnapshot Smooth(in PoseSnapshot a, in PoseSnapshot b, in PoseSnapshot c, in PoseSnapshot d, float t)
    {
        var p = Lerp(b, c, t);
        p.Head = CatmullRom(a.Head, b.Head, c.Head, d.Head, t);
        p.Hand = CatmullRom(a.Hand, b.Hand, c.Hand, d.Hand, t);
        p.Tool = CatmullRom(a.Tool, b.Tool, c.Tool, d.Tool, t);
        p.Feet = CatmullRom(a.Feet, b.Feet, c.Feet, d.Feet, t);
        return p;
    }

    internal static PoseSnapshot Lerp(in PoseSnapshot a, in PoseSnapshot b, float t)
    {
        return new PoseSnapshot
        {
            SenderTime = a.SenderTime + (b.SenderTime - a.SenderTime) * t,
            Head = Vector3.LerpUnclamped(a.Head, b.Head, t),
            HeadRotation = Quaternion.Slerp(a.HeadRotation, b.HeadRotation, t),
            Hand = Vector3.LerpUnclamped(a.Hand, b.Hand, t),
            HandRotation = Quaternion.Slerp(a.HandRotation, b.HandRotation, t),
            Tool = Vector3.LerpUnclamped(a.Tool, b.Tool, t),
            ToolRotation = Quaternion.Slerp(a.ToolRotation, b.ToolRotation, t),
            HasFeet = t < .5f ? a.HasFeet : b.HasFeet,
            Feet = Vector3.LerpUnclamped(a.Feet, b.Feet, t),
            FeetRotation = Quaternion.Slerp(a.FeetRotation, b.FeetRotation, t),
            HoldingTool = t < .5f ? a.HoldingTool : b.HoldingTool,
        };
    }
}

/// <summary>
/// Snapshot interpolation: remote players are drawn a little in the past (<see cref="Delay"/>), between the two
/// snapshots around that moment, so their motion stays smooth however unevenly the packets arrive. The sender's
/// clock is mapped to ours by the smallest (arrival - sent) offset seen recently, which ignores queueing delay.
/// </summary>
internal sealed class SnapshotBuffer
{
    internal const float Delay = .12f;          // two to three 20 Hz packets of slack
    private const int Capacity = 32;
    private readonly List<PoseSnapshot> snapshots = new(Capacity);
    private double offset = double.NaN;         // our clock - their clock (+ the fastest one-way trip)
    private double offsetAge;

    internal int Count => snapshots.Count;
    /// <summary>Last sample: seconds of buffered future left (negative = extrapolating past the newest snapshot).</summary>
    internal double LastMargin;
    internal PoseSnapshot Latest => snapshots[snapshots.Count - 1];

    internal void Add(PoseSnapshot snapshot, double now)
    {
        if (snapshots.Count > 0 && snapshot.SenderTime <= snapshots[snapshots.Count - 1].SenderTime) return;  // late / duplicate
        double seen = now - snapshot.SenderTime;
        // take the smallest offset (least delayed packet); let it drift up slowly so a changed route is followed
        if (double.IsNaN(offset) || seen < offset) offset = seen;
        else offset += Math.Min(seen - offset, (now - offsetAge) * .02);
        offsetAge = now;
        snapshots.Add(snapshot);
        if (snapshots.Count > Capacity) snapshots.RemoveAt(0);
    }

    /// <summary>The pose at our time <paramref name="now"/> (interpolated; briefly extrapolated if packets stall).</summary>
    internal bool Sample(double now, out PoseSnapshot pose)
    {
        pose = default;
        if (snapshots.Count == 0) return false;
        double t = now - offset - Delay;
        LastMargin = snapshots[snapshots.Count - 1].SenderTime - t;
        if (snapshots.Count == 1 || t <= snapshots[0].SenderTime) { pose = snapshots[0]; return true; }
        for (int i = snapshots.Count - 1; i > 0; i--)
        {
            var a = snapshots[i - 1];
            var b = snapshots[i];
            if (t >= a.SenderTime)
            {
                double span = Math.Max(b.SenderTime - a.SenderTime, 1e-4);
                // past the newest snapshot: carry on its motion for up to 1/4 s, then hold
                float u = (float)Math.Min((t - a.SenderTime) / span, 1.0 + Math.Min(.25, t - b.SenderTime) / span);
                if (u <= 1f && i >= 2 && i + 1 < snapshots.Count && !Jump(snapshots[i - 2], a) && !Jump(b, snapshots[i + 1]) && !Jump(a, b))
                    pose = PoseSnapshot.Smooth(snapshots[i - 2], a, b, snapshots[i + 1], u);
                else
                    pose = PoseSnapshot.Lerp(a, b, u);
                return true;
            }
        }
        pose = snapshots[0];
        return true;
    }

    internal void Clear() { snapshots.Clear(); offset = double.NaN; }

    /// <summary>A teleport between two snapshots (level change, respawn): don't curve through it.</summary>
    private static bool Jump(in PoseSnapshot a, in PoseSnapshot b) => (a.Head - b.Head).sqrMagnitude > 4f;
}

/// <summary>Small procedural-animation helpers.</summary>
internal static class Motion
{
    /// <summary>Frame-rate independent exponential approach (sharpness ~ 1/seconds).</summary>
    internal static float Damp(float sharpness, float dt) => 1f - Mathf.Exp(-sharpness * dt);

    /// <summary>
    /// Two-bone IK on three transforms (upper, lower, end). Rotates upper and lower so end reaches target (clamped
    /// to reach), bending toward pole. Works on the transforms' current world rotations, so it layers on top of
    /// whatever pose they already have.
    /// </summary>
    internal static void TwoBone(Transform upper, Transform lower, Transform end, Vector3 target, Vector3 pole, float weight = 1f)
    {
        if (weight <= 0f) return;
        Vector3 a = upper.position, b = lower.position, c = end.position;
        float lab = (b - a).magnitude, lcb = (b - c).magnitude;
        if (lab < 1e-5f || lcb < 1e-5f) return;
        target = Vector3.Lerp(c, target, weight);
        Vector3 at = target - a;
        float lat = Mathf.Clamp(at.magnitude, Mathf.Abs(lab - lcb) + 1e-4f, lab + lcb - 1e-4f);
        // current and wanted interior angles
        float ac_ab0 = Mathf.Acos(Mathf.Clamp(Vector3.Dot((c - a).normalized, (b - a).normalized), -1f, 1f));
        float ba_bc0 = Mathf.Acos(Mathf.Clamp(Vector3.Dot((a - b).normalized, (c - b).normalized), -1f, 1f));
        float ac_ab1 = Mathf.Acos(Mathf.Clamp((lcb * lcb - lab * lab - lat * lat) / (-2f * lab * lat), -1f, 1f));
        float ba_bc1 = Mathf.Acos(Mathf.Clamp((lat * lat - lab * lab - lcb * lcb) / (-2f * lab * lcb), -1f, 1f));
        // bend plane from the pole
        Vector3 axis0 = Vector3.Cross(c - a, b - a);
        Vector3 toPole = pole - a;
        Vector3 axisP = Vector3.Cross(at, toPole);
        Vector3 axis = axisP.sqrMagnitude > 1e-8f ? axisP.normalized : (axis0.sqrMagnitude > 1e-8f ? axis0.normalized : Vector3.right);
        Vector3 axisA = axis0.sqrMagnitude > 1e-8f ? axis0.normalized : axis;
        // open/close the elbow, then swing the chain onto the target, then twist it into the pole plane
        var r0 = Quaternion.AngleAxis((ac_ab1 - ac_ab0) * Mathf.Rad2Deg, axisA);
        var r1 = Quaternion.AngleAxis((ba_bc1 - ba_bc0) * Mathf.Rad2Deg, axisA);
        upper.rotation = r0 * upper.rotation;
        lower.rotation = r1 * lower.rotation;
        c = end.position;
        upper.rotation = Quaternion.FromToRotation(c - a, target - a) * upper.rotation;
        // twist around the a->target axis so the elbow/knee points at the pole
        b = lower.position;
        Vector3 dir = (target - a).normalized;
        Vector3 bPerp = Vector3.ProjectOnPlane(b - a, dir);
        Vector3 pPerp = Vector3.ProjectOnPlane(toPole, dir);
        if (bPerp.sqrMagnitude > 1e-8f && pPerp.sqrMagnitude > 1e-8f)
            upper.rotation = Quaternion.AngleAxis(Vector3.SignedAngle(bPerp, pPerp, dir), dir) * upper.rotation;
    }

    /// <summary>A critically-damped-ish spring on an angle vector (degrees), for ears, tails and wobble.</summary>
    internal struct Spring
    {
        internal Vector3 Value, Velocity;
        internal void Step(Vector3 target, float stiffness, float damping, float dt)
        {
            dt = Mathf.Min(dt, 1f / 30f);
            Vector3 accel = (target - Value) * stiffness - Velocity * damping;
            Velocity += accel * dt;
            Value += Velocity * dt;
        }
    }
}

/// <summary>
/// Procedural stepping for one foot: it stays planted until its ideal spot (under the hip, a little ahead when
/// moving) drifts too far, then swings there in an arc. Only one foot swings at a time.
/// </summary>
internal sealed class FootStepper
{
    internal Vector3 Planted;           // where the foot is on the ground
    internal Vector3 From, To;
    internal float Progress = 1f;       // 1 = planted
    internal float Duration = .3f;
    internal bool Swinging => Progress < 1f;

    internal void Reset(Vector3 at) { Planted = From = To = at; Progress = 1f; }

    /// <summary>Start a step to <paramref name="target"/> if it's far enough and the other foot is down.</summary>
    internal bool TryStep(Vector3 target, float threshold, bool otherSwinging, float duration)
    {
        if (Swinging || otherSwinging) return false;
        if ((Vector3.ProjectOnPlane(target - Planted, Vector3.up)).magnitude < threshold) return false;
        From = Planted; To = target; Progress = 0f; Duration = duration;
        return true;
    }

    /// <summary>Advance; returns the foot position and how high it is lifted (0..1 of the arc).</summary>
    internal Vector3 Tick(float dt, float lift, out float arc)
    {
        if (Swinging)
        {
            Progress = Mathf.Min(1f, Progress + dt / Mathf.Max(Duration, .05f));
            float s = Progress * Progress * (3f - 2f * Progress);
            Planted = Vector3.Lerp(From, To, s);
            arc = Mathf.Sin(Progress * Mathf.PI);
            return Planted + Vector3.up * (arc * lift);
        }
        arc = 0f;
        return Planted;
    }
}
