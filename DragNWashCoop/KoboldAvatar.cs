using System;
using System.Collections.Generic;
using UnityEngine;

namespace DragNWashCoop;

/// <summary>
/// Another player's body: the kobold from KoboldKare, animated procedurally from what that player actually does,
/// so it always matches them - the head looks where their camera looks, the left hand reaches for their real
/// hand (the plapper), the right hand holds their real tool, the feet plant and step as they walk, and the tail,
/// ears and breathing keep it alive. Nothing here sends anything: it only reads the interpolated pose.
/// </summary>
internal sealed class KoboldAvatar
{
    // the kobold's colours, by player slot: KoboldKare red first, then the game's kobold green, blue, purple
    internal static readonly (float Hue, float Brightness, float Saturation)[] Palette =
    {
        (0f, 0f, 1f), (118f, .02f, .95f), (205f, 0f, 1f), (270f, 0f, 1f), (35f, .04f, 1.05f), (160f, 0f, 1f),
    };

    internal readonly GameObject Root;
    private readonly AvatarData data;
    private readonly Transform[] bones;
    private readonly Quaternion[] restRootRot;      // each bone's rest rotation relative to the avatar root
    private readonly Vector3[] restRootPos;
    private readonly SkinnedMeshRenderer body;
    private readonly Transform hips, spine, chest, neck, head, jaw;
    private readonly Transform? earL, earR;
    private readonly Transform armL, foreL, handL, armR, foreR, handR;
    private readonly Transform legL, shinL, footL, legR, shinR, footR;
    private readonly Transform[] tail;
    private readonly List<(Transform bone, Vector3 axisRest, float share)> fingersL = new(), fingersR = new();
    private readonly int blink, happy, squint, mouthOpen;

    private readonly FootStepper stepL = new(), stepR = new();
    private float footYawL, footYawR;
    private float yawFromL, yawToL, yawFromR, yawToR;   // a swinging foot turns over the step, not at lift-off
    private bool placed, feetPlaced;
    private float bodyYaw;
    private float yawVelocity;
    private float yawTarget;
    private Motion.Spring hipSpring;   // smoothed bob/sway (x = sway, y = bob)
    private Vector3 lastGround;
    private Vector3 velocity;
    private float standingEye = -1f;
    private float scale = 1f;
    private Motion.Spring earSpringL, earSpringR, tailSpring;
    private Vector3 lastHeadForward;
    private float nextBlink, blinkT = 1f;
    private float breathPhase;
    private float swing;               // gait phase for the free arm
    private float hipDrop;             // how far the hips sit below rest so the feet stay reachable
    private float yipAge = 10f;        // seconds since they last yipped
    private float reachLean;           // degrees the body leans toward a far tool
    private float gaitNow;
    private readonly float seed;
    private int slot;

    internal Transform Head => head;
    internal Transform HandL => handL;
    internal Transform FootL => footL;
    internal Transform FootR => footR;
    internal Transform Hips => hips;
    internal bool SwingingL => stepL.Swinging;
    internal bool SwingingR => stepR.Swinging;
    internal float BodyYaw => bodyYaw;
    internal Vector3 AnkleTargetL, AnkleTargetR;   // (developer trace)
    /// <summary>A foot came down this frame (for its footstep sound).</summary>
    internal bool LandedL, LandedR;
    /// <summary>0 standing .. 1 running.</summary>
    internal float Gait => gaitNow;
    internal int Slot => slot;

    internal KoboldAvatar(AvatarData data, Material baseMaterial, Transform parent, int colourSlot, int seed)
    {
        this.data = data;
        this.seed = seed * .6180339f % 1f;
        Root = new GameObject("KoboldAvatar");
        Root.transform.SetParent(parent, false);
        int n = data.BoneNames.Length;
        bones = new Transform[n];
        for (int i = 0; i < n; i++)
        {
            var t = new GameObject(data.BoneNames[i]).transform;
            bones[i] = t;
        }
        for (int i = 0; i < n; i++)
        {
            bones[i].SetParent(data.Parents[i] >= 0 ? bones[data.Parents[i]] : Root.transform, false);
            bones[i].localPosition = data.LocalPositions[i];
            bones[i].localRotation = data.LocalRotations[i];
        }
        restRootRot = new Quaternion[n];
        restRootPos = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            int p = data.Parents[i];
            restRootRot[i] = p >= 0 ? restRootRot[p] * data.LocalRotations[i] : data.LocalRotations[i];
            restRootPos[i] = p >= 0 ? restRootPos[p] + restRootRot[p] * data.LocalPositions[i] : data.LocalPositions[i];
        }
        Transform B(string role) => bones[data.Roles[role]];
        Transform? Bo(string role) => data.Roles.TryGetValue(role, out int i) ? bones[i] : null;
        hips = B("hips"); spine = B("spine"); chest = B("chest"); neck = B("neck"); head = B("head"); jaw = B("jaw");
        earL = Bo("earL"); earR = Bo("earR");
        armL = B("upperArmL"); foreL = B("lowerArmL"); handL = B("handL");
        armR = B("upperArmR"); foreR = B("lowerArmR"); handR = B("handR");
        legL = B("upperLegL"); shinL = B("lowerLegL"); footL = B("footL");
        legR = B("upperLegR"); shinR = B("lowerLegR"); footR = B("footR");
        var tailList = new List<Transform>();
        for (int i = 0; i < 8 && Bo("tail" + i) is { } tb; i++) tailList.Add(tb);
        tail = tailList.ToArray();
        CollectFingers(".L", fingersL);
        CollectFingers(".R", fingersR);

        var go = new GameObject("Body");
        go.transform.SetParent(Root.transform, false);
        body = go.AddComponent<SkinnedMeshRenderer>();
        body.sharedMesh = data.Mesh;
        body.bones = bones;
        body.rootBone = hips;
        body.updateWhenOffscreen = true;
        body.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        var mats = new Material[data.Mesh.subMeshCount];
        slot = ((colourSlot % Palette.Length) + Palette.Length) % Palette.Length;
        for (int s = 0; s < mats.Length; s++)
        {
            var m = new Material(baseMaterial) { name = $"Kobold_{s}" };
            // the body is recoloured per player; the eyes keep their colour
            SetTexture(m, Texture(data, s, s == 0 ? slot : 0));
            if (m.HasProperty("_UseVertexTinting")) m.SetFloat("_UseVertexTinting", 0f);
            if (m.HasProperty("_ColorTint")) m.SetColor("_ColorTint", data.Colors[s]);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", data.Colors[s]);
            if (m.HasProperty("_Dirty")) m.SetFloat("_Dirty", 0f);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", data.Smoothness[s]);
            if (m.HasProperty("_MaskMap")) m.SetTexture("_MaskMap", MaskTexture(data.Smoothness[s]));
            mats[s] = m;
        }
        body.sharedMaterials = mats;

        var mesh = data.Mesh;
        blink = mesh.GetBlendShapeIndex("BlinkLeftRight");
        happy = mesh.GetBlendShapeIndex("HappyLeftRight");
        squint = mesh.GetBlendShapeIndex("SquintLeftRight");
        mouthOpen = mesh.GetBlendShapeIndex("vrc.v_aa");
        nextBlink = Time.time + 1f + this.seed * 3f;
        lastHeadForward = Vector3.forward;
    }

    /// <summary>Wear another colour (the host has told everyone which one this player is).</summary>
    internal void SetColour(int colourSlot)
    {
        colourSlot = ((colourSlot % Palette.Length) + Palette.Length) % Palette.Length;
        if (colourSlot == slot) return;
        slot = colourSlot;
        var mats = body.sharedMaterials;
        if (mats.Length > 0) SetTexture(mats[0], Texture(data, 0, slot));
    }

    private static Texture2D? Texture(AvatarData data, int submesh, int colourSlot)
    {
        var png = data.TextureBytes[submesh];
        if (png == null) return null;
        int key = submesh * 100 + colourSlot;
        if (!textures.TryGetValue(key, out var tex) || tex == null)
        {
            var pal = Palette[colourSlot];
            textures[key] = tex = AvatarData.Recolour(png, submesh == 0 ? pal.Hue : 0f, submesh == 0 ? pal.Brightness : 0f, submesh == 0 ? pal.Saturation : 1f);
        }
        return tex;
    }

    private static void SetTexture(Material m, Texture2D? tex)
    {
        if (tex == null) return;
        if (m.HasProperty("_BaseColorMap")) m.SetTexture("_BaseColorMap", tex);
        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
        if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);
    }

    /// <summary>They yipped: chin up, mouth open, ears perk, tail wags.</summary>
    internal void Yip()
    {
        yipAge = 0f;
        earSpringL.Velocity.x += 320f;
        earSpringR.Velocity.x += 320f;
    }

    private static readonly Dictionary<int, Texture2D> masks = new();
    private static readonly Dictionary<int, Texture2D> textures = new();   // recoloured once per colour, shared

    private static int prewarmed;

    /// <summary>
    /// Prepare one more of the avatar's textures ahead of time (call once per frame in menus): every palette colour of
    /// the body, then the eyes. Returns false when all are ready, so a friend appearing mid-game costs no hitch.
    /// </summary>
    internal static bool PrewarmNext(AvatarData data)
    {
        int count = Palette.Length + (data.TextureBytes.Length > 1 ? data.TextureBytes.Length - 1 : 0);
        if (prewarmed >= count) return false;
        int i = prewarmed++;
        int s = i < Palette.Length ? 0 : i - Palette.Length + 1;
        int slot = i < Palette.Length ? i : 0;
        Texture(data, s, slot);
        return true;
    }

    /// <summary>A flat mask map (metallic 0, occlusion 1, smoothness s) for the game's lit shader.</summary>
    private static Texture2D MaskTexture(float smoothness)
    {
        int key = Mathf.RoundToInt(smoothness * 100);
        if (masks.TryGetValue(key, out var t) && t != null) return t;
        t = new Texture2D(4, 4, TextureFormat.RGBA32, false, true);
        var px = new Color[16];
        for (int i = 0; i < 16; i++) px[i] = new Color(0f, 1f, 0f, smoothness);
        t.SetPixels(px);
        t.Apply(false, true);
        return masks[key] = t;
    }

    private void CollectFingers(string side, List<(Transform, Vector3, float)> into)
    {
        // palm faces down in the T-pose rest; a finger curls toward the palm around (finger direction x down)
        foreach (var (finger, share) in new[] { ("Index", 1f), ("Middle", 1f), ("Ring", 1f), ("Pinky", 1f), ("Thumb", .45f) })
            for (int k = 1; k <= 3; k++)
            {
                int i = Array.IndexOf(data.BoneNames, $"Hand{finger}{k}{side}");
                if (i < 0) continue;
                int child = Array.IndexOf(data.BoneNames, $"Hand{finger}{k + 1}{side}");
                Vector3 dir = child >= 0 ? restRootPos[child] - restRootPos[i] : restRootRot[i] * Vector3.up;
                Vector3 axis = Vector3.Cross(dir.normalized, Vector3.down);
                if (axis.sqrMagnitude < 1e-6f) continue;
                into.Add((bones[i], axis.normalized, share));
            }
    }

    private void ResetPose()
    {
        for (int i = 0; i < bones.Length; i++)
        {
            bones[i].localPosition = data.LocalPositions[i];
            bones[i].localRotation = data.LocalRotations[i];
        }
    }

    private static readonly RaycastHit[] hits = new RaycastHit[8];

    /// <summary>
    /// The floor under a foot: the nearest upward-facing surface around foot level. Anything much higher (another
    /// player's capsule, the dragon, a tool rack) is ignored, so a foot never lands on top of it.
    /// </summary>
    private static float GroundY(Vector3 at, float fallback, float reach)
    {
        int n = Physics.RaycastNonAlloc(at + Vector3.up * reach * .4f, Vector3.down, hits, reach * 1.4f, ~0, QueryTriggerInteraction.Ignore);
        float best = float.NegativeInfinity;
        for (int i = 0; i < n; i++)
        {
            var h = hits[i];
            // steps up onto a kerb or stair, but never reaches down more than a little below where the player
            // stands (over a ledge or the wash pit the foot stays level with them instead of hanging off the leg)
            if (h.normal.y < .6f || h.point.y > fallback + reach * .35f || h.point.y < fallback - reach * .2f) continue;
            if (h.collider.attachedRigidbody != null && !h.collider.attachedRigidbody.isKinematic) continue;
            if (h.point.y > best) best = h.point.y;
        }
        return float.IsNegativeInfinity(best) ? fallback : best;
    }

    /// <summary>Pose the kobold for this frame.</summary>
    internal void Drive(in PoseSnapshot p, float dt, bool holdingTool, bool usingTool)
    {
        LandedL = LandedR = false;
        if (dt <= 0f) return;
        ResetPose();
        yipAge += dt;
        // the yip: snaps open in 0.07 s, eases shut over 0.4 s
        float yip = yipAge < .07f ? yipAge / .07f : Smooth01(1f - (yipAge - .07f) / .4f);

        // ---------------------------------------------------------------- where they stand, how tall they are
        Vector3 ground = p.HasFeet ? p.Feet : p.Head - Vector3.up * (standingEye > 0 ? standingEye : 1.2f);
        float eye = Mathf.Max(.3f, p.Head.y - ground.y);
        standingEye = standingEye < 0 ? eye : Mathf.Max(standingEye - dt * .02f, eye);     // the tallest they stand
        scale = Mathf.Clamp(standingEye / data.EyeHeight, .35f, 3f);
        float crouch = Mathf.Clamp01((standingEye - eye) / standingEye);
        float legLen = data.LegLength * scale;

        if (!placed)
        {
            placed = true;
            lastGround = ground;
            bodyYaw = yawTarget = p.HeadRotation.eulerAngles.y;
        }
        var v = (ground - lastGround) / dt;
        lastGround = ground;
        v.y = 0f;
        if (v.magnitude > 20f) v = Vector3.zero;                 // a teleport (level change, respawn)
        velocity = Vector3.Lerp(velocity, v, Motion.Damp(10f, dt));
        float speed = velocity.magnitude;
        float gait = Mathf.Clamp01(speed / (legLen * 2.2f));      // 0 standing .. 1 running
        gaitNow = gait;

        // ---------------------------------------------------------------- body turns toward where they look
        float camYaw = p.HeadRotation.eulerAngles.y;
        float diff = Mathf.DeltaAngle(bodyYaw, camYaw);
        // walking: the body faces where they look; standing: it only turns once the head has turned far enough,
        // then most of the way (a held target, so it doesn't start and stop at the threshold)
        if (speed > .25f * scale) yawTarget = camYaw;
        else if (Mathf.Abs(Mathf.DeltaAngle(yawTarget, camYaw)) > 45f) yawTarget = camYaw - Mathf.Sign(diff) * 15f;
        // critically damped spring: turns start and stop softly
        const float yawK = 45f;
        float yawErr = Mathf.DeltaAngle(bodyYaw, yawTarget);
        yawVelocity += (yawErr * yawK - yawVelocity * 2f * Mathf.Sqrt(yawK)) * Mathf.Min(dt, 1f / 30f);
        bodyYaw += yawVelocity * dt;
        var rootRot = Quaternion.Euler(0f, bodyYaw, 0f);
        var root = Root.transform;
        root.SetPositionAndRotation(ground, rootRot);
        root.localScale = Vector3.one * scale;

        // ---------------------------------------------------------------- feet
        Vector3 fwd = rootRot * Vector3.forward, right = rootRot * Vector3.right;
        float stepTime = Mathf.Lerp(.34f, .2f, gait);
        // a foot swings for one step and stands for one (while the other swings): aiming 1.5 steps of travel ahead
        // lands it half a step in front of the body and lifts it half a step behind - a symmetric stride that
        // neither trails nor over-reaches
        Vector3 lead = velocity * (stepTime * 1.5f);
        Vector3 homeL = root.TransformPoint(new Vector3(restRootPos[IndexOf(footL)].x, 0f, restRootPos[IndexOf(footL)].z));
        Vector3 homeR = root.TransformPoint(new Vector3(restRootPos[IndexOf(footR)].x, 0f, restRootPos[IndexOf(footR)].z));
        if (!feetPlaced) { feetPlaced = true; stepL.Reset(homeL); stepR.Reset(homeR); footYawL = footYawR = yawFromL = yawToL = yawFromR = yawToR = bodyYaw; }
        if ((stepL.Planted - homeL).sqrMagnitude > legLen * legLen * 9f) { stepL.Reset(homeL); stepR.Reset(homeR); }   // teleported
        float thresh = speed > .05f * scale ? legLen * .1f : legLen * .28f;
        Vector3 wantL = homeL + lead, wantR = homeR + lead;
        // each foot keeps to its own side of the body and of the other foot: no crossing on turns
        float halfStance = Mathf.Abs(restRootPos[IndexOf(footL)].x) * scale;
        wantL = KeepSide(wantL, ground, right, -1f, halfStance, stepR.Planted);
        wantR = KeepSide(wantR, ground, right, +1f, halfStance, stepL.Planted);
        wantL.y = GroundY(wantL, ground.y, legLen);
        wantR.y = GroundY(wantR, ground.y, legLen);
        // the foot that's furthest behind goes first; turning on the spot also steps
        float errL = Vector3.ProjectOnPlane(wantL - stepL.Planted, Vector3.up).magnitude + (Mathf.Abs(Mathf.DeltaAngle(footYawL, bodyYaw)) > 40f ? legLen : 0f);
        float errR = Vector3.ProjectOnPlane(wantR - stepR.Planted, Vector3.up).magnitude + (Mathf.Abs(Mathf.DeltaAngle(footYawR, bodyYaw)) > 40f ? legLen : 0f);
        bool startedL = false, startedR = false;
        // one foot swings at a time - unless a planted foot is getting stretched out behind (a sudden start or turn):
        // then it may lift once the other is past the middle of its swing, like a quick first stride
        bool overL = Vector3.ProjectOnPlane(stepL.Planted - homeL, Vector3.up).magnitude > legLen * .6f;
        bool overR = Vector3.ProjectOnPlane(stepR.Planted - homeR, Vector3.up).magnitude > legLen * .6f;
        bool blockL = stepR.Swinging && !(overL && stepR.Progress > .5f);
        bool blockR = stepL.Swinging && !(overR && stepL.Progress > .5f);
        if (errL >= errR) { startedL = stepL.TryStep(wantL, thresh, blockL, stepTime); if (!startedL) startedR = stepR.TryStep(wantR, thresh, blockR, stepTime); }
        else { startedR = stepR.TryStep(wantR, thresh, blockR, stepTime); if (!startedR) startedL = stepL.TryStep(wantL, thresh, blockL, stepTime); }
        if (startedL) { yawFromL = footYawL; yawToL = bodyYaw; }
        if (startedR) { yawFromR = footYawR; yawToR = bodyYaw; }
        float lift = legLen * (.1f + .1f * gait);
        bool wasL = stepL.Swinging, wasR = stepR.Swinging;
        Vector3 fL = stepL.Tick(dt, lift, out float arcL);
        Vector3 fR = stepR.Tick(dt, lift, out float arcR);
        LandedL = wasL && !stepL.Swinging;
        LandedR = wasR && !stepR.Swinging;
        footYawL = stepL.Swinging || startedL ? Mathf.LerpAngle(yawFromL, yawToL, Smooth01(stepL.Progress)) : yawToL;
        footYawR = stepR.Swinging || startedR ? Mathf.LerpAngle(yawFromR, yawToR, Smooth01(stepR.Progress)) : yawToR;
        if (stepL.Swinging || startedL) swing = Mathf.Lerp(swing, 1f, Motion.Damp(8f, dt));
        if (stepR.Swinging || startedR) swing = Mathf.Lerp(swing, -1f, Motion.Damp(8f, dt));

        // ---------------------------------------------------------------- hips: height, bob, lean, sway
        breathPhase += dt * (1.6f + 2.5f * gait);
        float breath = Mathf.Sin(breathPhase);
        float bob = gait * (Mathf.Max(arcL, arcR) - .6f) * .045f;
        float drop = crouch * data.HipHeight * .9f + (1f - gait) * 0f;
        var stance = arcL > arcR ? fR : fL;
        float sway = Mathf.Clamp(Vector3.Dot(stance - ground, right) / scale, -.08f, .08f) * .35f * (1f - crouch);
        hipSpring.Step(new Vector3(sway, bob, 0f), 120f, 2f * Mathf.Sqrt(120f), dt);   // no kinks between steps
        sway = hipSpring.Value.x; bob = hipSpring.Value.y;
        Vector3 localVel = Quaternion.Inverse(rootRot) * velocity / Mathf.Max(scale, .01f);
        float leanFwd = Mathf.Clamp(localVel.z * 4f, -6f, 12f) + crouch * 25f;
        if (holdingTool)
        {
            // reaching further than an arm (the sponge out on a surface): lean into it
            Vector3 shoulder = ground + Vector3.up * (restRootPos[IndexOf(armR)].y * scale);
            float excess = Vector3.Distance(shoulder, p.Tool) / (data.ArmLength * scale) - .9f;
            reachLean = Mathf.Lerp(reachLean, Mathf.Clamp01(excess) * 18f, Motion.Damp(6f, dt));
        }
        else reachLean = Mathf.Lerp(reachLean, 0f, Motion.Damp(6f, dt));
        leanFwd += reachLean;
        float leanSide = Mathf.Clamp(-yawVelocity * .03f, -8f, 8f) - sway * 30f;
        var lean = Quaternion.Euler(leanFwd, 0f, leanSide);
        hips.localRotation = lean * data.LocalRotations[IndexOf(hips)];
        // the rest pose has straight legs: the hips settle just low enough that both ankles can be reached with the
        // knees a little bent, measured on the real hip joints (after lean), or the IK would drag a planted foot
        Vector3 hipsBase = restRootPos[IndexOf(hips)] + new Vector3(sway, -drop + bob + breath * .003f, 0f);
        hips.localPosition = hipsBase - new Vector3(0f, hipDrop, 0f);
        float extra = 0f, spare = float.PositiveInfinity;
        foreach (var (upperLeg, footBone, fp) in new[] { (legL, footL, fL), (legR, footR, fR) })
        {
            Vector3 hj = upperLeg.position;
            Vector3 ankle = fp + Vector3.up * (restRootPos[IndexOf(footBone)].y * scale);
            float reach = legLen * .96f;
            float horiz = Vector3.ProjectOnPlane(hj - ankle, Vector3.up).magnitude;
            float allowed = Mathf.Sqrt(Mathf.Max(0f, reach * reach - horiz * horiz));
            float over = (hj.y - ankle.y) - allowed;              // > 0: too high to reach that ankle
            extra = Mathf.Max(extra, over);
            spare = Mathf.Min(spare, -over);
        }
        if (extra > 0f) hipDrop += extra / scale;                // at once: a planted foot must never be dragged
        else hipDrop = Mathf.Max(0f, hipDrop - Mathf.Min(spare / scale, hipDrop) * Motion.Damp(5f, dt));   // rise gently
        hipDrop = Mathf.Min(hipDrop, data.LegLength * .35f);
        hips.localPosition = hipsBase - new Vector3(0f, hipDrop, 0f);

        // ---------------------------------------------------------------- spine, neck, head: look where the camera looks
        var look = Quaternion.Inverse(rootRot * lean) * p.HeadRotation;
        Vector3 lf = look * Vector3.forward;
        float lookYaw = Mathf.Clamp(Mathf.Atan2(lf.x, lf.z) * Mathf.Rad2Deg, -80f, 80f);
        float lookPitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(lf.y, -1f, 1f)) * Mathf.Rad2Deg, -55f, 60f);
        float counter = -swing * 6f * gait;                          // shoulders twist against the hips when walking
        var chain = new (Transform bone, float share)[] { (spine, .12f), (chest, .3f), (neck, .6f), (head, 1f) };
        foreach (var (bone, share) in chain)
        {
            float twist = bone == chest ? counter : bone == spine ? counter * .5f : 0f;
            float pitch = lookPitch * share + (bone == chest ? breath * .8f : 0f) - (bone == head ? crouch * 10f : 0f)
                        - yip * (bone == head ? 13f : bone == neck ? 6f : 0f);      // chin up to yip
            bone.rotation = rootRot * lean * Quaternion.Euler(pitch, lookYaw * share + twist, 0f) * restRootRot[IndexOf(bone)];
        }

        // ---------------------------------------------------------------- legs
        Vector3 up = Vector3.up;
        LegIK(legL, shinL, footL, fL, footYawL, stepL.Swinging ? stepL.Progress : 1f, fwd, up, lean);
        LegIK(legR, shinR, footR, fR, footYawR, stepR.Swinging ? stepR.Progress : 1f, fwd, up, lean);

        // ---------------------------------------------------------------- arms
        Vector3 down = Vector3.down;
        float arm = data.ArmLength * scale;
        // left: reaches for their real hand (the plapper), wherever it is - but never locks the elbow straight
        Motion.TwoBone(armL, foreL, handL, Reach(armL.position, p.Hand, arm), armL.position + (-fwd * .6f + down * .5f - right * .6f) * arm);
        // right: holds the tool if they have one, otherwise hangs and swings with the walk
        Vector3 rightTarget;
        if (holdingTool) rightTarget = p.Tool;
        else
        {
            Vector3 hang = armR.position + down * (arm * .92f) + right * (arm * .12f) + fwd * (arm * .1f);
            rightTarget = hang + fwd * (swing * gait * arm * .35f);
        }
        Motion.TwoBone(armR, foreR, handR, Reach(armR.position, rightTarget, arm), armR.position + (-fwd * .6f + down * .5f + right * .6f) * arm);
        if (!holdingTool)   // relaxed wrist: palm toward the thigh
            handR.rotation = Quaternion.AngleAxis(-70f, foreR.position - armR.position) * handR.rotation;
        Curl(fingersL, handL, 15f + 10f * breath * .3f);
        Curl(fingersR, handR, holdingTool ? 72f : 22f);

        // ---------------------------------------------------------------- tail: trails the turn, lifts when running
        Vector3 tailTarget = new(-8f + 10f * gait - crouch * 10f, Mathf.Clamp(-yawVelocity * .12f, -35f, 35f), 0f);
        tailSpring.Step(tailTarget, 30f, 7f, dt);
        float t = Time.time + seed * 10f;
        for (int i = 0; i < tail.Length; i++)
        {
            float k = (i + 1f) / tail.Length;
            float excited = Mathf.Clamp01(1f - yipAge / 1.6f);           // a happy wag after a yip
            float wag = Mathf.Sin(t * (1.1f + 2.4f * gait + 5f * excited) - i * .7f) * (4f + 10f * gait + 12f * excited) * k;
            var bend = Quaternion.Euler(tailSpring.Value.x * k, tailSpring.Value.y * k + wag, 0f);
            tail[i].rotation = rootRot * lean * bend * restRootRot[IndexOf(tail[i])];
        }

        // ---------------------------------------------------------------- ears: flop with how fast the head turns
        Vector3 hf = head.forward;
        Vector3 turn = Vector3.Cross(lastHeadForward, hf) / dt;     // head angular velocity (rad/s)
        lastHeadForward = hf;
        var earKick = new Vector3(Vector3.Dot(turn, head.right) * -8f, 0f, Vector3.Dot(turn, head.up) * 10f);
        earSpringL.Step(earKick + new Vector3(-6f * gait, 0f, 0f), 60f, 9f, dt);
        earSpringR.Step(earKick + new Vector3(-6f * gait, 0f, 0f), 60f, 9f, dt);
        if (earL != null) earL.rotation = Quaternion.AngleAxis(earSpringL.Value.x, head.right) * Quaternion.AngleAxis(earSpringL.Value.z, head.forward) * earL.rotation;
        if (earR != null) earR.rotation = Quaternion.AngleAxis(earSpringR.Value.x, head.right) * Quaternion.AngleAxis(-earSpringR.Value.z, head.forward) * earR.rotation;

        // ---------------------------------------------------------------- face: blinks, a friendly look, squinting at work
        if (Time.time >= nextBlink) { blinkT = 0f; nextBlink = Time.time + 2.2f + ((seed * 7.1f + Time.time * .37f) % 1f) * 3.5f; }
        blinkT += dt / .16f;
        float lid = blinkT < 1f ? Mathf.Sin(blinkT * Mathf.PI) : 0f;
        if (blink >= 0) body.SetBlendShapeWeight(blink, lid * 100f);
        if (happy >= 0) body.SetBlendShapeWeight(happy, Mathf.Max(35f + 20f * gait, 100f * yip));
        if (squint >= 0) body.SetBlendShapeWeight(squint, usingTool ? 45f * (1f - yip) : 0f);
        if (mouthOpen >= 0) body.SetBlendShapeWeight(mouthOpen, Mathf.Max(gait * 35f + (usingTool ? 10f : 0f), 95f * yip));
        jaw.rotation = Quaternion.AngleAxis(gait * 6f + yip * 16f, head.right) * jaw.rotation;
    }

    /// <summary>A target the arm can reach with a little bend left in the elbow.</summary>
    private static Vector3 Reach(Vector3 shoulder, Vector3 target, float armLength)
    {
        Vector3 d = target - shoulder;
        float max = armLength * .93f;
        return d.magnitude > max ? shoulder + d.normalized * max : target;
    }

    /// <summary>Keep a foot target on its own side (side -1 left, +1 right) of the body line and of the other foot.</summary>
    private static Vector3 KeepSide(Vector3 want, Vector3 ground, Vector3 right, float side, float halfStance, Vector3 otherFoot)
    {
        float lat = Vector3.Dot(want - ground, right) * side;
        float minLat = halfStance * .45f;
        if (lat < minLat) want += right * side * (minLat - lat);
        float fromOther = Vector3.Dot(want - otherFoot, right) * side;
        float minGap = halfStance * .9f;
        if (fromOther < minGap) want += right * side * (minGap - fromOther);
        return want;
    }

    private static float Smooth01(float u) { u = Mathf.Clamp01(u); return u * u * (3f - 2f * u); }

    private void LegIK(Transform upper, Transform lower, Transform foot, Vector3 footPos, float footYaw, float progress,
                       Vector3 fwd, Vector3 up, Quaternion lean)
    {
        int fi = IndexOf(foot);
        // the ankle sits above the sole by its rest height
        Vector3 ankle = footPos + up * (restRootPos[fi].y * scale);
        if (foot == footL) AnkleTargetL = ankle; else AnkleTargetR = ankle;
        Motion.TwoBone(upper, lower, foot, ankle, upper.position + (fwd * 1.2f + Vector3.down * .2f) * data.LegLength * scale);
        // flat on the ground, pointing the way it was planted; heel lifts first (toes down), toes rise before landing
        float pitch = progress < 1f ? Mathf.Sin(progress * Mathf.PI * 2f) * -12f : 0f;
        foot.rotation = Quaternion.Euler(0f, footYaw, 0f) * Quaternion.Euler(-pitch, 0f, 0f) * restRootRot[fi];
    }

    private void Curl(List<(Transform bone, Vector3 axisRest, float share)> fingers, Transform hand, float degrees)
    {
        // the hand's rotation away from its rest, applied to each finger's rest curl axis
        var handDelta = hand.rotation * Quaternion.Inverse(Root.transform.rotation * restRootRot[IndexOf(hand)]);
        foreach (var (bone, axisRest, share) in fingers)
            bone.rotation = Quaternion.AngleAxis(degrees * share, handDelta * (Root.transform.rotation * axisRest)) * bone.rotation;
    }

    private readonly Dictionary<Transform, int> indexCache = new();

    private int IndexOf(Transform bone)
    {
        if (indexCache.TryGetValue(bone, out int i)) return i;
        i = Array.IndexOf(bones, bone);
        indexCache[bone] = i;
        return i;
    }
}
