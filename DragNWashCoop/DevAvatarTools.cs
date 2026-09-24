using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DragNWashCoop;

/// <summary>
/// Local-test developer options for the player avatars (only with -coop-local*):
///   -coop-dev-walk          this copy's *sent* pose walks a circle, then stands and looks around (the real player
///                           doesn't move): the other copy sees a kobold walking, over the real network path.
///   -coop-avatar-shot DIR   once a friend's kobold has been seen for a few seconds, photograph it from a separate
///                           camera at timed moments (front 3/4, side, back), log its pose; with -coop-shot-quit, quit.
///   -coop-dev-yip           this copy yips once a cycle of -coop-dev-walk, while standing (the other copy photographs it)
///   -coop-dev-tool NAME     this copy picks up that tool (e.g. Sprayer, Sponge) and, with -coop-dev-walk, uses it
///                           while standing: the other copy sees the kobold hold and work it.
/// </summary>
internal static class DevAvatarTools
{
    internal static bool Walk;
    internal static bool YipCycle;
    private static int lastYipCycle = -1;
    internal static string ToolName = string.Empty;
    private static float nextEquipTry;
    private static bool toolInUse;
    internal static string ShotDir = string.Empty;
    internal static bool QuitAfter;
    internal static string TracePath = string.Empty;
    private static StreamWriter? trace;
    private static float traceStart = -1f;
    private static float walkStart = -1f;
    private static Vector3 walkAnchor;
    private static float anchorYaw;

    internal static void Parse(string[] args)
    {
        Run = Array.IndexOf(args, "-coop-dev-run") >= 0;
        Walk = Run || Array.IndexOf(args, "-coop-dev-walk") >= 0;
        YipCycle = Array.IndexOf(args, "-coop-dev-yip") >= 0;
        int i = Array.IndexOf(args, "-coop-avatar-shot");
        if (i >= 0 && i + 1 < args.Length) ShotDir = args[i + 1];
        QuitAfter = Array.IndexOf(args, "-coop-shot-quit") >= 0;
        int k = Array.IndexOf(args, "-coop-avatar-trace");
        if (k >= 0 && k + 1 < args.Length) TracePath = args[k + 1];
        float Arg(string name) { int a = Array.IndexOf(args, name); return a >= 0 && a + 1 < args.Length &&
            float.TryParse(args[a + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 0f; }
        Loss = Mathf.Clamp01(Arg("-coop-dev-loss"));
        Jitter = Mathf.Clamp(Arg("-coop-dev-jitter"), 0f, 1f);
        int dt = Array.IndexOf(args, "-coop-dragon-trace");
        if (dt >= 0 && dt + 1 < args.Length) DragonTracePath = args[dt + 1];
        int d = Array.IndexOf(args, "-coop-dragon-shot");
        if (d >= 0 && d + 1 < args.Length) DragonShotDir = args[d + 1];
        int j = Array.IndexOf(args, "-coop-dev-tool");
        if (j >= 0 && j + 1 < args.Length) ToolName = args[j + 1];
    }

    /// <summary>-coop-dev-yip: true once per fake-walk cycle, 9.5 s in (standing, facing about the same way).</summary>
    internal static bool YipNow()
    {
        if (!YipCycle || walkStart < 0f) return false;
        float total = Time.unscaledTime - walkStart;
        int cycle = Mathf.FloorToInt(total / 13f);
        if (cycle == lastYipCycle || total % 13f < 9.5f) return false;
        lastYipCycle = cycle;
        return true;
    }

    private static readonly System.Reflection.FieldInfo ManagerField =
        HarmonyLib.AccessTools.Field(typeof(com.gatordragongames.washnwalk.tools.ToolManager), "_instance");

    /// <summary>-coop-dev-tool: hold that tool (re-taken if the game swaps it), used while the fake walk stands.</summary>
    internal static void TickTool()
    {
        if (ToolName.Length == 0 || UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "PlayGame" ||
            !com.gatordragongames.washnwalk.tools.ToolManager.IsValid()) return;
        var current = com.gatordragongames.washnwalk.tools.ToolManager.GetCurrentTool();
        if ((current == null || current.name != ToolName) && Time.unscaledTime >= nextEquipTry)
        {
            nextEquipTry = Time.unscaledTime + 1f;
            var tool = System.Linq.Enumerable.FirstOrDefault(Resources.FindObjectsOfTypeAll<com.gatordragongames.washnwalk.tools.Tool>(), t => t.name == ToolName);
            if (tool != null) { com.gatordragongames.washnwalk.tools.ToolManager.EquipTool(tool); toolInUse = false; }
            return;
        }
        if (current == null || current.name != ToolName) return;
        bool use = Walk && walkStart >= 0f && (Time.unscaledTime - walkStart) % 13f is > 9f and < 12f;
        if (use == toolInUse || ManagerField.GetValue(null) is not com.gatordragongames.washnwalk.tools.ToolManager manager) return;
        toolInUse = use;
        if (use) manager.StartUseTool(); else manager.StopUseTool();
    }

    /// <summary>
    /// Rewrites the pose about to be sent: 8 s walking a 2.2 m circle, 5 s standing and looking left/right/up, repeat.
    /// Everything (head, hand, tool, feet) moves together, as if the player walked.
    /// </summary>
    internal static void FakeWalk(ref Vector3 head, ref Quaternion headRot, ref Vector3 hand, ref Quaternion handRot,
                                  ref Vector3 tool, ref Quaternion toolRot, ref FeetPose feet)
    {
        if (!Walk || !feet.Has) return;
        if (walkStart < 0f) { walkStart = Time.unscaledTime; walkAnchor = runPos = feet.Position; anchorYaw = runYaw = headRot.eulerAngles.y; runLast = Time.unscaledTime; }
        float total = Time.unscaledTime - walkStart;
        float t = total % 13f;
        int laps = Mathf.FloorToInt(total / 13f);
        const float radius = 2.2f, speed = 1.3f;
        float yaw, lookYaw = 0f, lookPitch = 0f;
        Vector3 ground;
        if (Run) PlayerLikeRun(t, out ground, out yaw);
        else
        {
            float walked = (laps * 8f + Mathf.Min(t, 8f)) * speed / radius;  // radians along the circle, continuing
            ground = walkAnchor + new Vector3(Mathf.Sin(walked) * radius, 0f, radius - Mathf.Cos(walked) * radius);
            yaw = walked * Mathf.Rad2Deg;                                    // tangent: starts heading +Z, turns right
        }
        if (t > 8f)
        {
            float s = t - 8f;                                            // standing: look left, right, up, back
            float fade = Mathf.Sin(Mathf.Clamp01(s / 5f) * Mathf.PI);      // eases in and out of the look-around
            lookYaw = Mathf.Sin(s * 1.3f) * 70f * fade;
            lookPitch = -(Mathf.Sin(s * 2.1f) * .5f + .5f) * 30f * fade;
        }
        if (Run && t > 8f) lookYaw *= .5f;   // the run already turns a lot; keep its look-around smaller
        var oldGround = feet.Position;
        var turn = Quaternion.Euler(0f, yaw + lookYaw - headRot.eulerAngles.y, 0f);
        var bodyTurn = Quaternion.Euler(0f, yaw - feet.Rotation.eulerAngles.y, 0f);
        Vector3 Move(Vector3 world) => ground + turn * (world - oldGround);
        head = Move(head);
        hand = Move(hand);
        tool = Move(tool);
        headRot = Quaternion.Euler(lookPitch, 0f, 0f) * turn * headRot;
        handRot = turn * handRot;
        toolRot = turn * toolRot;
        feet.Position = ground;
        feet.Rotation = bodyTurn * feet.Rotation;
    }

    // -coop-dev-run: the moves of a real player, at the game's walking speed (5 m/s, 35 m/s² acceleration)
    internal static bool Run;
    private static Vector3 runPos, runVel;
    private static float runYaw, runLast;

    /// <summary>
    /// The first 8 s of a -coop-dev-run cycle, in camera space: run, stop dead, flick the mouse round, strafe right,
    /// backpedal, strafe left, run while turning, stop, flick back. A weak pull toward the start keeps it from drifting.
    /// </summary>
    private static void PlayerLikeRun(float t, out Vector3 ground, out float yaw)
    {
        float dt = Mathf.Clamp(Time.unscaledTime - runLast, 0f, .1f);
        runLast = Time.unscaledTime;
        Vector3 move = Vector3.zero;   // (right, 0, forward) in camera space, m/s
        float yawRate = 0f;            // degrees per second
        if (t < 1f) { move = new(0f, 0f, 5f); yawRate = 30f; }
        else if (t < 1.5f) { }
        else if (t < 2.1f) { if (t is > 1.6f and < 1.9f) yawRate = -370f; }      // a quick flick left
        else if (t < 3.1f) move = new(5f, 0f, 0f);
        else if (t < 3.4f) { }
        else if (t < 4.4f) move = new(0f, 0f, -5f);
        else if (t < 5.4f) move = new(-5f, 0f, 0f);
        else if (t < 6.6f) { move = new(0f, 0f, 5f); yawRate = -60f; }
        else if (t < 7f) { }
        else if (t < 7.6f) yawRate = 180f;
        if (t < 8f) runYaw += yawRate * dt;
        Vector3 want = Quaternion.Euler(0f, runYaw, 0f) * move + (walkAnchor - runPos) * .3f;
        runVel = Vector3.MoveTowards(runVel, want, 35f * dt);
        runPos += runVel * dt;
        ground = runPos;
        yaw = runYaw;
    }

    /// <summary>
    /// -coop-avatar-trace FILE: every frame, the first friend's kobold as CSV (time, frame time, root / head / hips /
    /// left hand / feet positions, which feet are swinging, body yaw, snapshot buffer margin), for 30 s.
    /// </summary>
    internal static void TickTrace(RemoteHands actors)
    {
        if (TracePath.Length == 0) return;
        var a = actors.FirstAvatar();
        var buf = actors.FirstBuffer();
        if (a == null || buf == null) return;
        if (trace == null)
        {
            trace = new StreamWriter(TracePath);
            traceStart = Time.unscaledTime;
            trace.WriteLine("t,dt,rx,ry,rz,hx,hy,hz,px,py,pz,lx,ly,lz,flx,fly,flz,frx,fry,frz,swl,swr,yaw,margin,tlx,tly,tlz,trx,try,trz," +
                            "klx,kly,klz,krx,kry,krz,hrx,hry,hrz,gx,gy,gz,fyl,fyr,gait,tool");
        }
        float t = Time.unscaledTime - traceStart;
        if (t > 30f) { trace.Close(); TracePath = string.Empty; trace = null; return; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string V(Vector3 v) => string.Format(inv, "{0:F5},{1:F5},{2:F5}", v.x, v.y, v.z);
        trace.WriteLine(string.Format(inv, "{0:F4},{1:F5},", t, Time.unscaledDeltaTime) +
                        V(a.Root.transform.position) + "," + V(a.Head.position) + "," + V(a.Hips.position) + "," +
                        V(a.HandL.position) + "," + V(a.FootL.position) + "," + V(a.FootR.position) + "," +
                        (a.SwingingL ? 1 : 0) + "," + (a.SwingingR ? 1 : 0) + "," +
                        string.Format(inv, "{0:F3},{1:F4},", a.BodyYaw, buf.LastMargin) + V(a.AnkleTargetL) + "," + V(a.AnkleTargetR) + "," +
                        V(a.KneeL.position) + "," + V(a.KneeR.position) + "," + V(a.HandR.position) + "," + V(a.RightTarget) + "," +
                        string.Format(inv, "{0:F2},{1:F2},{2:F3},{3}", a.FootYawL, a.FootYawR, a.Gait, a.HoldingTool ? 1 : 0));
    }

    // -coop-dev-loss F / -coop-dev-jitter S: this copy's unreliable packets (poses) are dropped with chance F and delayed by
    // up to S seconds, as over a poor internet connection
    internal static float Loss, Jitter;
    private static readonly System.Random chaos = new();
    internal static bool DropPose() => Loss > 0f && chaos.NextDouble() < Loss;
    internal static float PoseDelay() => Jitter > 0f ? (float)chaos.NextDouble() * Jitter : 0f;

    // -coop-dragon-trace CSV: Ryan as this copy draws him, every frame for 30 s (root, a bone mid-body)
    internal static string DragonTracePath = string.Empty;
    private static StreamWriter? dragonTrace;
    private static float dragonTraceStart;

    internal static void TickDragonTrace()
    {
        if (DragonTracePath.Length == 0 || UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "PlayGame" ||
            !WalkNWashSceneState.TryGetActiveDragon(out var dragon) || dragon.skin == null) return;
        if (dragonTrace == null)
        {
            dragonTrace = new StreamWriter(DragonTracePath);
            dragonTraceStart = Time.unscaledTime;
            dragonTrace.WriteLine("t,dt,rx,ry,rz,bx,by,bz");
        }
        float t = Time.unscaledTime - dragonTraceStart;
        if (t > 30f) { dragonTrace.Close(); dragonTrace = null; DragonTracePath = string.Empty; return; }
        var bones = dragon.skin.bones;
        var bone = bones.Length > 0 ? bones[bones.Length / 2] : dragon.skin.transform;
        var r = dragon.gameObject.transform.position; var b = bone != null ? bone.position : r;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        dragonTrace.WriteLine(string.Format(inv, "{0:F4},{1:F5},{2:F4},{3:F4},{4:F4},{5:F4},{6:F4},{7:F4}", t, Time.unscaledDeltaTime, r.x, r.y, r.z, b.x, b.y, b.z));
        dragonTrace.Flush();
    }

    // -coop-dragon-shot DIR: photograph Ryan as this copy draws him (paint, pose), front and side, 6 s and 12 s after he appears
    internal static string DragonShotDir = string.Empty;
    private static float dragonSeen = -1f;
    private static int dragonShot;
    private static readonly (float at, string view)[] DragonSchedule = { (6f, "front"), (6.4f, "side"), (12f, "front"), (12.4f, "side") };

    internal static void TickDragonShots(BepInEx.Logging.ManualLogSource log)
    {
        if (DragonShotDir.Length == 0 || dragonShot >= DragonSchedule.Length) return;
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "PlayGame" ||
            !WalkNWashSceneState.TryGetActiveDragon(out var dragon) || dragon.skin == null) return;
        if (dragonSeen < 0f) { dragonSeen = Time.unscaledTime; Directory.CreateDirectory(DragonShotDir); log.LogInfo("Dragon shots: Ryan seen"); }
        var (at, view) = DragonSchedule[dragonShot];
        if (Time.unscaledTime - dragonSeen < at) return;
        dragonShot++;
        var bounds = dragon.skin.bounds;
        var t = dragon.gameObject.transform;
        float size = bounds.extents.magnitude;
        Vector3 from = bounds.center + (view == "side" ? t.right : t.forward) * (size * 2f) + Vector3.up * (size * .25f);
        string path = Path.Combine(DragonShotDir, $"dragon_{dragonShot:00}_{view}.png");
        try { Photo(from, bounds.center, path); log.LogInfo($"Dragon shot {Path.GetFileName(path)}"); }
        catch (Exception e) { log.LogWarning($"Dragon shot failed: {e}"); }
        if (dragonShot >= DragonSchedule.Length && QuitAfter && ShotDir.Length == 0) Application.Quit();
    }

    /// <summary>Render the scene from a separate camera to a PNG.</summary>
    private static void Photo(Vector3 from, Vector3 target, string path)
    {
        var main = Camera.main;
        var go = new GameObject("DevPhotoCamera");
        var cam = go.AddComponent<Camera>();
        if (main != null) cam.CopyFrom(main);
        cam.fieldOfView = 45f;
        cam.nearClipPlane = .05f;
        go.transform.position = from;
        go.transform.rotation = Quaternion.LookRotation(target - from, Vector3.up);
        var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
        cam.targetTexture = rt;
        cam.Render();
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        File.WriteAllBytes(path, tex.EncodeToPNG());
        cam.targetTexture = null;
        UnityEngine.Object.Destroy(rt);
        UnityEngine.Object.Destroy(tex);
        UnityEngine.Object.Destroy(go);
    }

    private static float firstSeen = -1f;
    private static int shotIndex;
    private static readonly (float at, string view)[] Schedule =
    {
        (4f, "front34"), (4.25f, "front34"), (4.5f, "front34"), (4.75f, "front34"), (5f, "side"), (5.3f, "side"),
        (5.6f, "side"), (9.5f, "front34"), (10.2f, "front"), (11f, "back"), (12f, "front34_close"),
    };

    private static float useSeen = -1f;
    private static int useShot;
    private static int yipShots;
    private static readonly (float at, string view)[] UseSchedule = { (.7f, "front34_close"), (1.3f, "side"), (2f, "front34") };

    /// <summary>
    /// Photograph the first friend's kobold on schedule, and (if they hold a tool) the first time they use it. With
    /// -coop-shot-quit, quit when done (waiting up to 40 s for a tool friend to use it).
    /// </summary>
    internal static void TickShots(RemoteHands actors, BepInEx.Logging.ManualLogSource log)
    {
        if (ShotDir.Length == 0) return;
        var avatar = actors.FirstAvatar();
        if (avatar == null) return;
        if (firstSeen < 0f) { firstSeen = Time.unscaledTime; Directory.CreateDirectory(ShotDir); log.LogInfo("Avatar shots: friend's kobold seen"); }
        bool holding = actors.FirstHolding(out bool usingTool);
        float yipAge = actors.FirstYipAge();
        if (yipShots < 2 && yipAge >= (yipShots == 0 ? .08f : .3f) && yipAge < 1f)
        {
            yipShots++;
            try { Shoot(actors, avatar, "front34_close", Path.Combine(ShotDir, $"avatar_yip_{yipShots:00}.png"), log); }
            catch (Exception e) { log.LogWarning($"Avatar shot failed: {e}"); }
        }
        if (usingTool && useSeen < 0f) { useSeen = Time.unscaledTime; log.LogInfo("Avatar shots: friend is using their tool"); }
        if (useSeen >= 0f && useShot < UseSchedule.Length && Time.unscaledTime - useSeen >= UseSchedule[useShot].at)
        {
            var (_, v) = UseSchedule[useShot++];
            try { Shoot(actors, avatar, v, Path.Combine(ShotDir, $"avatar_use_{useShot:00}_{v}.png"), log); }
            catch (Exception e) { log.LogWarning($"Avatar shot failed: {e}"); }
        }
        if (shotIndex < Schedule.Length && Time.unscaledTime - firstSeen >= Schedule[shotIndex].at)
        {
            var (_, view) = Schedule[shotIndex++];
            try { Shoot(actors, avatar, view, Path.Combine(ShotDir, $"avatar_{shotIndex:00}_{view}.png"), log); }
            catch (Exception e) { log.LogWarning($"Avatar shot failed: {e}"); }
        }
        bool useDone = !holding || useShot >= UseSchedule.Length || Time.unscaledTime - firstSeen > 40f;
        if (QuitAfter && shotIndex >= Schedule.Length && useDone && (yipShots >= 2 || Time.unscaledTime - firstSeen > 40f))
        {
            log.LogInfo($"Avatar shots done; friend's footsteps: {PlayerLooks.Footsteps} ({PlayerLooks.FootstepSounds} with a floor sound)");
            Application.Quit();
        }
    }

    private static void Shoot(RemoteHands actors, KoboldAvatar avatar, string view, string path, BepInEx.Logging.ManualLogSource log)
    {
        var main = Camera.main;
        var root = avatar.Root.transform;
        float h = avatar.Head.position.y - root.position.y;                 // about eye height
        Vector3 fwd = root.forward, right = root.right, up = Vector3.up;
        Vector3 target = root.position + up * h * .62f;
        Vector3 offset = view switch
        {
            "side" => right * 2.6f * h + up * .15f * h,
            "front" => fwd * 2.6f * h + up * .2f * h,
            "back" => -fwd * 2.6f * h + right * .6f * h + up * .35f * h,
            "front34_close" => (fwd * 1.3f + right * .9f) * h + up * .35f * h,
            _ => (fwd * 2.2f + right * 1.4f) * h + up * .3f * h,
        };
        if (view == "front34_close") target = avatar.Head.position - up * h * .1f;
        var go = new GameObject("DevAvatarPhotoCamera");
        var cam = go.AddComponent<Camera>();
        if (main != null) cam.CopyFrom(main);
        cam.fieldOfView = 45f;
        cam.nearClipPlane = .05f;
        go.transform.position = target + offset;
        go.transform.rotation = Quaternion.LookRotation(target - go.transform.position, Vector3.up);
        actors.FaceLabels(cam);   // as they'd face this camera if it were yours
        var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32);
        cam.targetTexture = rt;
        cam.Render();
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
        tex.Apply();
        RenderTexture.active = prev;
        File.WriteAllBytes(path, tex.EncodeToPNG());
        cam.targetTexture = null;
        UnityEngine.Object.Destroy(rt);
        UnityEngine.Object.Destroy(tex);
        UnityEngine.Object.Destroy(go);
        log.LogInfo($"Avatar shot {Path.GetFileName(path)}: root {root.position} scale {root.localScale.x:0.00} head {avatar.Head.position}");
    }
}
