using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluidRenderingForGames;
using HarmonyLib;
using SimpleJSON;
using UnityEngine;
using UnityEngine.SceneManagement;
using WalkNWash.Flags;
using Yarn.Unity;

namespace DragNWashCoop;

/// <summary>Host-authoritative shared scene, independent local cameras, and join snapshots.</summary>
internal sealed class CoopWorld
{
    private readonly CoopPlugin owner;
    private readonly RemoteHands actors;
    internal RemoteHands Actors => actors;
    private readonly MaskSync masks;
    private readonly DragonPoseSync dragonPose;
    private readonly ToolRackSync racks;
    private readonly Dictionary<ulong, (float Window, int Count)> interactRates = new();
    private readonly Dictionary<ulong, (float Window, int Count)> contactRates = new();
    private readonly Dictionary<ulong, (float Window, int Count)> rackRates = new();
    private readonly Dictionary<ulong, (float Window, int Count)> dialogueRates = new();
    private readonly Dictionary<ulong, (float Window, int Count)> emoteRates = new();
    private float nextYip;
    private readonly Dictionary<ulong, (float Window, int Count)> paintRates = new();
    private readonly HashSet<string> unlockedTools = new();
    private float nextToolScan;
    private string toolCacheScene = string.Empty;
    private float nextPose;
    private float nextState;
    private float nextObjectState;
    private float nextJoinReady;
    private bool started;
    private bool waitingForScene;
    private string remoteSaveJson = string.Empty;
    private string desiredScene = string.Empty;
    private uint epoch = 1;
    private uint worldEventSequence;
    private string lastAppliedSaveJson = string.Empty;
    private bool saveDirty = true;               // host: a flag changed since the save last went to everyone
    private int sentSaveLevel = -1;              // host: the level that save was on
    private float nextRub;
    private bool handEventsBound;
    private readonly HashSet<ulong> awaitingScene = new();
    private readonly HashSet<Peer> pendingSnapshots = new();
    private bool sceneBarrier;
    private float barrierDeadline;
    private float previousTimeScale = 1f;
    private int lastHostLevel = -1;
    private int lastHostDragon;                  // instance ID of the host's dragon for that level
    private int dialogueLine;                    // host: lines advanced in the current dialogue
    private int seenDialogueLine;                // guest: the host's line number it last applied
    private bool guestSexAct;                    // guest: DragonBarkSexAct.doingSexAct was set from the host
    private readonly Dictionary<ulong, (Collider Collider, Vector3 Position, Vector3 Normal, float At, int Frame)> rubs = new();
    // sponge and spray contacts from the network, kept up every frame for a moment (they arrive about 20 times
    // a second, and Ryan's longer reactions need contact on most frames); keyed by who (0: the host, on guests)
    private readonly Dictionary<(ulong Source, WorldEventKind Kind), (Vector3 Position, Vector3 Normal, float At, int Frame)> washes = new();
    private float nextSponge, nextSplash;
    private readonly Dictionary<string, FluidParticleSystemSettings> fluidsByName = new();
    private string pendingDialogueStart = string.Empty;
    private float lastToolUse = -100f;
    private float lastHostDialogue = -100f;   // guest: last time the host was in a dialogue (or one started here)
    private readonly Dictionary<string, float> gateOpenUntil = new();   // host: gate -> Time.time it closes
    private string sexActTarget = string.Empty;                          // host: penetrable of a running sex act
    private int? pendingOption;
    private float pendingDialogueUntil;

    internal bool IsHost => started && owner.Session.Role == CoopRole.Host;
    internal bool IsGuest => started && owner.Session.Role == CoopRole.Guest;
    internal bool ApplyingNetworkEvent { get; set; }
    internal string RemoteSaveJson => remoteSaveJson;
    internal uint Epoch => epoch;
    internal bool Started => started;
    internal MaskSync Masks => masks;
    internal ToolRackSync Racks => racks;
    internal int CurrentLevel => ReadLevel();
    internal bool HasDragonPose => dragonPose.HasPose;
    /// <summary>This player used its tool in the last 2 s (spray can still be landing just after).</summary>
    internal bool UsedToolRecently => Time.unscaledTime - lastToolUse < 2f;

    internal CoopWorld(CoopPlugin owner)
    {
        this.owner = owner;
        actors = new RemoteHands(owner);
        masks = new MaskSync(owner, this);
        dragonPose = new DragonPoseSync(owner, this);
        racks = new ToolRackSync(this);
    }

    internal void StartCampaign(int slot)
    {
        if (owner.Session.Role != CoopRole.Host || slot < 1 || slot > 3 ||
            SceneManager.GetActiveScene().name != "StartScene") return;
        if (owner.Session.Peers.Any(p => p.Authenticated && !p.Ready)) return;
        SaveManagerV1.SetSaveSlot(slot);
        started = true;
        epoch++;
        owner.Ui.Close();
        MenuManager.TriggerEvent(new MenuEventUserIntent("Continue"));
    }

    internal bool EquipPersonalTool(string name)
    {
        if (!started || SceneManager.GetActiveScene().name != "PlayGame" ||
            !com.gatordragongames.washnwalk.tools.ToolManager.IsValid()) return false;
        foreach (var equip in UnityEngine.Object.FindObjectsByType<InteractableToolEquip>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!equip.gameObject.activeInHierarchy) continue;
            var asset = AccessTools.Field(typeof(InteractableToolEquip), "tool").GetValue(equip)
                        as com.gatordragongames.washnwalk.tools.Tool;
            if (asset == null || asset.name != name) continue;
            var required = AccessTools.Field(typeof(InteractableToolEquip), "requiredFlag").GetValue(equip) as string;
            if (!string.IsNullOrEmpty(required) && !WalkNWashSceneState.GetFlag(required)) return false;
            com.gatordragongames.washnwalk.tools.ToolManager.EquipTool(asset);
            return true;
        }
        return false;
    }

    internal void Tick()
    {
        actors.Tick();
        masks.Tick();
        if (!started) return;
        TickYip();
        if (IsGuest && Time.unscaledTime < pendingDialogueUntil)
        {
            ApplyingNetworkEvent = true;
            try
            {
                if (pendingDialogueStart.Length > 0 && TryStartDialogue(pendingDialogueStart)) pendingDialogueStart = string.Empty;
                if (pendingOption.HasValue && TrySelectOption(pendingOption.Value)) pendingOption = null;
            }
            finally { ApplyingNetworkEvent = false; }
        }
        if (sceneBarrier && Time.realtimeSinceStartup >= barrierDeadline) ReleaseSceneBarrier();
        racks.Tick();
        // the host's dialogue ended but this guest's did not (e.g. it missed a line): end it too
        if (IsGuest && InDialogue() && Time.unscaledTime - lastHostDialogue > 4f)
        {
            owner.Log.LogInfo("Host's dialogue has ended; ending this copy's");
            lastHostDialogue = Time.unscaledTime;
            DialogueRunnerOf()?.Stop().Forget();
        }
        if (IsHost && SceneManager.GetActiveScene().name == "PlayGame")
        {
            // a level change inside PlayGame spawns a new dragon; a level that ends in a sex scene or the
            // finale only bumps the level number, and the scene change that follows moves everyone
            int level = ReadLevel();
            int dragonId = WalkNWashSceneState.TryGetActiveDragon(out var hostDragon) ? hostDragon.gameObject.GetInstanceID() : 0;
            if (lastHostLevel < 0) { lastHostLevel = level; lastHostDragon = dragonId; }
            else if (level == lastHostLevel) { if (dragonId != 0) lastHostDragon = dragonId; }
            else if (dragonId != 0 && dragonId != lastHostDragon)
            {
                lastHostLevel = level;
                lastHostDragon = dragonId;
                BeginLevelChange();
            }
            SustainRubs();
            if (pendingSnapshots.Count > 0 && WalkNWashSceneState.TryGetActiveDragon(out _))
            {
                foreach (var peer in pendingSnapshots) masks.SendSnapshot(peer);
                pendingSnapshots.Clear();
            }
        }
        if (SceneManager.GetActiveScene().name == "PlayGame") SustainWashes();
        if (IsGuest && waitingForScene && Time.unscaledTime >= nextJoinReady)
        {
            nextJoinReady = Time.unscaledTime + .5f;
            if (SceneManager.GetActiveScene().name == desiredScene &&
                (desiredScene != "PlayGame" || WalkNWashSceneState.TryGetActiveDragon(out _)))
            {
                waitingForScene = false;
                owner.Session.SendToHost(PacketKind.SceneReady, epoch, w => w.Write((byte)0));
            }
        }
        if (Time.unscaledTime >= nextPose && SceneManager.GetActiveScene().name == "PlayGame")
        {
            nextPose = Time.unscaledTime + .05f;
            SendLocalPose();
        }
        if (IsHost && Time.unscaledTime >= nextState)
        {
            nextState = Time.unscaledTime + 1f;
            BroadcastWorldState();
        }
        if (IsHost && Time.unscaledTime >= nextObjectState)
        {
            nextObjectState = Time.unscaledTime + 2f;
            SendObjectStates();
        }
    }

    internal void OnPeerReady(Peer peer)
    {
        if (owner.Session.Role == CoopRole.Host)   // in the lobby too: colours are picked as friends join
        {
            PlayerLooks.Assign((ulong)peer.SteamId, owner.Session.Peers.Where(p => p.Authenticated).Select(p => (ulong)p.SteamId).ToList());
            BroadcastColours();
        }
        if (IsHost && SceneManager.GetActiveScene().name == "PlayGame")
        { BroadcastWorldState(peer); SendObjectStates(peer); SendJoinState(peer); }
    }

    internal void OnPeerLeft(Peer peer)
    {
        actors.Remove((ulong)peer.SteamId);
        if (IsHost) racks.ReturnFor((ulong)peer.SteamId);
        if (owner.Session.Role == CoopRole.Host) { PlayerLooks.Forget((ulong)peer.SteamId); BroadcastColours(); }
        pendingSnapshots.Remove(peer);
        awaitingScene.Remove((ulong)peer.SteamId);
        if (sceneBarrier && awaitingScene.Count == 0) ReleaseSceneBarrier();
        if (owner.Session.Role == CoopRole.None) Reset();
    }

    internal void OnSceneChanged(Scene previous, Scene current)
    {
        UnbindHandEvents();
        InvalidateToolCache();
        actors.Clear();
        masks.Clear();
        dragonPose.Clear();
        racks.Clear();
        gateOpenUntil.Clear(); sexActTarget = string.Empty;
        if (IsHost && current.name == "StartScene" && previous.name != "StartScene")
        {
            owner.Session.Leave();
            return;
        }
        if (current.name == "PlayGame") BindHandEvents();
        if (started) owner.Ui.Close();   // e.g. a guest's menu still open from readying up would cover the game
        lastHostLevel = current.name == "PlayGame" ? ReadLevel() : -1;
        lastHostDragon = 0;
        pendingDialogueStart = string.Empty;
        pendingOption = null;
        rubs.Clear(); washes.Clear();
        if (!started) return;
        if (IsHost)
        {
            epoch++;
            RaiseSceneBarrier();
            string save = GetSaveSnapshot(current.name);
            owner.Log.LogInfo($"Moving everyone to {current.name}" +
                              (save.Length > 0 ? $" (level {SaveManagerV1.GetDataProgress(JSON.Parse(save))})" : " (no save to send)"));
            owner.Session.Broadcast(PacketKind.SceneChange, epoch, w =>
            {
                w.WriteShortString(current.name);
                w.WriteLongString(save);
            });
            BroadcastWorldState();
        }
        else if (IsGuest && current.name == desiredScene)
        {
            Time.timeScale = 0f;
            waitingForScene = true;
            nextJoinReady = Time.unscaledTime + .5f;
        }
    }

    internal void OnPacket(Peer peer, Envelope packet)
    {
        if (packet.Kind != PacketKind.WorldState && packet.Kind != PacketKind.SceneChange &&
            packet.Kind != PacketKind.LevelChange && packet.Kind != PacketKind.Colours &&
            packet.Kind != PacketKind.Ping && packet.SceneEpoch != epoch)
        {
            if (packet.Kind == PacketKind.DragonPose) dragonPose.Dropped($"it is from scene epoch {packet.SceneEpoch}, this copy is at {epoch}");
            return;
        }
        try
        {
            switch (packet.Kind)
            {
                case PacketKind.Pose: ReceivePose(peer, packet); break;
                case PacketKind.DragonPose: dragonPose.Receive(packet.Reader); break;
                case PacketKind.WorldState: ReceiveWorldState(packet); break;
                case PacketKind.SceneChange: ReceiveSceneChange(packet); break;
                case PacketKind.LevelChange: ReceiveLevelChange(packet); break;
                case PacketKind.ObjectState: ReceiveObjectStates(packet.Reader); break;
                case PacketKind.Emote: ReceiveEmote(peer, packet); break;
                case PacketKind.Colours: if (owner.Session.Role == CoopRole.Guest) PlayerLooks.Read(packet.Reader); break;
                case PacketKind.SceneReady:
                    if (IsHost)
                    {
                        byte stage = packet.Reader.ReadByte();
                        if (stage == 0)
                        {
                            if (SceneManager.GetActiveScene().name != "PlayGame")
                            {
                                awaitingScene.Remove((ulong)peer.SteamId);
                                if (sceneBarrier && awaitingScene.Count == 0) ReleaseSceneBarrier();
                                else if (!sceneBarrier) SendResume(peer);
                                break;
                            }
                            BroadcastWorldState(peer);
                            SendObjectStates(peer);
                            SendJoinState(peer);
                            if (WalkNWashSceneState.TryGetActiveDragon(out _)) masks.SendSnapshot(peer);
                            else pendingSnapshots.Add(peer);
                        }
                        if (stage == 1)
                        {
                            awaitingScene.Remove((ulong)peer.SteamId);
                            if (sceneBarrier && awaitingScene.Count == 0) ReleaseSceneBarrier();
                            else if (!sceneBarrier) SendResume(peer);
                        }
                    }
                    break;
                case PacketKind.ActionRequest: ReceiveAction(peer, packet); break;
                case PacketKind.WorldEvent: ReceiveWorldEvent(packet); break;
                case PacketKind.SnapshotChunk: masks.ReceiveChunk(packet.Reader); break;
                case PacketKind.SnapshotEnd: masks.FinishSnapshot(packet.Reader); break;
                case PacketKind.Ping:
                    byte pingKind = packet.Reader.ReadByte();
                    if (IsHost && pingKind == 1) masks.SendSnapshot(peer);
                    if (owner.Session.Role == CoopRole.Host && pingKind == 2)
                        peer.Ready = packet.Reader.ReadBoolean();
                    break;
            }
        }
        catch (Exception e) { owner.Log.LogWarning($"Ignored co-op world packet: {e}"); }
    }

    internal void Reset()
    {
        PlayerLooks.Clear();
        UnbindHandEvents();
        InvalidateToolCache();
        // the game itself never stops time, so a 0 here was left by a barrier
        if (sceneBarrier || IsGuest || Time.timeScale == 0f) Time.timeScale = 1f;
        if (guestSexAct) { DragonBarkSexAct.doingSexAct = false; guestSexAct = false; }
        rubs.Clear(); washes.Clear();
        sceneBarrier = false;
        awaitingScene.Clear();
        pendingSnapshots.Clear();
        lastHostLevel = -1;
        pendingDialogueStart = string.Empty;
        pendingOption = null;
        started = false;
        waitingForScene = false;
        remoteSaveJson = string.Empty;
        desiredScene = string.Empty;
        lastAppliedSaveJson = string.Empty;
        saveDirty = true;
        sentSaveLevel = -1;
        actors.Clear();
        masks.Clear();
        dragonPose.Clear();
        racks.Clear();
        gateOpenUntil.Clear(); sexActTarget = string.Empty;
    }

    /// <param name="scene">
    /// The scene being entered: while the game changes scenes, the active scene can still read as the old one,
    /// which made the snapshot for guests following the host into a level come out empty (they then loaded
    /// their own save, another level).
    /// </param>
    private string GetSaveSnapshot(string? scene = null)
    {
        if ((scene ?? SceneManager.GetActiveScene().name) != "PlayGame") return string.Empty;
        try
        {
            var instance = AccessTools.Field(typeof(WalkNWashSceneState), "instance").GetValue(null);
            int level = (int)AccessTools.Field(typeof(WalkNWashSceneState), "currentLevel").GetValue(instance);
            // not Flags.ToJson: it hands back the JSON as SimpleJSON's string conversion, which for an array is its
            // (empty) value, so every snapshot was empty and guests loaded their own saves
            return Flags.Registry.ToJson(level).ToString();
        }
        catch (Exception e) { owner.Log.LogWarning($"Could not make save snapshot: {e.Message}"); return string.Empty; }
    }

    private static int ReadLevel()
    {
        try
        {
            var state = AccessTools.Field(typeof(WalkNWashSceneState), "instance").GetValue(null);
            return state == null ? -1 : (int)AccessTools.Field(typeof(WalkNWashSceneState), "currentLevel").GetValue(state);
        }
        catch { return -1; }
    }

    private static WalkNWashCleanEvaluate.CleanEvaluation ReadCleanEvaluation()
    {
        try
        {
            var state = AccessTools.Field(typeof(WalkNWashSceneState), "instance").GetValue(null);
            var evaluator = state != null ? AccessTools.Field(typeof(WalkNWashSceneState), "cleanEvaluate").GetValue(state) : null;
            return evaluator != null ? (WalkNWashCleanEvaluate.CleanEvaluation)
                AccessTools.Field(typeof(WalkNWashCleanEvaluate), "currentEvaluation").GetValue(evaluator) : default;
        }
        catch { return default; }
    }

    private void BeginLevelChange()
    {
        epoch++;
        masks.Clear();
        dragonPose.Clear();
        racks.ClearCache();                      // held tools stay held across levels
        gateOpenUntil.Clear(); sexActTarget = string.Empty;
        RaiseSceneBarrier();
        string save = GetSaveSnapshot();
        owner.Session.Broadcast(PacketKind.LevelChange, epoch, w => w.WriteLongString(save));
        BroadcastWorldState();
    }

    private void ReceiveLevelChange(Envelope packet)
    {
        if (owner.Session.Role != CoopRole.Guest) return;
        started = true;
        epoch = packet.SceneEpoch;
        var levelSave = packet.Reader.ReadLongString();
        if (levelSave.Length > 0) remoteSaveJson = levelSave;
        desiredScene = "PlayGame";
        masks.Clear(); dragonPose.Clear(); racks.ClearCache();
        pendingDialogueStart = string.Empty;
        pendingOption = null;
        waitingForScene = true;
        nextJoinReady = Time.unscaledTime + .5f;
        Time.timeScale = 0f;
        if (SceneManager.GetActiveScene().name != "PlayGame")
        {
            SceneManager.LoadScene("PlayGame");
            return;
        }
        var instance = AccessTools.Field(typeof(WalkNWashSceneState), "instance").GetValue(null);
        if (instance == null || string.IsNullOrEmpty(remoteSaveJson)) return;
        ApplyingNetworkEvent = true;
        try
        {
            Flags.FromJson(remoteSaveJson);
            AccessTools.Method(typeof(WalkNWashSceneState), "StartCurrentLevel").Invoke(instance, null);
            lastAppliedSaveJson = remoteSaveJson;
        }
        finally { ApplyingNetworkEvent = false; }
    }

    /// <summary>Host: a flag was set or cleared, so the save goes out with the next world state.</summary>
    internal void NoteFlagsChanged() => saveDirty = true;

    private void BroadcastWorldState(Peer? one = null)
    {
        if (!IsHost) return;
        // the save only when it changed (or to one player just arriving); guests keep the last one they got
        int level = ReadLevel();
        string save = one != null || saveDirty || level != sentSaveLevel ? GetSaveSnapshot() : string.Empty;
        if (one == null && save.Length > 0) { saveDirty = false; sentSaveLevel = level; }
        Action<BinaryWriter> write = w =>
        {
            w.WriteShortString(SceneManager.GetActiveScene().name);
            w.WriteLongString(save);
            bool inGame = SceneManager.GetActiveScene().name == "PlayGame" &&
                          AccessTools.Field(typeof(WalkNWashSceneState), "instance").GetValue(null) != null;
            w.Write((byte)(inGame ? WalkNWashSceneState.GetDragonState() : 0));
            w.Write((byte)(inGame ? WalkNWashSceneState.GetWeatherState() : 0));
            if (inGame && WalkNWashSceneState.TryGetActiveDragon(out var dragon))
            {
                w.Write(true);
                w.WriteShortString(dragon.gameObject.name);
                w.Write(dragon.gameObject.transform.position);
                w.Write(dragon.gameObject.transform.rotation);
                var animator = dragon.animator;
                var state = animator != null ? animator.GetCurrentAnimatorStateInfo(0) : default;
                w.Write(state.fullPathHash);
                w.Write(state.normalizedTime);
            }
            else w.Write(false);
            var evaluation = inGame ? ReadCleanEvaluation() : default;
            w.Write(evaluation.cleanPercentage);
            w.Write(evaluation.soapPercentage);
            w.Write(InDialogue());
        };
        if (one != null) owner.Session.Send(one, PacketKind.WorldState, epoch, write);
        else owner.Session.Broadcast(PacketKind.WorldState, epoch, write);
    }

    private void SendObjectStates(Peer? one = null)
    {
        if (!IsHost || SceneManager.GetActiveScene().name != "PlayGame") return;
        var objects = UnityEngine.Object.FindObjectsByType<Interactable>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        for (int offset = 0; offset < objects.Length; offset += 24)
        {
            int from = offset, count = Math.Min(24, objects.Length - offset);
            Action<BinaryWriter> write = w =>
            {
                w.Write((byte)count);
                for (int i = from; i < from + count; i++)
                {
                    var obj = objects[i];
                    w.WriteShortString(EntityIds.For(obj));
                    w.Write(obj.gameObject.activeSelf);
                    w.Write(obj.transform.position); w.Write(obj.transform.rotation);
                    var animator = obj.GetComponent<Animator>();
                    var state = animator != null && animator.runtimeAnimatorController != null ?
                        animator.GetCurrentAnimatorStateInfo(0) : default;
                    w.Write(state.fullPathHash); w.Write(state.normalizedTime);
                    var collider = obj.GetComponent<Collider>();
                    w.Write(collider != null); if (collider != null) w.Write(collider.enabled);
                    var light = obj.GetComponent<Light>();
                    w.Write(light != null); if (light != null) w.Write(light.enabled);
                    w.Write(obj is InteractableToolEquip equip && equip.placedModelActive);
                }
            };
            if (one != null) owner.Session.Send(one, PacketKind.ObjectState, epoch, write);
            else owner.Session.Broadcast(PacketKind.ObjectState, epoch, write);
        }
    }

    private void ReceiveObjectStates(BinaryReader reader)
    {
        if (!IsGuest || SceneManager.GetActiveScene().name != "PlayGame") return;
        int count = reader.ReadByte();
        if (count > 24) throw new InvalidDataException("Too many object states");
        ApplyingNetworkEvent = true;
        try
        {
            for (int i = 0; i < count; i++)
            {
                var id = reader.ReadShortString();
                bool active = reader.ReadBoolean();
                var pos = reader.ReadVector3(); var rot = reader.ReadQuaternion();
                int stateHash = reader.ReadInt32(); float stateTime = reader.ReadSingle();
                bool hasCollider = reader.ReadBoolean();
                bool colliderEnabled = hasCollider && reader.ReadBoolean();
                bool hasLight = reader.ReadBoolean();
                bool lightEnabled = hasLight && reader.ReadBoolean();
                bool toolPlaced = reader.ReadBoolean();
                var obj = EntityIds.Find(id);
                if (obj == null) continue;
                // things on the dragon follow its synced pose; a 2 s-old world pose would pull them off it
                if (!id.StartsWith("@dragon/", StringComparison.Ordinal)) obj.transform.SetPositionAndRotation(pos, rot);
                if (obj.activeSelf != active) obj.SetActive(active);
                var animator = obj.GetComponent<Animator>();
                if (animator != null && stateHash != 0 && animator.GetCurrentAnimatorStateInfo(0).fullPathHash != stateHash)
                    animator.Play(stateHash, 0, stateTime % 1f);
                if (hasCollider && obj.GetComponent<Collider>() is { } collider) collider.enabled = colliderEnabled;
                if (hasLight && obj.GetComponent<Light>() is { } light) light.enabled = lightEnabled;
                if (obj.GetComponent<InteractableToolEquip>() is { } equip && equip.GetPlacedModel() is { } placed)
                    placed.gameObject.SetActive(toolPlaced);
            }
        }
        finally { ApplyingNetworkEvent = false; }
    }

    private void ReceiveWorldState(Envelope packet)
    {
        if (owner.Session.Role != CoopRole.Guest) return;
        var reader = packet.Reader;
        var scene = reader.ReadShortString();
        var save = reader.ReadLongString();
        var state = (WalkNWashSceneState.DragonState)reader.ReadByte();
        var weather = (WalkNWashSceneState.WeatherState)reader.ReadByte();
        bool hasDragon = reader.ReadBoolean();
        string dragonName = string.Empty;
        Vector3 pos = default;
        Quaternion rot = default;
        int animationHash = 0;
        float animationTime = 0;
        if (hasDragon)
        {
            dragonName = reader.ReadShortString();
            pos = reader.ReadVector3();
            rot = reader.ReadQuaternion();
            animationHash = reader.ReadInt32();
            animationTime = reader.ReadSingle();
        }
        float clean = reader.ReadSingle();
        float soap = reader.ReadSingle();
        if (reader.ReadBoolean()) lastHostDialogue = Time.unscaledTime;
        epoch = packet.SceneEpoch;
        if (!string.IsNullOrEmpty(save)) remoteSaveJson = save;
        if (!started) started = true;
        desiredScene = scene;
        if (scene != SceneManager.GetActiveScene().name)
        {
            if (!waitingForScene)
            {
                waitingForScene = true;
                SceneManager.LoadScene(scene);
            }
            return;
        }
        if (scene == "PlayGame" && !waitingForScene)
        {
            // the last save the host sent, which may have come while this copy was still loading
            ApplyRemoteState(remoteSaveJson, state, weather, hasDragon, dragonName, pos, rot,
                             animationHash, animationTime, clean, soap);
        }
    }

    private void ReceiveSceneChange(Envelope packet)
    {
        if (owner.Session.Role != CoopRole.Guest) return;
        started = true;
        epoch = packet.SceneEpoch;
        desiredScene = packet.Reader.ReadShortString();
        // outside PlayGame the host sends no save; keep the last one so this copy never loads its own
        var sceneSave = packet.Reader.ReadLongString();
        if (sceneSave.Length > 0) remoteSaveJson = sceneSave;
        pendingDialogueStart = string.Empty;
        pendingOption = null;
        waitingForScene = true;
        Time.timeScale = 0f;
        actors.Clear();
        masks.Clear();
        if (SceneManager.GetActiveScene().name != desiredScene) SceneManager.LoadScene(desiredScene);
        else nextJoinReady = Time.unscaledTime + .5f;
    }

    private void ApplyRemoteState(string json, WalkNWashSceneState.DragonState state,
                                  WalkNWashSceneState.WeatherState weather, bool hasDragon,
                                  string dragonName, Vector3 position, Quaternion rotation,
                                  int animationHash, float animationTime, float clean, float soap)
    {
        ApplyingNetworkEvent = true;
        try
        {
            if (!string.IsNullOrEmpty(json) && json != lastAppliedSaveJson)
            {
                var stateInstance = AccessTools.Field(typeof(WalkNWashSceneState), "instance").GetValue(null);
                var node = JSON.Parse(json);
                int before = stateInstance != null ? (int)AccessTools.Field(typeof(WalkNWashSceneState), "currentLevel").GetValue(stateInstance) : -1;
                if (node != null && stateInstance != null && SaveManagerV1.GetDataProgress(node) == before)
                    ApplyChangedFlags(node);   // reloading every flag re-fires them all (and e.g. takes the medkit away)
                else if (node != null)
                {
                    Flags.FromJson(json);
                    int after = stateInstance != null ? (int)AccessTools.Field(typeof(WalkNWashSceneState), "currentLevel").GetValue(stateInstance) : -1;
                    owner.Log.LogInfo($"The host's save is on level {SaveManagerV1.GetDataProgress(node)}; this copy was on {before}, now {after}" +
                                      (before != after ? ", restarting the level" : ""));
                    if (before != after && stateInstance != null)
                    {
                        dragonPose.Clear();
                        masks.Clear();
                        racks.ClearCache();
                        AccessTools.Method(typeof(WalkNWashSceneState), "StartCurrentLevel").Invoke(stateInstance, null);
                    }
                }
                lastAppliedSaveJson = json;
            }
            WalkNWashSceneState.SetWeatherState(weather);
            var instance = AccessTools.Field(typeof(WalkNWashSceneState), "instance").GetValue(null);
            if (instance != null) SetGuestDragonState(state, instance);
            if (hasDragon && WalkNWashSceneState.TryGetActiveDragon(out var dragon))
            {
                if (!dragonPose.HasPose) dragon.gameObject.transform.SetPositionAndRotation(position, rotation);
                if (!dragonPose.HasPose && dragon.animator != null && animationHash != 0 &&
                    dragon.animator.GetCurrentAnimatorStateInfo(0).fullPathHash != animationHash)
                    dragon.animator.Play(animationHash, 0, animationTime % 1f);
            }
            if (float.IsFinite(clean) && float.IsFinite(soap) && clean >= 0f && clean <= 1f &&
                soap >= 0f && soap <= 1f)
                WalkNWashSceneState.CleanEvaluationUpdated(new WalkNWashCleanEvaluate.CleanEvaluation
                { cleanPercentage = clean, soapPercentage = soap });
        }
        finally { ApplyingNetworkEvent = false; }
    }

    private void SendLocalPose()
    {
        var camera = Camera.main;
        if (camera == null) return;
        var p = camera.transform.position;
        var r = camera.transform.rotation;
        string tool = "";
        Vector3 handPos = p + r * new Vector3(-.2f, -.2f, .6f);
        Quaternion handRot = r;
        Vector3 toolPos = p + r * new Vector3(.2f, -.2f, .6f);
        Quaternion toolRot = r;
        bool usingTool = false;
        var toolParts = ToolPose.Empty;
        try
        {
            if (localPlapper == null) localPlapper = UnityEngine.Object.FindFirstObjectByType<PlapperHand>();
            var hand = localPlapper != null ? PlapperHandField.GetValue(localPlapper) as Transform : null;
            if (hand != null) { handPos = hand.position; handRot = hand.rotation; }
            var current = com.gatordragongames.washnwalk.tools.ToolManager.GetCurrentTool();
            tool = current?.name ?? "";
            var model = current?.GetModel();
            if (model != null) { toolPos = model.transform.position; toolRot = model.transform.rotation; }
            toolParts = ToolPose.Capture(model);
            var manager = ToolManagerInstance.GetValue(null);
            if (manager != null) usingTool = (bool)ToolManagerUseDown.GetValue(manager);
        }
        catch { }
        if (usingTool) lastToolUse = Time.unscaledTime;
        var feet = FeetPose.Capture();
        if (owner.LocalTest) DevAvatarTools.FakeWalk(ref p, ref r, ref handPos, ref handRot, ref toolPos, ref toolRot, ref feet);
        double sentAt = Time.realtimeSinceStartupAsDouble;
        Action<BinaryWriter> write = w =>
        {
            w.Write(owner.Session.SelfId);
            w.Write(sentAt);
            w.Write(p); w.Write(r);
            w.WriteShortString(tool);
            w.Write(handPos); w.Write(handRot); w.Write(toolPos); w.Write(toolRot); w.Write(usingTool);
            feet.Write(w);
            toolParts.Write(w);
        };
        if (IsHost) owner.Session.Broadcast(PacketKind.Pose, epoch, write, false);
        if (IsGuest) owner.Session.SendToHost(PacketKind.Pose, epoch, write, false);
    }

    /// <summary>
    /// Y: yip! Your friends hear it from your kobold, in your colour's voice (and you hear yourself, as when you
    /// answer "Yip!" in the story).
    /// </summary>
    private void TickYip()
    {
        if (UnityEngine.InputSystem.Keyboard.current?.yKey.wasPressedThisFrame == true) Yip();
    }

    internal void Yip()
    {
        if (!started || Time.unscaledTime < nextYip || owner.Ui.Opened || SceneManager.GetActiveScene().name != "PlayGame" ||
            GameStateManager.Instance == null || GameStateManager.Instance.IsPaused) return;
        var camera = Camera.main;
        if (camera == null) return;
        nextYip = Time.unscaledTime + .35f;
        ulong self = owner.Session.SelfId;
        PlayerLooks.PlayYip(camera.transform.position + camera.transform.forward * .25f, PlayerLooks.SlotOf(self, (ulong)owner.Session.HostId));
        Action<BinaryWriter> write = w => { w.Write(self); w.Write((byte)0); };
        if (IsHost) owner.Session.Broadcast(PacketKind.Emote, epoch, write);
        if (IsGuest) owner.Session.SendToHost(PacketKind.Emote, epoch, write);
    }

    private void ReceiveEmote(Peer peer, Envelope packet)
    {
        ulong id = packet.Reader.ReadUInt64();
        byte emote = packet.Reader.ReadByte();
        if (emote != 0) return;                                   // 0: yip (the only one so far)
        if (IsHost && (id != (ulong)peer.SteamId || !WithinRate(emoteRates, id, 4))) return;
        if (!started || SceneManager.GetActiveScene().name != "PlayGame") return;
        actors.Yip(id);
        if (IsHost) owner.Session.Broadcast(PacketKind.Emote, epoch, w => { w.Write(id); w.Write(emote); });
    }

    /// <summary>Host: tell everyone which colour each guest is.</summary>
    private void BroadcastColours() => owner.Session.Broadcast(PacketKind.Colours, epoch, PlayerLooks.Write);

    private void ReceivePose(Peer peer, Envelope packet)
    {
        ulong id = packet.Reader.ReadUInt64();
        double sentAt = packet.Reader.ReadDouble();
        var pos = packet.Reader.ReadVector3();
        var rot = packet.Reader.ReadQuaternion();
        var tool = packet.Reader.ReadShortString();
        var handPos = packet.Reader.ReadVector3();
        var handRot = packet.Reader.ReadQuaternion();
        var toolPos = packet.Reader.ReadVector3();
        var toolRot = packet.Reader.ReadQuaternion();
        bool usingTool = packet.Reader.ReadBoolean();
        var feet = FeetPose.Read(packet.Reader);
        var toolParts = ToolPose.Read(packet.Reader);
        if (owner.Session.Role == CoopRole.Host && id != (ulong)peer.SteamId) return;
        if (!started || SceneManager.GetActiveScene().name != "PlayGame") return;
        actors.UpdatePose(id, sentAt, pos, rot, tool, handPos, handRot, toolPos, toolRot, usingTool, feet, toolParts);
        if (IsHost)
            owner.Session.Broadcast(PacketKind.Pose, epoch, w =>
            {
                w.Write(id); w.Write(sentAt); w.Write(pos); w.Write(rot); w.WriteShortString(tool);
                w.Write(handPos); w.Write(handRot); w.Write(toolPos); w.Write(toolRot); w.Write(usingTool);
                feet.Write(w);
                toolParts.Write(w);
            }, false);
    }

    internal void SendAction(WorldEventKind kind, string target, Vector3 position, Vector3 normal, string data = "")
    {
        if (!IsGuest || !masks.Ready) return;
        owner.Session.SendToHost(PacketKind.ActionRequest, epoch, w =>
        {
            w.Write((byte)kind); w.WriteShortString(target); w.Write(position); w.Write(normal); w.WriteShortString(data);
        });
    }

    private void ReceiveAction(Peer peer, Envelope packet)
    {
        if (!IsHost) return;
        var kind = (WorldEventKind)packet.Reader.ReadByte();
        if (kind == WorldEventKind.Decal)
        {
            masks.ReceiveGuestBatch(peer, packet.Reader);
            return;
        }
        var target = packet.Reader.ReadShortString();
        var pos = packet.Reader.ReadVector3();
        var normal = packet.Reader.ReadVector3();
        var data = packet.Reader.ReadShortString();
        if (kind == WorldEventKind.DialogueNext)
        {
            // a guest pressed "continue" on the line it last saw; if someone already advanced past that
            // line the request is stale, which stops two presses from skipping a line
            if (peer.Authenticated && InDialogue() && int.TryParse(target, out int line) && line == dialogueLine &&
                WithinRate(dialogueRates, (ulong)peer.SteamId, 4))
                DialogueAdvancerOf()?.RequestNextLine();
            return;
        }
        if (kind == WorldEventKind.ToolEquip || kind == WorldEventKind.RackFill || kind == WorldEventKind.RackSpray)
        {
            if (peer.Authenticated && SceneManager.GetActiveScene().name == "PlayGame")
                racks.ReceiveRequest(peer, kind, target, data);
            return;
        }
        if (!Enum.IsDefined(typeof(WorldEventKind), kind) || !ValidateAction(peer, kind, target, pos, data)) return;
        // guests replay a guest's interaction: while applying it the host holds back its results (dragon
        // state, flags), and the replay produces them on each guest. The dialogue ones (window, phone,
        // talking to Ryan) instead announce their dialogue from the host, since each copy tracks its own
        // queue of dialogues, and a replay would start whatever that copy has next.
        var interactable = kind == WorldEventKind.Interact ? EntityIds.Find(target)?.GetComponent<Interactable>() : null;
        if (!ApplyAction(kind, target, pos, normal, data)) return;
        if (kind == WorldEventKind.HandContact && data == "rub" && EntityIds.Find(target)?.GetComponent<Collider>() is { } rubbed)
            rubs[(ulong)peer.SteamId] = (rubbed, pos, normal, Time.unscaledTime, Time.frameCount);
        if (kind is WorldEventKind.SpongeContact or WorldEventKind.SplashContact)
            washes[((ulong)peer.SteamId, kind)] = (pos, normal, Time.unscaledTime, Time.frameCount);
        if (interactable == null || MirrorsInteract(interactable)) BroadcastEvent(kind, target, pos, normal, data);
    }

    /// <summary>
    /// Whether guests replay the host's own interaction. Not for ones whose results the host already sends
    /// (dialogue, dragon state, tool racks), or that act on the replaying player's own tool.
    /// </summary>
    internal static bool MirrorsInteract(Interactable interactable)
        => interactable is not (InteractablePhone or InteractableDragonTalk or InteractableDragonWindow or
                                InteractableToolEquip or InteractableWetSponge or InteractableWaterSource);

    /// <summary>Host: the tool this guest last reported holding ("" if unknown).</summary>
    internal string ToolOf(Peer peer) => actors.TryGetTool((ulong)peer.SteamId, out var name) ? name : string.Empty;

    /// <summary>Host: the guest is within <paramref name="range"/> of <paramref name="at"/>.</summary>
    internal bool IsPeerNear(Peer peer, Vector3 at, float range)
        => IsHost && peer.Authenticated && actors.TryGetPosition((ulong)peer.SteamId, out var source) &&
           Vector3.Distance(source, at) <= range;

    /// <summary>Host: the guest is near <paramref name="at"/> and not flooding requests.</summary>
    internal bool CheckPeer(Peer peer, Vector3 at, float range, int perSecond)
        => IsHost && peer.Authenticated && actors.TryGetPosition((ulong)peer.SteamId, out var source) &&
           Vector3.Distance(source, at) <= range && WithinRate(rackRates, (ulong)peer.SteamId, perSecond);

    private bool ValidateAction(Peer peer, WorldEventKind kind, string target, Vector3 pos, string data)
    {
        if (kind is not (WorldEventKind.Interact or WorldEventKind.HandContact or WorldEventKind.SprayContact or
                         WorldEventKind.SpongeContact or WorldEventKind.SplashContact)) return false;
        if (!peer.Authenticated || SceneManager.GetActiveScene().name != "PlayGame") return false;
        if (!actors.TryGetPosition((ulong)peer.SteamId, out var source)) return false;
        // hands and sponges are held at arm's length; spray reaches about 15 m
        float range = kind is WorldEventKind.SplashContact or WorldEventKind.SprayContact ? 20f : 3.5f;
        if (Vector3.Distance(source, pos) > range) return false;
        if (kind != WorldEventKind.HandContact &&
            (!actors.TryGetTool((ulong)peer.SteamId, out var tool) || !IsToolUnlocked(tool))) return false;
        if (kind == WorldEventKind.Interact && (!IsToolUnlocked(data) || EntityIds.Find(target)?.GetComponent<Interactable>() == null))
            return false;
        if (kind == WorldEventKind.Interact ? !WithinRate(interactRates, (ulong)peer.SteamId, 10)
                                            : !WithinRate(contactRates, (ulong)peer.SteamId, 500)) return false;
        return true;
    }

    internal bool ValidateRemotePaint(Peer peer, Vector3 centre)
    {
        return IsHost && peer.Authenticated &&
               actors.TryGetPosition((ulong)peer.SteamId, out var source) &&
               actors.TryGetTool((ulong)peer.SteamId, out var tool) && IsToolUnlocked(tool) &&
               Vector3.Distance(source, centre) < 25f && WithinRate(paintRates, (ulong)peer.SteamId, 4000);
    }

    private static bool WithinRate(Dictionary<ulong, (float Window, int Count)> windows, ulong id, int maxPerSecond)
    {
        float now = Time.realtimeSinceStartup;
        if (!windows.TryGetValue(id, out var rate) || now - rate.Window >= 1f) rate = (now, 0);
        if (++rate.Count > maxPerSecond) { windows[id] = rate; return false; }
        windows[id] = rate;
        return true;
    }

    internal void InvalidateToolCache()
    {
        nextToolScan = 0f;
        unlockedTools.Clear();
    }

    private bool IsToolUnlocked(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string scene = SceneManager.GetActiveScene().name;
        if (Time.realtimeSinceStartup >= nextToolScan || scene != toolCacheScene)
        {
            toolCacheScene = scene;
            nextToolScan = Time.realtimeSinceStartup + .25f;
            unlockedTools.Clear();
            if (com.gatordragongames.washnwalk.tools.ToolManager.IsValid() &&
                com.gatordragongames.washnwalk.tools.ToolManager.GetEmptyTool() is { } empty)
                unlockedTools.Add(empty.name);
            foreach (var equip in UnityEngine.Object.FindObjectsByType<InteractableToolEquip>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var asset = AccessTools.Field(typeof(InteractableToolEquip), "tool").GetValue(equip)
                            as com.gatordragongames.washnwalk.tools.Tool;
                if (asset == null || !equip.gameObject.activeInHierarchy) continue;
                var required = AccessTools.Field(typeof(InteractableToolEquip), "requiredFlag").GetValue(equip) as string;
                if (string.IsNullOrEmpty(required) || WalkNWashSceneState.GetFlag(required)) unlockedTools.Add(asset.name);
            }
        }
        return unlockedTools.Contains(name);
    }

    private bool ApplyAction(WorldEventKind kind, string target, Vector3 pos, Vector3 normal, string data)
    {
        ApplyingNetworkEvent = true;
        try
        {
            switch (kind)
            {
                case WorldEventKind.Interact:
                {
                    var interact = EntityIds.Find(target)?.GetComponent<Interactable>();
                    if (interact == null) return false;
                    if (interact is InteractableToolEquip or InteractableWetSponge or InteractableWaterSource) return false;
                    var tool = Resources.FindObjectsOfTypeAll<com.gatordragongames.washnwalk.tools.Tool>()
                        .FirstOrDefault(t => t.name == data);
                    if (tool == null) return false;
                    var eligible = AccessTools.Method(interact.GetType(), "CanInteract")?.Invoke(interact, new object[] { tool });
                    if (eligible is bool allowed && !allowed) return false;
                    bool announce = IsHost && interact is InteractablePhone or InteractableDragonTalk or InteractableDragonWindow;
                    if (announce) ApplyingNetworkEvent = false;   // let its dialogue and dragon state broadcast
                    try { interact.Interact(tool); }
                    finally { if (announce) ApplyingNetworkEvent = true; }
                    return true;
                }
                case WorldEventKind.HandContact:
                {
                    var collider = EntityIds.Find(target)?.GetComponent<Collider>();
                    if (collider == null) return false;
                    if (data == "plap") PlapperHand.PlapEvent?.Invoke(collider, pos, normal);
                    else PlapperHand.RubEvent?.Invoke(collider, pos, normal);
                    return true;
                }
                case WorldEventKind.SprayContact:
                {
                    var hitbox = EntityIds.Find(target)?.GetComponent<HitboxTrigger>();
                    var parts = data.Split('|');
                    if (hitbox == null || parts.Length != 2 || !int.TryParse(parts[0], out int type) ||
                        type < 0 || type > 4) return false;
                    if (!fluidsByName.TryGetValue(parts[1], out var fluid) || fluid == null)
                        fluidsByName[parts[1]] = fluid = Resources.FindObjectsOfTypeAll<FluidParticleSystemSettings>()
                            .FirstOrDefault(f => f.name == parts[1]);
                    if (fluid == null) return false;
                    hitbox.Hit(fluid, (HitboxTrigger.HitType)type);
                    return true;
                }
                case WorldEventKind.FlagBool:
                case WorldEventKind.FlagString:
                {
                    int split = data.IndexOf('=');
                    if (split <= 0 || !SceneStateReady()) return false;
                    string flag = data.Substring(0, split), value = data.Substring(split + 1);
                    if (kind == WorldEventKind.FlagBool) WalkNWashSceneState.SetFlag(flag, value == "1");
                    else WalkNWashSceneState.SetFlag(flag, value);
                    return true;
                }
                case WorldEventKind.Weather:
                    if (!int.TryParse(data, out int weather) || weather < 0 || weather > 2 || !SceneStateReady()) return false;
                    WalkNWashSceneState.SetWeatherState((WalkNWashSceneState.WeatherState)weather);
                    return true;
                case WorldEventKind.DragonState:
                    if (!int.TryParse(data, out int state) || state < 0 || state > 8) return false;
                    var stateInstance = AccessTools.Field(typeof(WalkNWashSceneState), "instance").GetValue(null);
                    if (stateInstance == null) return false;
                    SetGuestDragonState((WalkNWashSceneState.DragonState)state, stateInstance);
                    return true;
                case WorldEventKind.StoryAdvance:
                    MenuManager.TriggerEvent(new MenuEventUserIntent(data));
                    return true;
                case WorldEventKind.DialogueStart:
                    if (!TryStartDialogue(data))
                    { pendingDialogueStart = data; pendingDialogueUntil = Time.unscaledTime + 10f; }
                    return true;
                case WorldEventKind.DialogueNext:
                    if (int.TryParse(target, out int hostLine)) seenDialogueLine = hostLine;
                    DialogueAdvancerOf()?.RequestNextLine();
                    return true;
                case WorldEventKind.DialogueOption:
                    if (!int.TryParse(data, out int option)) return false;
                    if (!TrySelectOption(option))
                    { pendingOption = option; pendingDialogueUntil = Time.unscaledTime + 10f; }
                    return true;
                case WorldEventKind.ToolEquip:
                    return IsGuest && racks.ApplyState(target, data);
                case WorldEventKind.ToolGrant:
                    return IsGuest && racks.ApplyGrant(target, data);
                case WorldEventKind.Gate:
                {
                    var gate = EntityIds.Find(target)?.GetComponent<GateController>();
                    if (!IsGuest || gate == null ||
                        !float.TryParse(data, System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out float duration) ||
                        !(duration > 0f && duration < 600f)) return false;
                    GatePatch.ApplyingHostGate = true;
                    try { gate.Open(duration); }
                    finally { GatePatch.ApplyingHostGate = false; }
                    return true;
                }
                case WorldEventKind.Performance:
                {
                    var trigger = EntityIds.Find(target)?.GetComponents<DragonPerformanceTrigger>()
                        .FirstOrDefault(t => t.GetType().Name == data);
                    if (!IsGuest || trigger == null) return false;
                    // replayed as the guest's own code, so its dialogue, flags and paint stay blocked
                    ApplyingNetworkEvent = false;
                    PerformancePatch.Replaying = true;
                    try { trigger.TriggerPerformance(); }
                    finally { PerformancePatch.Replaying = false; ApplyingNetworkEvent = true; }
                    return true;
                }
                case WorldEventKind.SexAct:
                {
                    if (!IsGuest || !WalkNWashSceneState.TryGetActiveDragon(out var dragon) || dragon.barker == null ||
                        dragon.barker.GetPenetrator() is not { } penetrator) return false;
                    bool start = data == "start";
                    penetrator.SetLinkedPenetrable(start ? EntityIds.Find(target)?.GetComponent<DPG.Penetrable>() : null);
                    DragonBarkSexAct.doingSexAct = start;
                    guestSexAct = start;
                    return true;
                }
                case WorldEventKind.Cum:
                {
                    if (!IsGuest || !WalkNWashSceneState.TryGetActiveDragon(out var dragon) || dragon.boner == null ||
                        !float.TryParse(data, System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out float volume) ||
                        !float.IsFinite(volume)) return false;
                    CumPatch.Replaying = true;
                    try { dragon.boner.Cum(Mathf.Clamp(volume, 0f, 20f)); }
                    finally { CumPatch.Replaying = false; }
                    return true;
                }
                case WorldEventKind.SpongeContact:
                case WorldEventKind.SplashContact:
                    // guests can wash during the host's dialogue (the host can't); a reaction then would cut it off
                    if (IsHost && InDialogue()) return false;
                    InvokeWash(kind, pos, normal);
                    return true;
                case WorldEventKind.Bark:
                    if (!IsGuest || target.Length == 0 || DialogInstance.GetValue(null) == null || InDialogue()) return false;
                    DialogCommands.StartBark(target);
                    return true;
                case WorldEventKind.BarkEnd:
                    if (!IsGuest || DialogInstance.GetValue(null) == null || InDialogue() ||
                        !DialogCommands.IsDialogueRunning || DialogCommands.CurrentNode != target) return false;
                    DialogCommands.EndBark();
                    return true;
                case WorldEventKind.Intermission:
                    // the game's own fade (it also holds input until it's done), as on the host
                    if (!IsGuest) return false;
                    (IntermissionEvent.GetValue(null) as Action)?.Invoke();
                    return true;
                case WorldEventKind.LevelRespawn:
                {
                    if (!IsGuest || SceneManager.GetActiveScene().name != "PlayGame") return false;
                    var player = UnityEngine.Object.FindFirstObjectByType<PlayerController>();
                    if (player == null) return false;
                    var spawn = EntityIds.Find(target)?.transform;
                    var facing = normal.sqrMagnitude > .01f ? Quaternion.LookRotation(normal) : Quaternion.identity;
                    player.Teleport(spawn != null ? spawn.position : pos, spawn != null ? spawn.rotation : facing);
                    return true;
                }
                case WorldEventKind.Pause:
                    if (data == "resume") Time.timeScale = 1f;
                    else if (GameStateManager.Instance != null) GameStateManager.Instance.IsPaused = data == "1";
                    return true;
                default: return false;
            }
        }
        finally { ApplyingNetworkEvent = false; }
    }

    /// <param name="force">Send even while applying a guest's action (for results guests cannot replay).</param>
    internal void BroadcastEvent(WorldEventKind kind, string target, Vector3 pos, Vector3 normal, string data = "",
                                 bool force = false)
    {
        if (!IsHost || (ApplyingNetworkEvent && !force)) return;
        uint seq = ++worldEventSequence;
        owner.Session.Broadcast(PacketKind.WorldEvent, epoch, w =>
        {
            w.Write((byte)kind); w.Write(seq); w.WriteShortString(target);
            w.Write(pos); w.Write(normal); w.WriteShortString(data);
        });
    }

    private static readonly System.Reflection.FieldInfo PlapperHandField = AccessTools.Field(typeof(PlapperHand), "hand");
    private static readonly System.Reflection.FieldInfo IntermissionEvent = AccessTools.Field(typeof(WalkNWashSceneState), "intermission");
    private static readonly System.Reflection.FieldInfo ToolManagerInstance =
        AccessTools.Field(typeof(com.gatordragongames.washnwalk.tools.ToolManager), "_instance");
    private static readonly System.Reflection.FieldInfo ToolManagerUseDown =
        AccessTools.Field(typeof(com.gatordragongames.washnwalk.tools.ToolManager), "_useButtonDown");
    private PlapperHand? localPlapper;
    private static readonly System.Reflection.FieldInfo DialogInstance = AccessTools.Field(typeof(DialogCommands), "_instance");
    private static readonly System.Reflection.FieldInfo DialogAdvancer = AccessTools.Field(typeof(DialogCommands), "lineAdvancer");
    private static readonly System.Reflection.FieldInfo DialogRunner = AccessTools.Field(typeof(DialogCommands), "dialogueRunner");

    /// <summary>The game's own dialogue advancer (not just the first one in the scene).</summary>
    private static LineAdvancer? DialogueAdvancerOf()
        => DialogInstance.GetValue(null) is DialogCommands d ? DialogAdvancer.GetValue(d) as LineAdvancer : null;

    private static DialogueRunner? DialogueRunnerOf()
        => DialogInstance.GetValue(null) is DialogCommands d ? DialogRunner.GetValue(d) as DialogueRunner : null;

    /// <summary>A real dialogue is showing (barks run with the line advancer switched off).</summary>
    private static bool InDialogue()
        => DialogueRunnerOf() is { IsDialogueRunning: true } && DialogueAdvancerOf() is { enabled: true };

    private static bool SceneStateReady() => AccessTools.Field(typeof(WalkNWashSceneState), "instance").GetValue(null) != null;

    internal void NoteDialogueStarted()
    {
        lastHostDialogue = Time.unscaledTime;
        seenDialogueLine = 0;
    }

    /// <summary>Host: a dialogue started, its lines count from 0.</summary>
    internal void NoteHostDialogueStarted() => dialogueLine = 0;

    /// <summary>Host: the dialogue moved to its next line; guests follow and learn its number.</summary>
    internal void BroadcastDialogueNext()
    {
        dialogueLine++;
        BroadcastEvent(WorldEventKind.DialogueNext, dialogueLine.ToString(), Vector3.zero, Vector3.zero);
    }

    /// <summary>Guest pressed "continue": the host advances the dialogue for everyone.</summary>
    internal void RequestDialogueNext()
    {
        // not gated on the paint sync like other actions: dialogue also runs in scenes without a dragon
        if (IsGuest)
            owner.Session.SendToHost(PacketKind.ActionRequest, epoch, w =>
            {
                w.Write((byte)WorldEventKind.DialogueNext); w.WriteShortString(seenDialogueLine.ToString());
                w.Write(Vector3.zero); w.Write(Vector3.zero); w.WriteShortString("");
            });
    }

    /// <summary>
    /// Host: a guest's rub arrives ~20 times a second, but the game only counts a rub on frames it
    /// happens (e.g. the handjob progress), so it is repeated each frame while the guest keeps rubbing.
    /// </summary>
    private void SustainRubs()
    {
        if (rubs.Count == 0) return;
        foreach (var id in rubs.Keys.ToArray())
        {
            var rub = rubs[id];
            if (rub.Collider == null || Time.unscaledTime - rub.At > .12f) { rubs.Remove(id); continue; }
            if (rub.Frame == Time.frameCount) continue;
            ApplyingNetworkEvent = true;
            try { PlapperHand.RubEvent?.Invoke(rub.Collider, rub.Position, rub.Normal); }
            finally { ApplyingNetworkEvent = false; }
        }
    }

    private void RaiseSceneBarrier()
    {
        awaitingScene.Clear();
        foreach (var peer in owner.Session.Peers)
            if (peer.Authenticated) awaitingScene.Add((ulong)peer.SteamId);
        if (awaitingScene.Count == 0) return;
        // a second barrier while one is up (a scene redirecting on load) must not save the stopped time
        if (!sceneBarrier) previousTimeScale = Time.timeScale > 0f ? Time.timeScale : 1f;
        sceneBarrier = true;
        barrierDeadline = Time.realtimeSinceStartup + 20f;
        Time.timeScale = 0f;
    }

    /// <summary>Guest: applies only the flags that differ from the host's save, so each fires once.</summary>
    private static void ApplyChangedFlags(JSONNode node)
    {
        var local = new Dictionary<string, FlagEntry>();
        foreach (var entry in Flags.GetAllFlags() ?? Array.Empty<FlagEntry>()) local[entry.id] = entry;
        var inHost = new HashSet<string>();
        foreach (var item in node.AsArray)
        {
            JSONNode flag = item;
            string id = flag["id"];
            if (string.IsNullOrEmpty(id)) continue;   // the version and level entries
            inHost.Add(id);
            bool has = local.TryGetValue(id, out var mine);
            if (flag["type"] == (object)"BOOL")
            {
                bool value = flag["boolValue"].AsBool;
                if (!has || mine.type != FlagEntry.Type.BOOL || mine.boolValue != value) WalkNWashSceneState.SetFlag(id, value);
            }
            else if (flag["type"] == (object)"STRING")
            {
                string value = flag["stringValue"].Value;
                if (!has || mine.type != FlagEntry.Type.STRING || mine.stringValue != value) WalkNWashSceneState.SetFlag(id, value);
            }
        }
        foreach (var id in local.Keys)
            if (!inHost.Contains(id)) Flags.Clear(id);
    }

    /// <summary>Host: what a player who just loaded in needs beyond the world state (racks, open gates, props).</summary>
    private void SendJoinState(Peer peer)
    {
        racks.SendAll(peer);
        foreach (var gate in gateOpenUntil)
        {
            float left = gate.Value - Time.time;
            if (left > .5f)
                SendEvent(peer, WorldEventKind.Gate, gate.Key, left.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
        if (sexActTarget.Length > 0) SendEvent(peer, WorldEventKind.SexAct, sexActTarget, "start");
    }

    internal void NoteGateOpened(GateController gate, float duration)
    {
        string id = EntityIds.For(gate);
        gateOpenUntil[id] = Time.time + duration;
        BroadcastEvent(WorldEventKind.Gate, id, gate.transform.position, Vector3.zero,
                       duration.ToString("R", System.Globalization.CultureInfo.InvariantCulture), force: true);
    }

    internal void NoteSexAct(DPG.Penetrable? penetrable)
    {
        sexActTarget = penetrable != null ? EntityIds.For(penetrable) : string.Empty;
        BroadcastEvent(WorldEventKind.SexAct, sexActTarget, Vector3.zero, Vector3.zero,
                       penetrable != null ? "start" : "end", force: true);
    }

    /// <summary>Host: a world event for one player only (e.g. the answer to its tool request).</summary>
    internal void SendEvent(Peer peer, WorldEventKind kind, string target, string data)
    {
        if (!IsHost) return;
        uint seq = ++worldEventSequence;
        owner.Session.Send(peer, PacketKind.WorldEvent, epoch, w =>
        {
            w.Write((byte)kind); w.Write(seq); w.WriteShortString(target);
            w.Write(Vector3.zero); w.Write(Vector3.zero); w.WriteShortString(data);
        });
    }

    private void ReceiveWorldEvent(Envelope packet)
    {
        if (!IsGuest) return;
        var kind = (WorldEventKind)packet.Reader.ReadByte();
        if (kind == WorldEventKind.Decal)
        {
            masks.ReceiveHostBatch(packet.Reader);
            return;
        }
        _ = packet.Reader.ReadUInt32();
        var target = packet.Reader.ReadShortString();
        var pos = packet.Reader.ReadVector3();
        var normal = packet.Reader.ReadVector3();
        var data = packet.Reader.ReadShortString();
        if (ApplyAction(kind, target, pos, normal, data) && kind is WorldEventKind.SpongeContact or WorldEventKind.SplashContact)
            washes[(0UL, kind)] = (pos, normal, Time.unscaledTime, Time.frameCount);
    }

    private void BindHandEvents()
    {
        if (handEventsBound) return;
        PlapperHand.RubEvent += OnHandRub;
        PlapperHand.PlapEvent += OnHandPlap;
        com.gatordragongames.washnwalk.tools.ToolModelSponge.spongeRubbed += OnSpongeRubbed;
        WalkNWashParticleSpline.splashHit += OnSplashHit;
        handEventsBound = true;
    }

    private void UnbindHandEvents()
    {
        if (!handEventsBound) return;
        PlapperHand.RubEvent -= OnHandRub;
        PlapperHand.PlapEvent -= OnHandPlap;
        com.gatordragongames.washnwalk.tools.ToolModelSponge.spongeRubbed -= OnSpongeRubbed;
        WalkNWashParticleSpline.splashHit -= OnSplashHit;
        handEventsBound = false;
    }

    private void OnHandRub(Collider collider, Vector3 position, Vector3 normal)
    {
        if (ApplyingNetworkEvent || Time.unscaledTime < nextRub) return;
        nextRub = Time.unscaledTime + .05f;
        HandEvent(collider, position, normal, "rub");
    }

    private void OnHandPlap(Collider collider, Vector3 position, Vector3 normal)
    {
        if (!ApplyingNetworkEvent) HandEvent(collider, position, normal, "plap");
    }

    /// <summary>This player's sponge touched something: Ryan reacts on every copy if it's him.</summary>
    private void OnSpongeRubbed(com.gatordragongames.washnwalk.tools.ToolModelSponge sponge, Collider collider, Vector3 position,
                                Vector3 normal)
    {
        if (ApplyingNetworkEvent || sponge == null || Time.unscaledTime < nextSponge || !OnDragon(collider)) return;
        nextSponge = Time.unscaledTime + .05f;
        WashEvent(WorldEventKind.SpongeContact, position, normal);
    }

    /// <summary>Spray hit something; on a guest only this player's own sprayer counts (the tap's runs on every copy).</summary>
    private void OnSplashHit(WalkNWashParticleSpline spline, Collider collider, Vector3 position)
    {
        if (ApplyingNetworkEvent || spline == null || Time.unscaledTime < nextSplash || !OnDragon(collider) ||
            (IsGuest && !SprayScopePatch.Active)) return;
        nextSplash = Time.unscaledTime + .05f;
        WashEvent(WorldEventKind.SplashContact, position, Vector3.up);
    }

    private void WashEvent(WorldEventKind kind, Vector3 position, Vector3 normal)
    {
        if (!started) return;
        if (IsHost) BroadcastEvent(kind, string.Empty, position, normal);
        if (IsGuest && masks.Ready) SendAction(kind, string.Empty, position, normal);
    }

    private static bool OnDragon(Collider? collider)
        => collider != null && WalkNWashSceneState.TryGetActiveDragon(out var dragon) &&
           collider.transform.IsChildOf(dragon.gameObject.transform);

    private static readonly System.Reflection.FieldInfo SpongeRubbedEvent =
        AccessTools.Field(typeof(com.gatordragongames.washnwalk.tools.ToolModelSponge), "spongeRubbed");
    private static readonly System.Reflection.FieldInfo SplashHitEvent = AccessTools.Field(typeof(WalkNWashParticleSpline), "splashHit");

    /// <summary>What the game raises when the local sponge or spray touches something; Ryan only looks at where.</summary>
    private static void InvokeWash(WorldEventKind kind, Vector3 position, Vector3 normal)
    {
        if (kind == WorldEventKind.SpongeContact)
            (SpongeRubbedEvent.GetValue(null) as com.gatordragongames.washnwalk.tools.ToolModelSponge.SpongeRubAction)?
                .Invoke(null!, null!, position, normal);
        else (SplashHitEvent.GetValue(null) as WalkNWashParticleSpline.SplashHitAction)?.Invoke(null!, null!, position);
    }

    private void SustainWashes()
    {
        if (washes.Count == 0) return;
        foreach (var key in washes.Keys.ToArray())
        {
            var wash = washes[key];
            if (Time.unscaledTime - wash.At > .12f) { washes.Remove(key); continue; }
            if (wash.Frame == Time.frameCount) continue;
            ApplyingNetworkEvent = true;
            try { InvokeWash(key.Kind, wash.Position, wash.Normal); }
            finally { ApplyingNetworkEvent = false; }
        }
    }

    private void HandEvent(Collider collider, Vector3 position, Vector3 normal, string kind)
    {
        if (collider == null || !started) return;
        string target = EntityIds.For(collider);
        if (IsHost) BroadcastEvent(WorldEventKind.HandContact, target, position, normal, kind);
        if (IsGuest && masks.Ready) SendAction(WorldEventKind.HandContact, target, position, normal, kind);
    }

    internal void SnapshotApplied()
    {
        if (IsGuest) owner.Session.SendToHost(PacketKind.SceneReady, epoch, w => w.Write((byte)1));
    }

    internal void LatePoseTick() => dragonPose.LateTick();

    internal void EarlyPoseTick() => dragonPose.EarlyTick();

    private void ReleaseSceneBarrier()
    {
        if (!sceneBarrier) return;
        sceneBarrier = false;
        awaitingScene.Clear();
        Time.timeScale = previousTimeScale > 0f ? previousTimeScale : 1f;
        owner.Session.Broadcast(PacketKind.WorldEvent, epoch, WriteResume);
    }

    private void SendResume(Peer peer) => owner.Session.Send(peer, PacketKind.WorldEvent, epoch, WriteResume);

    private static bool TryStartDialogue(string data)
    {
        if (data.Length < 2 || !WalkNWashSceneState.TryGetActiveDragon(out _)) return false;
        DialogCommands.StartDialogue(data.Substring(1), data[0] == '1');
        return true;
    }

    private static void SetGuestDragonState(WalkNWashSceneState.DragonState state, object instance)
    {
        var field = AccessTools.Field(typeof(WalkNWashSceneState), "dragonState");
        if ((WalkNWashSceneState.DragonState)field.GetValue(instance) == state) return;
        field.SetValue(instance, state);
        WalkNWashSceneState.dragonStateChanged?.Invoke(state);
    }

    private static bool TrySelectOption(int option)
    {
        foreach (var item in UnityEngine.Object.FindObjectsByType<OptionItem>(
                     FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            try
            {
                if (item.Option.DialogueOptionID != option || !item.Option.IsAvailable) continue;
                item.InvokeOptionSelected();
                return true;
            }
            catch (NullReferenceException) { }
        }
        return false;
    }

    private static void WriteResume(BinaryWriter w)
    {
        w.Write((byte)WorldEventKind.Pause); w.Write((uint)0);
        w.WriteShortString(""); w.Write(Vector3.zero); w.Write(Vector3.zero); w.WriteShortString("resume");
    }
}
