using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.VFX;
using com.gatordragongames.washnwalk.tools;

namespace DragNWashCoop;

/// <summary>Where a player's feet are and what their animator is doing (the game's player is a hand and feet).</summary>
internal struct FeetPose
{
    internal const int MaxParameters = 16;
    internal bool Has;
    internal Vector3 Position;
    internal Quaternion Rotation;
    internal float[] Parameters;

    /// <summary>This player's feet: PlayerCharacter/Base/Feet, animated by LocomotionAnimationBridge.</summary>
    private static LocomotionAnimationBridge? bridge;
    private static Animator? animator;

    internal static FeetPose Capture()
    {
        if (bridge == null)
        {
            bridge = UnityEngine.Object.FindFirstObjectByType<LocomotionAnimationBridge>();
            animator = bridge != null ? bridge.GetComponent<Animator>() : null;
        }
        if (animator == null) return new FeetPose { Parameters = Array.Empty<float>() };
        var parameters = animator.parameters;
        var values = new float[Math.Min(parameters.Length, MaxParameters)];
        for (int i = 0; i < values.Length; i++)
            values[i] = parameters[i].type switch
            {
                AnimatorControllerParameterType.Float => animator.GetFloat(parameters[i].nameHash),
                AnimatorControllerParameterType.Bool => animator.GetBool(parameters[i].nameHash) ? 1f : 0f,
                AnimatorControllerParameterType.Int => animator.GetInteger(parameters[i].nameHash),
                _ => 0f
            };
        return new FeetPose { Has = true, Position = bridge!.transform.position, Rotation = bridge.transform.rotation, Parameters = values };
    }

    internal void Write(BinaryWriter w)
    {
        w.Write(Has);
        if (!Has) return;
        w.Write(Position); w.Write(Rotation);
        w.Write((byte)Parameters.Length);
        foreach (var value in Parameters) w.Write(value);
    }

    internal static FeetPose Read(BinaryReader r)
    {
        if (!r.ReadBoolean()) return new FeetPose { Parameters = Array.Empty<float>() };
        var pose = new FeetPose { Has = true, Position = r.ReadVector3(), Rotation = r.ReadQuaternion() };
        int count = r.ReadByte();
        if (count > MaxParameters) throw new InvalidDataException("Too many feet animator parameters");
        pose.Parameters = new float[count];
        for (int i = 0; i < count; i++) pose.Parameters[i] = r.ReadSingle();
        return pose;
    }
}

/// <summary>
/// The moving parts inside the held tool (e.g. the sponge scrubbing out toward a surface while its
/// model stays at the hand), as local poses in hierarchy order.
/// </summary>
internal struct ToolPose
{
    internal const int MaxParts = 48;
    internal Vector3[] Positions;
    internal Quaternion[] Rotations;

    private static ToolModel? partsOf;
    private static Transform[] parts = Array.Empty<Transform>();

    internal static ToolPose Capture(ToolModel? model)
    {
        if (model == null) return Empty;
        // the parts the tool had when equipped, the same as the prefab the other player instantiates;
        // things the tool spawns under itself later (e.g. the sprayer's spray) would shift the list
        if (partsOf != model) { partsOf = model; parts = model.GetComponentsInChildren<Transform>(true); }
        int count = Math.Min(parts.Length - 1, MaxParts);   // [0] is the model itself, sent separately
        if (count <= 0) return Empty;
        var pose = new ToolPose { Positions = new Vector3[count], Rotations = new Quaternion[count] };
        for (int i = 0; i < count; i++)
        {
            pose.Positions[i] = parts[i + 1].localPosition;
            pose.Rotations[i] = parts[i + 1].localRotation;
        }
        return pose;
    }

    internal static ToolPose Empty => new() { Positions = Array.Empty<Vector3>(), Rotations = Array.Empty<Quaternion>() };

    internal void Write(BinaryWriter w)
    {
        w.Write((byte)Positions.Length);
        for (int i = 0; i < Positions.Length; i++) { w.Write(Positions[i]); w.Write(Rotations[i]); }
    }

    internal static ToolPose Read(BinaryReader r)
    {
        int count = r.ReadByte();
        if (count > MaxParts) throw new InvalidDataException("Too many tool parts");
        var pose = new ToolPose { Positions = new Vector3[count], Rotations = new Quaternion[count] };
        for (int i = 0; i < count; i++) { pose.Positions[i] = r.ReadVector3(); pose.Rotations[i] = r.ReadQuaternion(); }
        return pose;
    }
}

/// <summary>
/// Other players, drawn the way the game draws you: one hand (the plapper), the tool in use, and a
/// pair of feet on the ground that walk with the player's own foot animation.
/// </summary>
internal sealed class RemoteHands
{
    private sealed class Actor
    {
        internal ulong Id;
        internal GameObject Root = null!;
        internal GameObject? Hand;
        internal GameObject? Feet;
        internal Animator? FeetAnimator;
        internal AnimatorControllerParameter[] FeetParameters = Array.Empty<AnimatorControllerParameter>();
        internal TextMeshPro Tag = null!;
        internal TextMeshPro? Bubble;       // "Yip!" over their head
        internal float BubbleAge = 10f;
        internal int LooksVersion = -1;
        internal GameObject Tool = null!;
        internal Transform[] ToolParts = Array.Empty<Transform>();
        internal Vector3[] ToolRest = Array.Empty<Vector3>();   // each part's local position as the prefab has it
        internal ToolPose ToolPose = ToolPose.Empty;
        internal Vector3 GoalPosition;
        internal Quaternion GoalRotation;
        internal Vector3 HandPosition;
        internal Quaternion HandRotation;
        internal Vector3 ToolPosition;
        internal Quaternion ToolRotation;
        internal FeetPose FeetPose;
        internal bool Using;
        internal string ToolName = "";
        internal float LastPose;
        internal RemoteSpray? Spray;
        internal readonly SnapshotBuffer Buffer = new();
        internal KoboldAvatar? Avatar;
    }

    /// <summary>
    /// A friend's sprayer: while they spray, real water streams come out of its nozzle, as the game's own sprayer
    /// makes them, with its mist and sound. For looks only: what the water does (paint, Ryan's reactions, buckets)
    /// comes from that player (see SprayScopePatch).
    /// </summary>
    private sealed class RemoteSpray
    {
        internal GameObject Prefab = null!;
        internal Transform Nozzle = null!;
        internal VisualEffect? Mist;
        internal AudioSource? Sound;
        internal WalkNWashParticleSpline? Stream;
        internal float NextStream;
        internal bool On;
    }

    private static readonly HashSet<WalkNWashParticleSpline> remoteStreams = new();
    private static readonly System.Reflection.FieldInfo SprayPrefabField = AccessTools.Field(typeof(WalkNWashSplineSprayer), "sprayPrefab");
    private static readonly System.Reflection.FieldInfo NozzleField = AccessTools.Field(typeof(WalkNWashSplineSprayer), "nozzleTransform");
    private static readonly System.Reflection.FieldInfo MistField = AccessTools.Field(typeof(ToolModelSprayer), "sprayEffect");
    private static readonly System.Reflection.FieldInfo SoundField = AccessTools.Field(typeof(ToolModelSprayer), "sprayAudio");

    /// <summary>A water stream from another player's sprayer (shown here, not acted on).</summary>
    internal static bool IsRemoteStream(WalkNWashParticleSpline stream) => remoteStreams.Count > 0 && remoteStreams.Contains(stream);

    private static readonly System.Reflection.FieldInfo HandField = AccessTools.Field(typeof(PlapperHand), "hand");
    private readonly Dictionary<ulong, Actor> actors = new();
    private readonly CoopPlugin owner;
    internal RemoteHands(CoopPlugin owner) => this.owner = owner;

    internal void UpdatePose(ulong id, double sentAt, Vector3 pos, Quaternion rot, string tool,
                             Vector3 handPos, Quaternion handRot, Vector3 toolPos, Quaternion toolRot, bool usingTool,
                             FeetPose feet, ToolPose toolParts)
    {
        if (id == owner.Session.SelfId) return;
        if (!actors.TryGetValue(id, out var actor))
        {
            actor = Create(id, pos, rot, handPos, handRot, feet);
            actors[id] = actor;
        }
        actor.Buffer.Add(new PoseSnapshot
        {
            SenderTime = sentAt, Head = pos, HeadRotation = rot, Hand = handPos, HandRotation = handRot,
            Tool = toolPos, ToolRotation = toolRot, HasFeet = feet.Has, Feet = feet.Position, FeetRotation = feet.Rotation,
            HoldingTool = !string.IsNullOrEmpty(tool) && !tool.Contains("Empty"),
        }, Time.realtimeSinceStartupAsDouble);
        actor.GoalPosition = pos;
        actor.GoalRotation = rot;
        actor.HandPosition = handPos;
        actor.HandRotation = handRot;
        actor.ToolPosition = toolPos;
        actor.ToolRotation = toolRot;
        actor.FeetPose = feet;
        actor.ToolPose = toolParts;
        actor.Using = usingTool;
        actor.LastPose = Time.realtimeSinceStartup;
        ApplyFeetParameters(actor);
        if (actor.ToolName != tool)
        {
            actor.ToolName = tool;
            if (actor.Spray != null) StopStream(actor.Spray);   // its nozzle goes with the old tool
            UnityEngine.Object.Destroy(actor.Tool);
            actor.Tool = CreateToolVisual(actor.Root.transform, tool, out actor.Spray);
            actor.ToolParts = actor.Tool.GetComponentsInChildren<Transform>(true);
            actor.ToolRest = actor.ToolParts.Select(t => t.localPosition).ToArray();
            // the kobold holds the tool itself; the old hands-only look names it under the player
            actor.Tag.text = owner.Session.NameOf(id) + (actor.Avatar == null && actor.Tool.activeSelf ? $"\n<size=70%>{tool}" : "");
        }
    }

    /// <summary>They yipped: heard from their kobold's mouth, in their colour's voice, with a "Yip!" over their head.</summary>
    internal void Yip(ulong id)
    {
        if (id == owner.Session.SelfId || !actors.TryGetValue(id, out var actor) || !actor.Root.activeSelf) return;
        var mouth = actor.Avatar != null ? actor.Avatar.Head : actor.Root.transform;
        PlayerLooks.PlayYip(mouth.position, SlotOf(id), mouth);
        actor.Avatar?.Yip();
        actor.Bubble ??= Lettering(actor.Root.transform, "FriendYip", "Yip!", LobbyLook.Yellow, 1.5f);
        actor.Bubble.gameObject.SetActive(true);
        actor.BubbleAge = 0f;
    }

    internal bool TryGetPosition(ulong id, out Vector3 position)
    {
        if (actors.TryGetValue(id, out var actor) && Time.realtimeSinceStartup - actor.LastPose < 2f)
        {
            position = actor.GoalPosition;
            return true;
        }
        position = default;
        return false;
    }

    /// <summary>The first friend's snapshot buffer (developer trace).</summary>
    internal SnapshotBuffer? FirstBuffer()
    {
        foreach (var actor in actors.Values)
            if (actor.Avatar != null && actor.Root.activeSelf) return actor.Buffer;
        return null;
    }

    /// <summary>The first friend's kobold that is showing (developer screenshots).</summary>
    internal KoboldAvatar? FirstAvatar()
    {
        foreach (var actor in actors.Values)
            if (actor.Avatar != null && actor.Root.activeSelf) return actor.Avatar;
        return null;
    }

    /// <summary>Seconds since the first friend last yipped (developer screenshots).</summary>
    internal float FirstYipAge()
    {
        foreach (var actor in actors.Values)
            if (actor.Avatar != null && actor.Root.activeSelf) return actor.Bubble != null ? actor.BubbleAge : 10f;
        return 10f;
    }

    /// <summary>Whether the first friend's kobold holds a tool, and is using it (developer screenshots).</summary>
    internal bool FirstHolding(out bool usingTool)
    {
        foreach (var actor in actors.Values)
            if (actor.Avatar != null && actor.Root.activeSelf) { usingTool = actor.Using && actor.Tool.activeSelf; return actor.Tool.activeSelf; }
        usingTool = false;
        return false;
    }

    internal bool TryGetTool(ulong id, out string name)
    {
        if (actors.TryGetValue(id, out var actor)) { name = actor.ToolName; return true; }
        name = string.Empty;
        return false;
    }

    internal void Tick()
    {
        double now = Time.realtimeSinceStartupAsDouble;
        foreach (var actor in actors.Values)
        {
            float blend = 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime);
            float partsBlend = blend;   // the tool's moving parts aren't in the snapshots: they keep easing
            if (actor.Buffer.Sample(now, out var snap))
            {
                // snapshot interpolation: exactly where they were a moment ago, smooth however packets arrive
                actor.GoalPosition = snap.Head; actor.GoalRotation = snap.HeadRotation;
                actor.HandPosition = snap.Hand; actor.HandRotation = snap.HandRotation;
                actor.ToolPosition = snap.Tool; actor.ToolRotation = snap.ToolRotation;
                if (snap.HasFeet) { actor.FeetPose.Position = snap.Feet; actor.FeetPose.Rotation = snap.FeetRotation; }
                blend = 1f;
            }
            actor.Root.transform.position = Vector3.Lerp(actor.Root.transform.position, actor.GoalPosition, blend);
            actor.Root.transform.rotation = Quaternion.Slerp(actor.Root.transform.rotation, actor.GoalRotation, blend);
            if (actor.Hand != null)
            {
                actor.Hand.transform.position = Vector3.Lerp(actor.Hand.transform.position, actor.HandPosition, blend);
                actor.Hand.transform.rotation = Quaternion.Slerp(actor.Hand.transform.rotation, actor.HandRotation, blend);
            }
            if (actor.Feet != null)
            {
                if (actor.Feet.activeSelf != actor.FeetPose.Has) actor.Feet.SetActive(actor.FeetPose.Has);
                if (actor.FeetPose.Has)
                {
                    actor.Feet.transform.position = Vector3.Lerp(actor.Feet.transform.position, actor.FeetPose.Position, blend);
                    actor.Feet.transform.rotation = Quaternion.Slerp(actor.Feet.transform.rotation, actor.FeetPose.Rotation, blend);
                }
            }
            if (actor.Tool.activeSelf)
            {
                actor.Tool.transform.position = Vector3.Lerp(actor.Tool.transform.position, actor.ToolPosition, blend);
                actor.Tool.transform.rotation = Quaternion.Slerp(actor.Tool.transform.rotation, actor.ToolRotation, blend);
                // same prefab on both sides: only when the part lists match (runtime-added children shift them)
                var parts = actor.ToolParts;
                var pose = actor.ToolPose;
                if (pose.Positions.Length == parts.Length - 1)
                    for (int i = 0; i < pose.Positions.Length; i++)
                    {
                        var part = parts[i + 1];
                        part.localPosition = Vector3.Lerp(part.localPosition, pose.Positions[i], partsBlend);
                        part.localRotation = Quaternion.Slerp(part.localRotation, pose.Rotations[i], partsBlend);
                    }
            }
            actor.Root.SetActive(Time.realtimeSinceStartup - actor.LastPose < 3f);
            if (actor.Avatar != null && actor.Root.activeSelf && actor.Buffer.Count > 0)
            {
                if (actor.Hand != null && actor.Hand.activeSelf) actor.Hand.SetActive(false);
                if (actor.Feet != null && actor.Feet.activeSelf) actor.Feet.SetActive(false);
                actor.Buffer.Sample(now, out var p);
                try
                {
                    if (actor.LooksVersion != PlayerLooks.Version)
                    {
                        actor.LooksVersion = PlayerLooks.Version;
                        actor.Avatar.SetColour(SlotOf(actor.Id));
                        actor.Tag.color = PlayerLooks.Lettering(actor.Avatar.Slot);
                    }
                    if (actor.Tool.activeSelf) p.Tool = Grip(actor);
                    actor.Avatar.Drive(p, Time.unscaledDeltaTime, actor.Tool.activeSelf, actor.Using);
                    float volume = Mathf.Lerp(.45f, 1f, actor.Avatar.Gait);   // shuffling in place is quieter
                    if (actor.Avatar.LandedL) PlayerLooks.Footstep(actor.Avatar.FootL.position, volume);
                    if (actor.Avatar.LandedR) PlayerLooks.Footstep(actor.Avatar.FootR.position, volume);
                    actor.Tag.transform.position = actor.Avatar.Head.position + Vector3.up * .42f * actor.Avatar.Head.lossyScale.y;
                }
                catch (Exception e)
                {
                    owner.Log.LogWarning($"Kobold avatar for {owner.Session.NameOf(actor.Id)} failed ({e.Message}); showing hands");
                    UnityEngine.Object.Destroy(actor.Avatar.Root);
                    actor.Avatar = null;
                    if (actor.Hand != null) actor.Hand.SetActive(true);
                }
            }
            if (actor.Spray != null) TickSpray(actor, actor.Spray);
            var camera = Camera.main;
            if (camera != null) Billboard(actor.Tag.transform, camera);
            if (actor.Bubble != null && actor.Bubble.gameObject.activeSelf) TickBubble(actor, camera, Time.unscaledDeltaTime);
        }
    }

    /// <summary>
    /// Where the kobold's hand should be on its tool: the tool's handle, carried along by the part that has moved
    /// furthest from where the prefab has it (the sponge reaching out to the surface being scrubbed).
    /// </summary>
    private static Vector3 Grip(Actor actor)
    {
        var parts = actor.ToolParts;
        Vector3 best = Vector3.zero;
        for (int i = 1; i < parts.Length && i < actor.ToolRest.Length; i++)
        {
            var parent = parts[i].parent;
            if (parent == null) continue;
            Vector3 moved = parent.TransformVector(parts[i].localPosition - actor.ToolRest[i]);
            if (moved.sqrMagnitude > best.sqrMagnitude) best = moved;
        }
        return actor.Tool.transform.position + (best.sqrMagnitude > .05f * .05f ? best : Vector3.zero);
    }

    /// <summary>Turn every name and "Yip!" toward this camera (developer photos, taken from their own camera).</summary>
    internal void FaceLabels(Camera camera)
    {
        foreach (var actor in actors.Values)
        {
            Billboard(actor.Tag.transform, camera);
            if (actor.Bubble != null && actor.Bubble.gameObject.activeSelf) TickBubble(actor, camera, 0f);
        }
    }

    /// <summary>Face the camera, and grow with distance a little so a name stays readable across the room.</summary>
    private static void Billboard(Transform label, Camera camera)
    {
        Vector3 away = label.position - camera.transform.position;
        if (away.sqrMagnitude < 1e-6f) return;
        label.rotation = Quaternion.LookRotation(away, camera.transform.up);
        label.localScale = Vector3.one * Mathf.Clamp(away.magnitude / 4f, 1f, 2.5f) / Mathf.Max(label.parent.lossyScale.x, 1e-3f);
    }

    /// <summary>"Yip!" pops up over their head, bounces, floats up a little and fades.</summary>
    private static void TickBubble(Actor actor, Camera? camera, float dt)
    {
        var bubble = actor.Bubble!;
        actor.BubbleAge += dt;
        float age = actor.BubbleAge;
        if (age > 1.1f) { bubble.gameObject.SetActive(false); return; }
        if (camera != null) Billboard(bubble.transform, camera);
        // just above their name, rising a little
        var tag = actor.Tag.transform;
        bubble.transform.position = tag.position + tag.up * (.16f + .1f * Mathf.Sqrt(age)) * tag.localScale.y;
        // overshoot to 1.2, settle at 1
        float pop = age < .12f ? age / .12f * 1.2f : Mathf.Lerp(1.2f, 1f, Mathf.Clamp01((age - .12f) / .12f));
        bubble.transform.localScale *= pop;
        bubble.alpha = Mathf.Clamp01((1.1f - age) / .3f);
    }

    /// <summary>Sticker-style lettering in the world: Chewy, an ink outline and a soft shadow, like the co-op menu.</summary>
    private static TextMeshPro Lettering(Transform parent, string name, string text, Color colour, float size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var label = go.AddComponent<TextMeshPro>();
        if (LobbyLook.Font != null) label.font = LobbyLook.Font;
        if (LobbyLook.LabelMaterial != null) label.fontSharedMaterial = LobbyLook.LabelMaterial;
        label.text = text;
        label.fontSize = size;
        label.color = colour;
        label.alignment = TextAlignmentOptions.Center;
        label.textWrappingMode = TextWrappingModes.NoWrap;
        label.rectTransform.sizeDelta = new Vector2(4f, 1f);
        return label;
    }

    private int SlotOf(ulong id) => PlayerLooks.SlotOf(id, (ulong)owner.Session.HostId);

    private static void TickSpray(Actor actor, RemoteSpray spray)
    {
        bool on = actor.Using && actor.Root.activeSelf && actor.Tool.activeSelf && spray.Nozzle != null;
        if (on != spray.On)
        {
            spray.On = on;
            if (spray.Mist != null) { if (on) spray.Mist.Play(); else spray.Mist.Stop(); }
            if (spray.Sound != null) { if (on) spray.Sound.Play(); else spray.Sound.Stop(); }
            if (!on) StopStream(spray);
            spray.NextStream = 0f;
        }
        if (!on || Time.time < spray.NextStream) return;
        // like the game's sprayer: a fresh stream every 0.24-0.84 s, the last one falls away
        spray.NextStream = Time.time + .96f - UnityEngine.Random.Range(.12f, .72f);
        StopStream(spray);
        // under the player, so it goes when they do
        var stream = UnityEngine.Object.Instantiate(spray.Prefab, actor.Root.transform).GetComponentInChildren<WalkNWashParticleSpline>();
        if (stream == null) return;
        remoteStreams.RemoveWhere(old => old == null);
        remoteStreams.Add(stream);
        stream.SetFireTransform(spray.Nozzle);
        stream.SetFiring(true);
        spray.Stream = stream;
    }

    private static void StopStream(RemoteSpray spray)
    {
        if (spray.Stream != null) spray.Stream.SetFiring(false);
        spray.Stream = null;
    }

    internal void Remove(ulong id)
    {
        if (!actors.TryGetValue(id, out var actor)) return;
        UnityEngine.Object.Destroy(actor.Root);
        actors.Remove(id);
    }

    internal void Clear()
    {
        foreach (var id in new List<ulong>(actors.Keys)) Remove(id);
    }

    private static void ApplyFeetParameters(Actor actor)
    {
        var animator = actor.FeetAnimator;
        if (animator == null || !animator.isActiveAndEnabled || actor.FeetPose.Parameters == null) return;
        int count = Math.Min(actor.FeetParameters.Length, actor.FeetPose.Parameters.Length);
        for (int i = 0; i < count; i++)
        {
            var parameter = actor.FeetParameters[i];
            float value = actor.FeetPose.Parameters[i];
            switch (parameter.type)
            {
                case AnimatorControllerParameterType.Float: animator.SetFloat(parameter.nameHash, value); break;
                case AnimatorControllerParameterType.Bool: animator.SetBool(parameter.nameHash, value > .5f); break;
                case AnimatorControllerParameterType.Int: animator.SetInteger(parameter.nameHash, Mathf.RoundToInt(value)); break;
            }
        }
    }

    private Actor Create(ulong id, Vector3 pos, Quaternion rot, Vector3 handPos, Quaternion handRot, FeetPose feet)
    {
        var root = new GameObject($"CoopPlayer_{id}");
        root.transform.SetPositionAndRotation(pos, rot);

        // the one hand the game gives you (the plapper), not a mirrored pair
        var plapper = UnityEngine.Object.FindFirstObjectByType<PlapperHand>();
        var handSource = plapper != null ? HandField.GetValue(plapper) as Transform ?? plapper.transform.Find("Hand") : null;
        var hand = handSource != null ? CloneVisual(handSource.gameObject, root.transform, keepAnimator: false) : null;
        if (hand != null)
        {
            hand.name = "FriendHand";
            hand.transform.SetPositionAndRotation(handPos, handRot);
        }
        else owner.Log.LogWarning("Could not locate the game's hand model for a remote player");

        // the feet, walking with the remote player's own foot animation
        var bridge = UnityEngine.Object.FindFirstObjectByType<LocomotionAnimationBridge>();
        var feetObject = bridge != null ? CloneVisual(bridge.gameObject, root.transform, keepAnimator: true) : null;
        Animator? feetAnimator = null;
        if (feetObject != null)
        {
            feetObject.name = "FriendFeet";
            if (feet.Has) feetObject.transform.SetPositionAndRotation(feet.Position, feet.Rotation);
            feetAnimator = feetObject.GetComponent<Animator>();
        }
        else owner.Log.LogWarning("Could not locate the game's feet model for a remote player");

        var tag = Lettering(root.transform, "FriendName", owner.Session.NameOf(id), PlayerLooks.Lettering(SlotOf(id)), 1.1f);
        tag.transform.localPosition = new Vector3(0, .35f, 0);

        var tool = CreateToolVisual(root.transform, string.Empty, out _);

        // the whole kobold (KoboldKare's, CC0), dressed in the game's own lit material taken from your hand
        KoboldAvatar? avatar = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var data = AvatarData.Get(owner.Log);
        long loadMs = clock.ElapsedMilliseconds;
        var handMaterial = handSource != null ? handSource.GetComponentInChildren<Renderer>(true)?.sharedMaterial : null;
        handMaterial ??= Resources.FindObjectsOfTypeAll<Material>().FirstOrDefault(m => m.shader != null && m.shader.name == "WalkNWashLit");
        if (data != null && handMaterial != null)
        {
            try
            {
                avatar = new KoboldAvatar(data, handMaterial, root.transform, SlotOf(id), (int)(id & 0x7fffffff));
                if (hand != null) hand.SetActive(false);
                if (feetObject != null) feetObject.SetActive(false);
                owner.Log.LogInfo($"Kobold for {owner.Session.NameOf(id)} built in {clock.ElapsedMilliseconds} ms (data {loadMs} ms)");
            }
            catch (Exception e) { owner.Log.LogWarning($"Could not build a kobold for {owner.Session.NameOf(id)}: {e.Message}"); }
        }
        return new Actor { Id = id, Avatar = avatar, Root = root, Hand = hand, Feet = feetObject, FeetAnimator = feetAnimator,
                           FeetParameters = feetAnimator != null ? feetAnimator.parameters : Array.Empty<AnimatorControllerParameter>(),
                           Tag = tag, Tool = tool, FeetPose = feet,
                           GoalPosition = pos, GoalRotation = rot, HandPosition = handPos, HandRotation = handRot,
                           ToolPosition = pos + rot * new Vector3(.2f, -.2f, .6f), ToolRotation = rot,
                           LastPose = Time.realtimeSinceStartup };
    }

    /// <summary>
    /// A look-alike of one of this player's own parts: cloned while inactive so none of its scripts
    /// (which drive the local player) ever run, with its colliders removed. Keeps the world scale.
    /// </summary>
    private static GameObject CloneVisual(GameObject source, Transform parent, bool keepAnimator)
    {
        var holder = new GameObject("CoopCloneHolder");
        holder.SetActive(false);
        var clone = UnityEngine.Object.Instantiate(source, holder.transform);
        // a few passes: a component another one requires can only go after the one requiring it
        for (int pass = 0; pass < 3; pass++)
        {
            foreach (var script in clone.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(script);
            foreach (var collider in clone.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(collider);
            foreach (var body in clone.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.DestroyImmediate(body);
            if (!keepAnimator)
                foreach (var animator in clone.GetComponentsInChildren<Animator>(true)) UnityEngine.Object.DestroyImmediate(animator);
        }
        foreach (var animator in clone.GetComponentsInChildren<Animator>(true))
        {
            animator.applyRootMotion = false;
            animator.fireEvents = false;   // their receivers (e.g. footstep sounds) were removed with the scripts
        }
        clone.transform.SetParent(parent, false);
        clone.transform.localScale = source.transform.lossyScale;
        clone.SetActive(true);
        UnityEngine.Object.Destroy(holder);
        return clone;
    }

    private static RemoteSpray? SprayOf(GameObject visual, out AudioResource? sound)
    {
        sound = null;
        var sprayer = visual.GetComponentInChildren<WalkNWashSplineSprayer>(true);
        if (sprayer == null || SprayPrefabField.GetValue(sprayer) is not GameObject prefab ||
            NozzleField.GetValue(sprayer) is not Transform nozzle || nozzle == null) return null;
        var model = visual.GetComponentInChildren<ToolModelSprayer>(true);
        if (model != null) sound = SoundField.GetValue(model) as AudioResource;
        return new RemoteSpray { Prefab = prefab, Nozzle = nozzle, Mist = model != null ? MistField.GetValue(model) as VisualEffect : null };
    }

    private static GameObject CreateToolVisual(Transform parent, string name, out RemoteSpray? spray)
    {
        spray = null;
        var asset = Resources.FindObjectsOfTypeAll<Tool>().FirstOrDefault(t => t.name == name);
        var prefab = asset != null ? AccessTools.Field(typeof(Tool), "modelPrefab")?.GetValue(asset) as ToolModel : null;
        GameObject visual;
        if (prefab != null)
        {
            visual = UnityEngine.Object.Instantiate(prefab.gameObject, parent);
            visual.name = "FriendTool_" + name;
            spray = SprayOf(visual, out var sound);   // read before its scripts go
            foreach (var script in visual.GetComponentsInChildren<MonoBehaviour>())
            { script.enabled = false; UnityEngine.Object.Destroy(script); }
            foreach (var collider in visual.GetComponentsInChildren<Collider>())
            { collider.enabled = false; UnityEngine.Object.Destroy(collider); }
            // the parts follow the sender's pose, so the copy's own animator must not move them
            foreach (var behaviour in visual.GetComponentsInChildren<Behaviour>()) behaviour.enabled = false;
            if (spray != null)
            {
                if (spray.Mist != null) { spray.Mist.enabled = true; spray.Mist.Stop(); }
                if (sound != null)
                {
                    var source = visual.AddComponent<AudioSource>();
                    AudioHelper.SetupAudioSourceForSFX(source);   // the game's sound settings (SFX volume)
                    source.playOnAwake = false;
                    source.loop = true;
                    source.spatialBlend = 1f;                      // heard from where that player is
                    source.resource = sound;
                    spray.Sound = source;
                }
            }
            visual.transform.localPosition = new Vector3(.25f, -.22f, .58f);
            visual.transform.localRotation = Quaternion.identity;
        }
        else
        {
            visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            visual.transform.SetParent(parent, false);
            visual.name = "FriendToolPlaceholder";
            visual.transform.localPosition = new Vector3(.27f, -.26f, .72f);
            visual.transform.localRotation = Quaternion.Euler(80, 0, 0);
            visual.transform.localScale = new Vector3(.055f, .14f, .055f);
            foreach (var collider in visual.GetComponents<Collider>()) UnityEngine.Object.Destroy(collider);
        }
        visual.SetActive(!string.IsNullOrEmpty(name) && !name.Contains("Empty"));
        return visual;
    }
}
