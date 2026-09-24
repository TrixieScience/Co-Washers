using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using com.gatordragongames.washnwalk.tools;

namespace DragNWashCoop;

/// <summary>
/// Tool racks (InteractableToolEquip) are shared. The host owns whether each placed tool is on its
/// rack and how full it is (sponge, bucket). Guests ask the host to pick up or put back, and only
/// the player who asked equips or unequips: the game's own Interact would change the host's hand.
/// </summary>
internal sealed class ToolRackSync
{
    private static readonly FieldInfo ToolField = AccessTools.Field(typeof(InteractableToolEquip), "tool");
    private static readonly FieldInfo EatsField = AccessTools.Field(typeof(InteractableToolEquip), "eatsPlacedTools");
    private static readonly FieldInfo TargetAnimatorField = AccessTools.Field(typeof(InteractableToolEquip), "targetAnimator");
    private static readonly FieldInfo ObtainFlagField = AccessTools.Field(typeof(InteractableToolEquip), "obtainFlag");
    private static readonly FieldInfo PlaceFlagField = AccessTools.Field(typeof(InteractableToolEquip), "placeFlag");
    private static readonly FieldInfo PlacePerformanceField = AccessTools.Field(typeof(InteractableToolEquip), "performanceTrigger");
    private static readonly FieldInfo PickupPerformanceField = AccessTools.Field(typeof(InteractableToolEquip), "performanceTriggerOnPickup");
    private static readonly FieldInfo WaterSourceRackField = AccessTools.Field(typeof(InteractableWaterSource), "toolEquip");
    private static readonly MethodInfo CanInteractMethod = AccessTools.Method(typeof(InteractableToolEquip), "CanInteract");

    private readonly CoopWorld world;
    private readonly List<(InteractableToolEquip Equip, string Id)> racks = new();
    private readonly Dictionary<string, (bool Placed, float Fill)> sent = new();
    private readonly Dictionary<string, ulong> holders = new();   // host: rack -> guest holding its tool
    private string scene = string.Empty;
    private float nextScan;
    private float nextPoll;
    private string heldRack = string.Empty;                          // guest: rack this player took its tool from
    private string hostRack = string.Empty;                          // host: rack the host took its own tool from
    private float requestUntil;                                      // guest: a rack request is on its way
    private readonly Dictionary<ulong, float> pickedAt = new();      // host: when each guest was last given a tool
    private readonly Dictionary<string, float> sprayed = new();      // guest: water sprayed into each rack's bucket, not yet sent
    private float nextSpraySend;
    private readonly Dictionary<ulong, float> sprayAt = new();       // host: when each guest's spray was last accepted

    internal ToolRackSync(CoopWorld world) => this.world = world;

    internal void Clear()
    {
        ClearCache();
        holders.Clear(); pickedAt.Clear();
        heldRack = hostRack = string.Empty;
    }

    /// <summary>A new level: the rack objects may change, but tools in hands (and where they came from) stay.</summary>
    internal void ClearCache()
    {
        racks.Clear(); sent.Clear(); sprayed.Clear();
        nextScan = 0f;
        requestUntil = 0f;
    }

    /// <summary>Host: sends every rack that changed (tools taken or returned, buckets filled or used).</summary>
    internal void Tick()
    {
        if (world.IsGuest) SendSpray();
        if (!world.IsHost || Time.unscaledTime < nextPoll || SceneManager.GetActiveScene().name != "PlayGame") return;
        nextPoll = Time.unscaledTime + .2f;
        foreach (var (equip, id) in Racks())
            if (equip != null) Publish(equip, id, false);
    }

    internal void BroadcastNow(InteractableToolEquip equip)
    {
        var id = IdOf(equip);
        if (id.Length > 0) Publish(equip, id, true);
    }

    /// <summary>Host: brings a player who just joined or loaded up to date.</summary>
    internal void SendAll(Peer peer)
    {
        if (!world.IsHost) return;
        foreach (var (equip, id) in Racks())
            if (equip != null) world.SendEvent(peer, WorldEventKind.ToolEquip, id, StateText(StateOf(equip)));
    }

    private void Publish(InteractableToolEquip equip, string id, bool force)
    {
        var state = StateOf(equip);
        if (!force && sent.TryGetValue(id, out var last) && last.Placed == state.Placed &&
            (float.IsNaN(last.Fill) ? float.IsNaN(state.Fill) : Mathf.Abs(last.Fill - state.Fill) < .005f)) return;
        sent[id] = state;
        world.BroadcastEvent(WorldEventKind.ToolEquip, id, equip.transform.position, Vector3.zero, StateText(state), force: true);
    }

    // ---------------------------------------------------------------- guest

    /// <summary>Guest used a rack: ask the host instead of running the game's Interact.</summary>
    internal void RequestFromGuest(InteractableToolEquip equip, Tool tool)
    {
        var placed = equip.GetPlacedModel();
        var own = ToolField.GetValue(equip) as Tool;
        if (placed == null || own == null || !ToolManager.IsValid()) return;
        // one request at a time: a second pickup before the first is granted would replace that tool in hand
        if (Time.unscaledTime < requestUntil) return;
        string data;
        if (tool.name == ToolManager.GetEmptyTool().name && placed.gameObject.activeSelf) data = "pickup";
        else if (tool.name == own.name && !placed.gameObject.activeSelf) data = "place|" + FillText(ReadFill(tool.GetModel()));
        else return;
        requestUntil = Time.unscaledTime + 2f;
        world.SendAction(WorldEventKind.ToolEquip, EntityIds.For(equip), equip.transform.position, Vector3.up, data);
    }

    /// <summary>Guest dunked its sponge or filled a bucket locally: tell the host the bucket's new level.</summary>
    internal void ReportLocalFill(Interactable interactable)
    {
        ToolModel? model = interactable switch
        {
            InteractableWetSponge wet => wet.bucket,
            InteractableWaterSource source => (WaterSourceRackField.GetValue(source) as InteractableToolEquip)?.GetPlacedModel(),
            _ => null
        };
        var equip = model != null ? Racks().FirstOrDefault(r => r.Equip != null && r.Equip.GetPlacedModel() == model).Equip : null;
        float fill = ReadFill(model);
        if (equip == null || float.IsNaN(fill)) return;
        world.SendAction(WorldEventKind.RackFill, EntityIds.For(equip), equip.transform.position, Vector3.up, FillText(fill));
    }

    /// <summary>
    /// Guest sprayed water into a bucket on a rack. Only the host fills buckets (its copy is the one everybody
    /// sees), so the water is added up here and sent a few times a second; the host's level comes back to
    /// everyone the usual way. Water spraying at a rate of frames would otherwise be one request per frame.
    /// </summary>
    internal void QueueSpray(ToolModelBucket bucket, float amount)
    {
        var equip = Racks().FirstOrDefault(r => r.Equip != null && r.Equip.GetPlacedModel() == bucket).Equip;
        if (equip == null) return;   // a bucket in someone's hand fills with them, not on a rack
        var id = IdOf(equip);
        sprayed[id] = (sprayed.TryGetValue(id, out var sum) ? sum : 0f) + amount;
    }

    private void SendSpray()
    {
        if (sprayed.Count == 0 || Time.unscaledTime < nextSpraySend) return;
        nextSpraySend = Time.unscaledTime + .25f;
        foreach (var (id, amount) in sprayed)
        {
            var equip = EntityIds.Find(id)?.GetComponent<InteractableToolEquip>();
            if (equip != null)
                world.SendAction(WorldEventKind.RackSpray, id, equip.transform.position, Vector3.up, FillText(Mathf.Min(amount, 1f)));
        }
        sprayed.Clear();
    }

    /// <summary>Guest: a rack changed on the host.</summary>
    internal bool ApplyState(string target, string data)
    {
        var placed = EntityIds.Find(target)?.GetComponent<InteractableToolEquip>()?.GetPlacedModel();
        var parts = data.Split('|');
        if (placed == null || parts.Length != 2) return false;
        bool active = parts[0] == "1";
        if (placed.gameObject.activeSelf != active) placed.gameObject.SetActive(active);
        WriteFill(placed, ParseFill(parts[1]));
        return true;
    }

    /// <summary>Guest: the host answered this player's rack request.</summary>
    internal bool ApplyGrant(string target, string data)
    {
        var equip = EntityIds.Find(target)?.GetComponent<InteractableToolEquip>();
        var own = equip != null ? ToolField.GetValue(equip) as Tool : null;
        if (!ToolManager.IsValid()) return false;
        requestUntil = 0f;
        var current = ToolManager.GetCurrentTool();
        switch (data)
        {
            case "pickup" when own != null && equip != null:
                ToolManager.EquipTool(own);
                ToolManager.GetCurrentTool().GetModel()?.Sync(equip.GetPlacedModel());
                heldRack = target;
                return true;
            case "place" when own != null && current != null && current.name == own.name:
            case "deny-return":                // the rack will not take it back; never leave the player stuck
                ToolManager.UnequipTool();
                heldRack = string.Empty;
                return true;
            default:
                return false;
        }
    }

    /// <summary>"Empty hands" in the co-op menu: return the tool to its rack if possible, else drop it.</summary>
    internal void ReturnHeldTool()
    {
        if (!ToolManager.IsValid()) return;
        var current = ToolManager.GetCurrentTool();
        if (current == null || current.name == ToolManager.GetEmptyTool().name) return;
        if (world.IsGuest)
        {
            var rack = heldRack.Length > 0 ? EntityIds.Find(heldRack)?.GetComponent<InteractableToolEquip>() : null;
            if (rack != null && ToolField.GetValue(rack) is Tool own && own.name == current.name)
                world.SendAction(WorldEventKind.ToolEquip, heldRack, rack.transform.position, Vector3.up,
                                 "return|" + FillText(ReadFill(current.GetModel())));
            else ToolManager.UnequipTool();
            return;
        }
        // host or single player: the game's own put-back, on the rack this player took the tool from (any
        // other matching rack could be a socket that starts a performance, e.g. Ryan's mount frame)
        var mine = hostRack.Length > 0 ? EntityIds.Find(hostRack)?.GetComponent<InteractableToolEquip>() : null;
        hostRack = string.Empty;
        if (mine != null && mine.GetPlacedModel() is { } spot && !spot.gameObject.activeSelf &&
            ToolField.GetValue(mine) is Tool t && t.name == current.name && !holders.ContainsKey(IdOf(mine)))
            mine.Interact(current);
        else ToolManager.UnequipTool();
    }

    /// <summary>Host: after its own rack use, remember the rack if the host just took that rack's tool.</summary>
    internal void NoteHostUse(InteractableToolEquip equip)
    {
        if (!ToolManager.IsValid() || ToolField.GetValue(equip) is not Tool own) return;
        var current = ToolManager.GetCurrentTool();
        if (current != null && current.name == own.name && equip.GetPlacedModel() is { } placed && !placed.gameObject.activeSelf)
            hostRack = IdOf(equip);
        else if (hostRack == IdOf(equip)) hostRack = string.Empty;
    }

    // ---------------------------------------------------------------- host

    /// <summary>Host: a guest asks to pick up, put back ("place") or return from the menu ("return").</summary>
    internal void ReceiveRequest(Peer peer, WorldEventKind kind, string target, string data)
    {
        var equip = EntityIds.Find(target)?.GetComponent<InteractableToolEquip>();
        var placed = equip != null ? equip.GetPlacedModel() : null;
        var own = equip != null ? ToolField.GetValue(equip) as Tool : null;
        if (equip == null || placed == null || own == null || !ToolManager.IsValid()) return;
        var parts = data.Split('|');
        ulong guest = (ulong)peer.SteamId;
        if (kind == WorldEventKind.RackFill)
        {
            if (world.CheckPeer(peer, equip.transform.position, 4f, 20) && parts.Length == 1)
                WriteFill(placed, ParseFill(parts[0]));
            return;
        }
        if (kind == WorldEventKind.RackSpray)
        {
            // the spray reaches about 15 m; guests send a few times a second, so more often than 8 is flooding
            float amount = parts.Length == 1 ? ParseFill(parts[0]) : float.NaN;
            bool tooSoon = sprayAt.TryGetValue(guest, out var last) && Time.unscaledTime - last < .12f;
            if (placed is ToolModelBucket bucket && placed.gameObject.activeSelf && !float.IsNaN(amount) && amount > 0f &&
                !tooSoon && world.IsPeerNear(peer, equip.transform.position, 20f))
            {
                sprayAt[guest] = Time.unscaledTime;
                bucket.AddFillAmount(Mathf.Min(amount, 1f));
            }
            return;
        }
        bool returning = parts[0] == "return" && holders.TryGetValue(target, out var holder) && holder == guest;
        if (!returning && !world.CheckPeer(peer, equip.transform.position, 4f, 10))
        {
            if (parts[0] == "return") world.SendEvent(peer, WorldEventKind.ToolGrant, target, "deny-return");
            return;
        }
        bool pickup = parts[0] == "pickup";
        // a guest holds one tool: no second pickup until the first shows up in its hand (or a second passed)
        if (pickup && ((pickedAt.TryGetValue(guest, out var givenAt) && Time.unscaledTime - givenAt < 1f) ||
                       world.ToolOf(peer) is { Length: > 0 } inHand && inHand != ToolManager.GetEmptyTool().name))
            return;
        var tool = pickup ? ToolManager.GetEmptyTool() : own;
        // a disabled rack is busy (e.g. the mount while Ryan uses it); the game's own check ignores that
        bool allowed = equip.isActiveAndEnabled &&
                       (pickup ? CanInteract(equip, tool)
                               : !placed.gameObject.activeSelf && (returning || CanInteract(equip, tool)));
        if (!allowed)
        {
            if (parts[0] == "return") world.SendEvent(peer, WorldEventKind.ToolGrant, target, "deny-return");
            return;
        }
        // the world half of InteractableToolEquip.Interact; the hand half happens on the guest
        equip.GetComponent<Animator>()?.SetTrigger("Interacted");
        string? placeFlag = PlaceFlagField.GetValue(equip) as string;
        if (pickup)
        {
            placed.gameObject.SetActive(false);
            (PickupPerformanceField.GetValue(equip) as DragonPerformanceTrigger)?.TriggerPerformance();
            if (!string.IsNullOrEmpty(placeFlag)) WalkNWashSceneState.SetFlag(placeFlag, false);
            holders[target] = guest;
            pickedAt[guest] = Time.unscaledTime;
        }
        else
        {
            if ((bool)EatsField.GetValue(equip) && !returning) (TargetAnimatorField.GetValue(equip) as Animator)?.SetTrigger("Trashed");
            else
            {
                placed.gameObject.SetActive(true);
                WriteFill(placed, parts.Length > 1 ? ParseFill(parts[1]) : float.NaN);
            }
            // a menu return only puts the tool back where it was: no obtaining, no performance
            if (!returning)
            {
                if (ObtainFlagField.GetValue(equip) is string obtain && obtain.Length > 0) WalkNWashSceneState.SetFlag(obtain, true);
                (PlacePerformanceField.GetValue(equip) as DragonPerformanceTrigger)?.TriggerPerformance();
            }
            if (!string.IsNullOrEmpty(placeFlag)) WalkNWashSceneState.SetFlag(placeFlag, true);
            // the guest's hand is empty now, whichever rack the tool came from (it may be trashed or moved)
            foreach (var id in holders.Where(h => h.Value == guest).Select(h => h.Key).ToList()) holders.Remove(id);
        }
        Publish(equip, target, true);
        world.SendEvent(peer, WorldEventKind.ToolGrant, target, pickup ? "pickup" : "place");
    }

    /// <summary>Host: a guest left while holding a rack's tool; put it back so nobody is stuck without it.</summary>
    internal void ReturnFor(ulong guest)
    {
        foreach (var id in holders.Where(h => h.Value == guest).Select(h => h.Key).ToList())
        {
            holders.Remove(id);
            var equip = EntityIds.Find(id)?.GetComponent<InteractableToolEquip>();
            var placed = equip != null ? equip.GetPlacedModel() : null;
            if (placed == null || placed.gameObject.activeSelf) continue;
            placed.gameObject.SetActive(true);
            if (PlaceFlagField.GetValue(equip) is string placeFlag && placeFlag.Length > 0) WalkNWashSceneState.SetFlag(placeFlag, true);
        }
    }

    // ---------------------------------------------------------------- helpers

    private List<(InteractableToolEquip Equip, string Id)> Racks()
    {
        string active = SceneManager.GetActiveScene().name;
        if (active != scene || Time.unscaledTime >= nextScan)
        {
            scene = active;
            nextScan = Time.unscaledTime + 5f;
            racks.Clear();
            foreach (var equip in Object.FindObjectsByType<InteractableToolEquip>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (equip.GetPlacedModel() != null) racks.Add((equip, EntityIds.For(equip)));
        }
        return racks;
    }

    private string IdOf(InteractableToolEquip equip)
    {
        foreach (var (e, id) in Racks()) if (e == equip) return id;
        return string.Empty;
    }

    private static bool CanInteract(InteractableToolEquip equip, Tool tool)
        => CanInteractMethod.Invoke(equip, new object[] { tool }) is true;

    private static (bool Placed, float Fill) StateOf(InteractableToolEquip equip)
    {
        var placed = equip.GetPlacedModel();
        return (placed.gameObject.activeSelf, ReadFill(placed));
    }

    private static string StateText((bool Placed, float Fill) state) => (state.Placed ? "1|" : "0|") + FillText(state.Fill);

    private static string FillText(float fill) => float.IsNaN(fill) ? "" : fill.ToString("R", CultureInfo.InvariantCulture);

    private static float ParseFill(string text)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fill) && float.IsFinite(fill)
            ? Mathf.Clamp01(fill) : float.NaN;

    private static readonly Dictionary<System.Type, (PropertyInfo? Property, FieldInfo? Field)> fillMembers = new();

    /// <summary>
    /// The sponge and the bucket keep a fill amount (a private field or a private-set property); other
    /// tools have none. Plain reflection, cached: AccessTools logs a warning for every miss.
    /// </summary>
    private static (PropertyInfo? Property, FieldInfo? Field) FillMember(System.Type type)
    {
        if (fillMembers.TryGetValue(type, out var member)) return member;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        PropertyInfo? property = null;
        FieldInfo? field = null;
        for (var t = type; t != null && property == null && field == null; t = t.BaseType)
        {
            property = t.GetProperty("fillAmount", flags);
            if (property?.PropertyType != typeof(float)) property = null;
            field = property == null ? t.GetField("fillAmount", flags) : null;
            if (field?.FieldType != typeof(float)) field = null;
        }
        return fillMembers[type] = (property, field);
    }

    private static float ReadFill(ToolModel? model)
    {
        if (model == null) return float.NaN;
        var (property, field) = FillMember(model.GetType());
        if (property != null) return (float)property.GetValue(model);
        if (field != null) return (float)field.GetValue(model);
        return float.NaN;
    }

    private static void WriteFill(ToolModel? model, float fill)
    {
        if (model == null || float.IsNaN(fill) || Mathf.Abs(ReadFill(model) - fill) < .0001f) return;
        var (property, field) = FillMember(model.GetType());
        if (property?.GetSetMethod(true) is { } setter) setter.Invoke(model, new object[] { fill });
        else if (field != null) field.SetValue(model, fill);
        else return;
        model.Sync(model);   // refreshes the sponge's look / the bucket's water level from its own fill
    }
}
