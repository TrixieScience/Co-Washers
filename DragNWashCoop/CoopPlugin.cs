using System;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace DragNWashCoop;

[BepInPlugin(Guid, "Co-Washers", "0.5.3")]
public sealed class CoopPlugin : BaseUnityPlugin
{
    public const string Guid = "trixiescience.dragnwashcoop";
    internal static CoopPlugin Instance { get; private set; } = null!;
    internal ManualLogSource Log => Logger;
    internal SteamSession Session { get; private set; } = null!;
    internal CoopWorld World { get; private set; } = null!;
    internal CoopUI Ui { get; private set; } = null!;
    /// <summary>Local test mode: copies of the game on this PC connect over localhost instead of Steam.</summary>
    internal bool LocalTest { get; private set; }
    private Harmony? harmony;
    private bool eggPatchAttempted;
    private InstanceLog? instanceLog;
    private string autoLocal = string.Empty;   // "host" or "guest" from the command line
    private float nextAutoLocal;
    private int autoLocalTries;
    private int autoStartSlot;                 // -coop-local-autostart <slot>: host starts once everyone is ready
    private bool autoReady;                    // -coop-local-join with -coop-local-ready: ready up once connected (scripted tests)
    private string menuShot = string.Empty;    // -coop-menu-shot <png>: developer option, screenshot of the main menu
    private bool menuShotUi;                   // -coop-shot-ui: with the co-op menu open
    private float menuShotAt = -1f;

    private void Awake()
    {
        Instance = this;
        Session = new SteamSession(this);
        World = new CoopWorld(this);
        Ui = new CoopUI(this);
        gameObject.AddComponent<CoopLatePose>();
        gameObject.AddComponent<CoopEarlyPose>();
        Session.PeerReady += World.OnPeerReady;
        Session.PeerLeft += World.OnPeerLeft;
        Session.PacketReceived += World.OnPacket;
        SceneManager.activeSceneChanged += World.OnSceneChanged;
        SetUpLocalTest();
        try
        {
            harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(CoopPlugin).Assembly);
            Log.LogInfo("Co-op plugin ready; open its menu with F7");
        }
        catch (Exception e) { Log.LogError($"Co-op patches failed: {e}"); }
    }

    private void SetUpLocalTest()
    {
        var enabled = Config.Bind("Local test", "Enabled", false,
            "Show Host/Join local test in the co-op menu so two copies of the game on this PC can play " +
            "together without Steam. For testing only.");
        var port = Config.Bind("Local test", "Port", 47713, "Localhost port used by the local test.");
        Session.LocalPort = port.Value;
        var args = Environment.GetCommandLineArgs();
        if (Array.IndexOf(args, "-coop-local-host") >= 0) autoLocal = "host";
        else if (Array.IndexOf(args, "-coop-local-join") >= 0) autoLocal = "guest";
        LocalTest = enabled.Value || autoLocal.Length > 0 || Array.IndexOf(args, "-coop-local") >= 0;
        if (!LocalTest) return;
        int shot = Array.IndexOf(args, "-coop-menu-shot");
        if (shot >= 0 && shot + 1 < args.Length) menuShot = args[shot + 1];
        menuShotUi = Array.IndexOf(args, "-coop-shot-ui") >= 0;
        int at = Array.IndexOf(args, "-coop-local-autostart");
        if (at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out int slot) && slot is >= 1 and <= 3) autoStartSlot = slot;
        autoReady = autoLocal == "guest" && Array.IndexOf(args, "-coop-local-ready") >= 0;
        DevAvatarTools.Parse(args);
        instanceLog = InstanceLog.Start(autoLocal.Length > 0 ? autoLocal : "pid" + System.Diagnostics.Process.GetCurrentProcess().Id);
        // the copy without focus has to keep running, or the other copy times out waiting for it
        bool wasInBackground = Application.runInBackground;
        Application.runInBackground = true;
        Log.LogInfo($"Local test mode on port {port.Value}{(autoLocal.Length > 0 ? $", starting as {autoLocal}" : "")}; " +
                    $"run in background was {wasInBackground}; this copy's log: {instanceLog?.FilePath ?? "(could not open)"}");
    }

    private void Update()
    {
        TryPatchEggMod();
        Session.Tick();
        TickAutoLocal();
        TickMenuShot();
        World.Tick();
        if (LocalTest) { DevAvatarTools.TickShots(World.Actors, Log); DevAvatarTools.TickTrace(World.Actors); DevAvatarTools.TickTool(); DevAvatarTools.TickDragonShots(Log); DevAvatarTools.TickDragonTrace(); if (DevAvatarTools.YipNow()) World.Yip(); }
        TickAvatarPrewarm();
        if (Keyboard.current?.f7Key.wasPressedThisFrame == true) Ui.Toggle();
        Ui.Tick();
    }

    /// <summary>-coop-local-host / -coop-local-join: host or join the local test on startup, then open the menu.</summary>
    /// <summary>Developer option: screenshot the main menu, log how its button column is laid out, quit.</summary>
    private void TickMenuShot()
    {
        if (menuShot.Length == 0 || MainMenuCoopButton.Column == null) return;
        if (menuShotAt < 0f) { menuShotAt = Time.unscaledTime + 4f; return; }
        if (menuShotUi && !Ui.Opened) Ui.Open();
        if (Time.unscaledTime < menuShotAt) return;
        // a local test host waits (up to 40 s) for its guest to join and ready up, and a guest for joining, to
        // show a lobby with players
        if (menuShotUi && Time.unscaledTime < menuShotAt + 40f &&
            (Session.Role == CoopRole.Host ? !(Session.Peers.Count > 0 && Session.Peers.All(p => p.Authenticated && p.Ready))
             : autoLocal == "guest" || autoReady || !Session.Peers.Any(p => p.Authenticated))) return;
        ScreenCapture.CaptureScreenshot(menuShot);
        var column = MainMenuCoopButton.Column;
        void Describe(Transform t, string indent)
        {
            var r = (RectTransform)t;
            Log.LogInfo($"{indent}{t.name} active={t.gameObject.activeSelf} pos={r.anchoredPosition} size={r.sizeDelta} " +
                        $"pivot={r.pivot} anchors={r.anchorMin}-{r.anchorMax} rot={t.localEulerAngles.z:0.0} worldY={t.position.y:0}");
            foreach (var c in t.GetComponents<Component>())
            {
                if (c is UnityEngine.UI.HorizontalOrVerticalLayoutGroup g)
                    Log.LogInfo($"{indent}  {g.GetType().Name} spacing={g.spacing} padding={g.padding} align={g.childAlignment} " +
                                $"controlH={g.childControlHeight} expandH={g.childForceExpandHeight} scaleH={g.childScaleHeight}");
                else if (c is UnityEngine.UI.LayoutElement e)
                    Log.LogInfo($"{indent}  LayoutElement min={e.minHeight} pref={e.preferredHeight} flex={e.flexibleHeight} ignore={e.ignoreLayout}");
                else if (c is UnityEngine.UI.ContentSizeFitter f)
                    Log.LogInfo($"{indent}  ContentSizeFitter v={f.verticalFit}");
                else if (!(c is RectTransform) && !(c is CanvasRenderer))
                    Log.LogInfo($"{indent}  {c.GetType().Name}");
            }
        }
        Describe(column.parent, "");
        Describe(column, "  ");
        foreach (Transform row in column)
        {
            Describe(row, "    ");
            var button = row.GetComponentInChildren<UnityEngine.UI.Button>(true);
            if (button != null) Describe(button.transform, "      ");
        }
        menuShot = string.Empty;
        Invoke(nameof(QuitAfterShot), 1.5f);
    }

    private void QuitAfterShot() => Application.Quit();

    private void TickAutoLocal()
    {
        if (autoReady && Session.Role == CoopRole.Guest && Session.Peers.Any(p => p.Authenticated))
        {
            autoReady = false;
            Ui.SetReady(true);
        }
        if (autoStartSlot > 0 && Session.Role == CoopRole.Host && Session.Peers.Count > 0 &&
            Session.Peers.All(p => p.Authenticated && p.Ready) && SceneManager.GetActiveScene().name == "StartScene")
        {
            Log.LogInfo($"Starting the campaign from slot {autoStartSlot} (-coop-local-autostart)");
            World.StartCampaign(autoStartSlot);
            autoStartSlot = 0;
        }
        if (autoLocal.Length == 0 || Time.realtimeSinceStartup < nextAutoLocal) return;
        if (Session.Role != CoopRole.None) { autoLocal = string.Empty; return; }
        nextAutoLocal = Time.realtimeSinceStartup + 2f;
        if (autoLocal == "host") Session.HostLocal();
        else Session.JoinLocal();
        bool connected = Session.Role != CoopRole.None;
        if (connected) Ui.Open();
        if (connected || ++autoLocalTries >= 30) autoLocal = string.Empty;
    }

    private bool avatarWarm;
    private System.Diagnostics.Stopwatch? warmClock;

    /// <summary>
    /// In the main menu, load the kobold avatar and prepare its colours a piece per frame, so a friend's kobold
    /// appears mid-game without a hitch.
    /// </summary>
    private void TickAvatarPrewarm()
    {
        if (avatarWarm || SceneManager.GetActiveScene().name != "StartScene" || Time.realtimeSinceStartup < 3f) return;
        warmClock ??= new System.Diagnostics.Stopwatch();
        warmClock.Start();
        var data = AvatarData.Get(Log);
        bool more = data != null && KoboldAvatar.PrewarmNext(data);
        warmClock.Stop();
        if (!more)
        {
            avatarWarm = true;
            Log.LogInfo($"Kobold avatar ready ({warmClock.ElapsedMilliseconds} ms of work, spread over menu frames)");
        }
    }

    private void TryPatchEggMod()
    {
        // once, after BepInEx has loaded every plugin (the egg mod is optional; a type search per frame is costly)
        if (eggPatchAttempted || harmony == null) return;
        eggPatchAttempted = true;
        // found by its DLL's name, not its plugin ID, so any build of it works
        var egg = BepInEx.Bootstrap.Chainloader.PluginInfos.Values.FirstOrDefault(p =>
            p.Instance != null && p.Instance.GetType().Assembly.GetName().Name == "RyanEatsEgg");
        if (egg == null) return;
        var type = egg.Instance.GetType();
        var update = AccessTools.Method(type, "Update");
        if (update == null) return;
        harmony.Patch(update, prefix: new HarmonyMethod(typeof(CoopPlugin), nameof(AllowEggUpdate)));
        Log.LogInfo("Ryan egg animations will pause only during co-op sessions");
    }

    private static bool AllowEggUpdate() => Instance == null || !Instance.World.Started;

    private void OnApplicationQuit() => Session.Quitting = true;

    private void OnDestroy()
    {
        SceneManager.activeSceneChanged -= World.OnSceneChanged;
        Session.Dispose();
        Ui.Dispose();
        if (!Session.Quitting) harmony?.UnpatchSelf();
        instanceLog?.Dispose();
        if (Instance == this) Instance = null!;
    }
}
