using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DragNWashCoop;

[DefaultExecutionOrder(32001)]
internal sealed class CoopLatePose : MonoBehaviour
{
    private void LateUpdate()
    {
        if (CoopPlugin.Instance != null) CoopPlugin.Instance.World.LatePoseTick();
    }
}

/// <summary>
/// Guests also apply the dragon pose before everything else in LateUpdate: the game bakes Ryan's
/// collider and places his attachments (boots, sticks, bandage spots) from the bones during LateUpdate.
/// </summary>
[DefaultExecutionOrder(-32000)]
internal sealed class CoopEarlyPose : MonoBehaviour
{
    private void LateUpdate()
    {
        if (CoopPlugin.Instance != null) CoopPlugin.Instance.World.EarlyPoseTick();
    }
}

/// <summary>Copies the evaluated dragon skeleton after Animator/IK, not just its Animator state.</summary>
internal sealed class DragonPoseSync
{
    private sealed class Pose
    {
        internal Vector3 Root;
        internal Quaternion RootRotation;
        // The game walks the dragon by moving its Animator's object (root motion), a child of the root.
        internal bool HasBody;
        internal Vector3 Body;
        internal Quaternion BodyRotation;
        internal Vector3[] Positions = Array.Empty<Vector3>();
        internal Quaternion[] Rotations = Array.Empty<Quaternion>();
        internal float[] Shapes = Array.Empty<float>();
    }

    private readonly CoopPlugin owner;
    private readonly CoopWorld world;
    // poses as they arrive, stamped with the host's clock: Ryan is drawn a moment in the past, between the two around
    // that moment, so a lost or late pose (they're sent unreliably over the internet) doesn't make him freeze and jump
    private readonly List<(double At, Pose Pose)> poses = new();
    private double offset = double.NaN, offsetAge;   // our clock - the host's (the fastest trip seen)
    private const double Delay = .18;                // a few poses of slack: a lost pose or two plus the internet's jitter
    private Pose? current;                           // the newest pose
    private string dropped = string.Empty;   // why the last pose was not used, logged once per reason
    private float nextSend;
    private SkinnedMeshRenderer? bonesOf;
    private Transform[] bones = Array.Empty<Transform>();
    internal bool HasPose => current != null;
    internal DragonPoseSync(CoopPlugin owner, CoopWorld world) { this.owner = owner; this.world = world; }

    internal void Clear() { poses.Clear(); offset = double.NaN; current = null; nextSend = 0; bonesOf = null; }

    /// <summary>skin.bones allocates a new array on every call.</summary>
    private Transform[] BonesOf(SkinnedMeshRenderer skin)
    {
        if (bonesOf != skin) { bonesOf = skin; bones = skin.bones; }
        return bones;
    }

    internal void EarlyTick()
    {
        if (world.IsGuest && current != null) Apply();
    }

    internal void LateTick()
    {
        if (world.IsHost && Time.unscaledTime >= nextSend)
        {
            nextSend = Time.unscaledTime + 1f / 20f;
            Send();
        }
        if (world.IsGuest && current != null) Apply();
    }

    private void Send()
    {
        if (!WalkNWashSceneState.TryGetActiveDragon(out var dragon) || dragon.skin == null) return;
        var bones = BonesOf(dragon.skin);
        if (bones.Length > 1200) return;
        int shapes = Math.Min(dragon.skin.sharedMesh?.blendShapeCount ?? 0, 256);
        if (15 + 12 + 28 + 29 + 2 + bones.Length * 14 + 2 + shapes > Envelope.MaxPacketBytes) return;
        var body = dragon.animator != null ? dragon.animator.transform : null;
        double sentAt = Time.realtimeSinceStartupAsDouble;
        owner.Session.Broadcast(PacketKind.DragonPose, world.Epoch, w =>
        {
            w.Write(world.CurrentLevel);
            w.Write(sentAt);
            w.Write(dragon.gameObject.transform.position);
            w.Write(dragon.gameObject.transform.rotation);
            w.Write(body != null);
            if (body != null) { w.Write(body.position); w.Write(body.rotation); }
            // bones packed small: positions as half floats, rotations as 16-bit parts (14 bytes a bone instead of 28)
            w.Write((ushort)bones.Length);
            foreach (var bone in bones)
            {
                var p = bone != null ? bone.localPosition : Vector3.zero;
                var q = bone != null ? bone.localRotation : Quaternion.identity;
                w.Write(Mathf.FloatToHalf(p.x)); w.Write(Mathf.FloatToHalf(p.y)); w.Write(Mathf.FloatToHalf(p.z));
                w.Write(Pack(q.x)); w.Write(Pack(q.y)); w.Write(Pack(q.z)); w.Write(Pack(q.w));
            }
            w.Write((ushort)shapes);   // blend shape weights (0..100) as a byte each
            for (int i = 0; i < shapes; i++) w.Write((byte)Mathf.Clamp(Mathf.RoundToInt(dragon.skin.GetBlendShapeWeight(i) * 2.55f), 0, 255));
        }, false);
    }

    internal void Receive(BinaryReader reader)
    {
        if (!world.IsGuest) return;
        int level = reader.ReadInt32();
        if (level != world.CurrentLevel) { Dropped($"the host is on level {level}, this copy on {world.CurrentLevel}"); return; }
        double sentAt = reader.ReadDouble();
        var pose = new Pose { Root = reader.ReadVector3(), RootRotation = reader.ReadQuaternion() };
        pose.HasBody = reader.ReadBoolean();
        if (pose.HasBody)
        {
            pose.Body = reader.ReadVector3();
            pose.BodyRotation = reader.ReadQuaternion();
        }
        int boneCount = reader.ReadUInt16();
        if (boneCount > 1200) throw new InvalidDataException("Dragon pose has too many bones");
        pose.Positions = new Vector3[boneCount];
        pose.Rotations = new Quaternion[boneCount];
        for (int i = 0; i < boneCount; i++)
        {
            pose.Positions[i] = new Vector3(Mathf.HalfToFloat(reader.ReadUInt16()), Mathf.HalfToFloat(reader.ReadUInt16()), Mathf.HalfToFloat(reader.ReadUInt16()));
            var q = new Quaternion(Unpack(reader.ReadInt16()), Unpack(reader.ReadInt16()), Unpack(reader.ReadInt16()), Unpack(reader.ReadInt16()));
            pose.Rotations[i] = q.normalized;
        }
        int shapeCount = reader.ReadUInt16();
        if (shapeCount > 256) throw new InvalidDataException("Dragon pose has too many shapes");
        pose.Shapes = new float[shapeCount];
        for (int i = 0; i < shapeCount; i++) pose.Shapes[i] = reader.ReadByte() / 2.55f;
        double now = Time.realtimeSinceStartupAsDouble;
        if (poses.Count > 0 && sentAt <= poses[poses.Count - 1].At) return;   // late or repeated
        double seen = now - sentAt;
        // the smallest offset seen is the least-delayed pose; let it drift up slowly so a changed route is followed
        if (double.IsNaN(offset) || seen < offset) offset = seen;
        else offset += Math.Min(seen - offset, (now - offsetAge) * .02);
        offsetAge = now;
        poses.Add((sentAt, pose));
        if (poses.Count > 16) poses.RemoveAt(0);
        current = pose;
    }

    private static short Pack(float part) => (short)Mathf.Clamp(Mathf.RoundToInt(part * 32767f), -32767, 32767);
    private static float Unpack(short part) => part / 32767f;

    /// <summary>The two poses around a moment a little in the past, and how far between them it is.</summary>
    private bool Sample(out Pose a, out Pose b, out float u)
    {
        a = b = null!; u = 0f;
        if (poses.Count == 0) return false;
        double t = Time.realtimeSinceStartupAsDouble - offset - Delay;
        if (t <= poses[0].At) { a = b = poses[0].Pose; return true; }
        for (int i = 1; i < poses.Count; i++)
            if (t < poses[i].At)
            {
                a = poses[i - 1].Pose; b = poses[i].Pose;
                u = (float)((t - poses[i - 1].At) / Math.Max(poses[i].At - poses[i - 1].At, 1e-4));
                return true;
            }
        // no newer pose yet: carry on the last motion for a moment (freezing, then jumping, is what shows), then hold
        if (poses.Count >= 2)
        {
            var (at0, p0) = poses[poses.Count - 2];
            var (at1, p1) = poses[poses.Count - 1];
            double span = Math.Max(at1 - at0, 1e-4);
            a = p0; b = p1;
            u = 1f + (float)(Math.Min(t - at1, .12) / span);
            return true;
        }
        a = b = poses[poses.Count - 1].Pose;
        return true;
    }

    private void Apply()
    {
        if (!WalkNWashSceneState.TryGetActiveDragon(out var dragon) || dragon.skin == null || current == null) return;
        var bones = BonesOf(dragon.skin);
        if (bones.Length != current.Positions.Length)
        {
            Dropped($"the host's Ryan has {current.Positions.Length} bones, this copy's {bones.Length}");
            return;
        }
        if (!Sample(out var a, out var b, out float t)) return;   // b: the later of the two poses around the moment drawn
        if (a.Positions.Length != bones.Length || b.Positions.Length != bones.Length) return;
        dragon.gameObject.transform.position = Vector3.LerpUnclamped(a.Root, b.Root, t);
        dragon.gameObject.transform.rotation = Quaternion.SlerpUnclamped(a.RootRotation, b.RootRotation, t);
        if (dragon.animator != null && a.HasBody && b.HasBody)
            dragon.animator.transform.SetPositionAndRotation(Vector3.LerpUnclamped(a.Body, b.Body, t),
                Quaternion.SlerpUnclamped(a.BodyRotation, b.BodyRotation, t));
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] == null) continue;
            bones[i].localPosition = Vector3.LerpUnclamped(a.Positions[i], b.Positions[i], t);
            bones[i].localRotation = Quaternion.SlerpUnclamped(a.Rotations[i], b.Rotations[i], t);
        }
        int shapes = Math.Min(dragon.skin.sharedMesh?.blendShapeCount ?? 0, b.Shapes.Length);
        for (int i = 0; i < shapes; i++)
            dragon.skin.SetBlendShapeWeight(i, Mathf.LerpUnclamped(a.Shapes[i], b.Shapes[i], t));
    }

    internal void Dropped(string why)
    {
        if (dropped == why) return;
        dropped = why;
        owner.Log.LogWarning($"Not using Ryan's pose from the host: {why}");
    }
}
