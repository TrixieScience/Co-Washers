using System;
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
    private Pose? previous;
    private Pose? current;
    private float receivedAt;
    private string dropped = string.Empty;   // why the last pose was not used, logged once per reason
    private float nextSend;
    private SkinnedMeshRenderer? bonesOf;
    private Transform[] bones = Array.Empty<Transform>();
    internal bool HasPose => current != null;
    internal DragonPoseSync(CoopPlugin owner, CoopWorld world) { this.owner = owner; this.world = world; }

    internal void Clear() { previous = null; current = null; nextSend = 0; bonesOf = null; }

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
            nextSend = Time.unscaledTime + 1f / 15f;
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
        if (15 + 4 + 28 + 29 + 2 + bones.Length * 28 + 2 + shapes * 4 > Envelope.MaxPacketBytes) return;
        var body = dragon.animator != null ? dragon.animator.transform : null;
        owner.Session.Broadcast(PacketKind.DragonPose, world.Epoch, w =>
        {
            w.Write(world.CurrentLevel);
            w.Write(dragon.gameObject.transform.position);
            w.Write(dragon.gameObject.transform.rotation);
            w.Write(body != null);
            if (body != null) { w.Write(body.position); w.Write(body.rotation); }
            w.Write((ushort)bones.Length);
            foreach (var bone in bones)
            {
                w.Write(bone != null ? bone.localPosition : Vector3.zero);
                w.Write(bone != null ? bone.localRotation : Quaternion.identity);
            }
            w.Write((ushort)shapes);
            for (int i = 0; i < shapes; i++) w.Write(dragon.skin.GetBlendShapeWeight(i));
        }, false);
    }

    internal void Receive(BinaryReader reader)
    {
        if (!world.IsGuest) return;
        int level = reader.ReadInt32();
        if (level != world.CurrentLevel) { Dropped($"the host is on level {level}, this copy on {world.CurrentLevel}"); return; }
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
            pose.Positions[i] = reader.ReadVector3();
            pose.Rotations[i] = reader.ReadQuaternion();
        }
        int shapeCount = reader.ReadUInt16();
        if (shapeCount > 256) throw new InvalidDataException("Dragon pose has too many shapes");
        pose.Shapes = new float[shapeCount];
        for (int i = 0; i < shapeCount; i++) pose.Shapes[i] = reader.ReadSingle();
        previous = current ?? pose;
        current = pose;
        receivedAt = Time.realtimeSinceStartup;
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
        var a = previous ?? current;
        float t = Mathf.Clamp01((Time.realtimeSinceStartup - receivedAt) * 15f);
        dragon.gameObject.transform.position = Vector3.Lerp(a.Root, current.Root, t);
        dragon.gameObject.transform.rotation = Quaternion.Slerp(a.RootRotation, current.RootRotation, t);
        if (dragon.animator != null && a.HasBody && current.HasBody)
            dragon.animator.transform.SetPositionAndRotation(Vector3.Lerp(a.Body, current.Body, t),
                Quaternion.Slerp(a.BodyRotation, current.BodyRotation, t));
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] == null) continue;
            bones[i].localPosition = Vector3.Lerp(a.Positions[i], current.Positions[i], t);
            bones[i].localRotation = Quaternion.Slerp(a.Rotations[i], current.Rotations[i], t);
        }
        int shapes = Math.Min(dragon.skin.sharedMesh?.blendShapeCount ?? 0, current.Shapes.Length);
        for (int i = 0; i < shapes; i++)
            dragon.skin.SetBlendShapeWeight(i, Mathf.Lerp(a.Shapes[i], current.Shapes[i], t));
    }

    internal void Dropped(string why)
    {
        if (dropped == why) return;
        dropped = why;
        owner.Log.LogWarning($"Not using Ryan's pose from the host: {why}");
    }
}
