using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SimpleJSON;
using SkinnedMeshDecals;
using UnityEngine;
using com.gatordragongames.washnwalk.tools;
using WalkNWash.Flags;
using Yarn.Unity;

namespace DragNWashCoop;

internal static class PatchState
{
    internal static CoopWorld? World => CoopPlugin.Instance != null ? CoopPlugin.Instance.World : null;
    internal static bool GuestBlocked => World is { IsGuest: true, ApplyingNetworkEvent: false };
}

[HarmonyPatch(typeof(SaveManagerV1), nameof(SaveManagerV1.TryLoad))]
internal static class GuestSaveLoadPatch
{
    private static bool Prefix(ref JSONNode jsonData, ref bool __result)
    {
        var world = PatchState.World;
        if (world is not { IsGuest: true }) return true;
        if (string.IsNullOrEmpty(world.RemoteSaveJson))
        {
            CoopPlugin.Instance.Log.LogWarning("Loading a level before the host's save arrived; this copy's own save is used");
            return true;
        }
        jsonData = JSON.Parse(world.RemoteSaveJson);
        __result = jsonData != null;
        CoopPlugin.Instance.Log.LogInfo($"Loading the host's save (level {SaveManagerV1.GetDataProgress(jsonData)})");
        return false;
    }
}

[HarmonyPatch(typeof(SaveManagerV1), nameof(SaveManagerV1.Save))]
internal static class GuestSaveWritePatch
{
    private static bool Prefix() => PatchState.World is not { IsGuest: true };
}

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Save))]
internal static class GuestLegacySaveWritePatch
{
    private static bool Prefix() => PatchState.World is not { IsGuest: true };
}

[HarmonyPatch(typeof(FlagRegistry), nameof(FlagRegistry.Load))]
internal static class GuestFlagLoadScopePatch
{
    private static void Prefix(out bool __state)
    {
        __state = PatchState.World is { ApplyingNetworkEvent: true };
        if (PatchState.World is { IsGuest: true } world) world.ApplyingNetworkEvent = true;
    }

    // a finalizer runs even if loading throws, and restores what was there before (it may be nested)
    private static Exception? Finalizer(Exception? __exception, bool __state)
    {
        if (PatchState.World is { IsGuest: true } world) world.ApplyingNetworkEvent = __state;
        return __exception;
    }
}

[HarmonyPatch(typeof(WalkNWashSceneState), nameof(WalkNWashSceneState.CleanEvaluationUpdated))]
internal static class GuestCleanEvaluationPatch
{
    private static bool Prefix() => !PatchState.GuestBlocked;
}

[HarmonyPatch(typeof(WalkNWashSceneState), nameof(WalkNWashSceneState.SetDragonState), new[] { typeof(WalkNWashSceneState.DragonState) })]
internal static class DragonStatePatch
{
    private static bool Prefix() => !PatchState.GuestBlocked;
    // the state it actually ended in: the game may refuse a transition, and nested calls (Exited ->
    // NextLevel -> WaitingToAppear) finish the inner one first
    private static void Postfix()
    {
        if (PatchState.World is { IsHost: true } world)
            world.BroadcastEvent(WorldEventKind.DragonState, "", Vector3.zero, Vector3.zero,
                                 ((int)WalkNWashSceneState.GetDragonState()).ToString());
    }
}

[HarmonyPatch(typeof(WalkNWashSceneState), nameof(WalkNWashSceneState.SetWeatherState))]
internal static class WeatherPatch
{
    private static bool Prefix() => !PatchState.GuestBlocked;
    private static void Postfix(WalkNWashSceneState.WeatherState state)
    {
        if (PatchState.World is { IsHost: true } world)
            world.BroadcastEvent(WorldEventKind.Weather, "", Vector3.zero, Vector3.zero, ((int)state).ToString());
    }
}

[HarmonyPatch]
internal static class FlagPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(WalkNWashSceneState)))
            if (method.Name == "SetFlag" && method.GetParameters().Length == 2) yield return method;
    }

    private static bool Prefix() => !PatchState.GuestBlocked;

    private static void Postfix(object[] __args)
    {
        if (PatchState.World is not { IsHost: true } world || __args.Length != 2) return;
        world.InvalidateToolCache();
        string id = (string)__args[0];
        if (__args[1] is bool value)
            world.BroadcastEvent(WorldEventKind.FlagBool, "", Vector3.zero, Vector3.zero, id + "=" + (value ? "1" : "0"));
        else if (__args[1] is string text)
            world.BroadcastEvent(WorldEventKind.FlagString, "", Vector3.zero, Vector3.zero, id + "=" + text);
    }
}

/// <summary>Any change to the flags (the game's, Yarn's, a loaded save) marks the host's save to be sent again.</summary>
[HarmonyPatch]
internal static class FlagChangePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(FlagRegistry)))
            if (method.Name is "Set" or "ClearAll" ||
                (method.Name == "FromJson" && method.GetParameters()[0].ParameterType == typeof(JSONNode)))
                yield return method;
    }

    private static void Postfix() => PatchState.World?.NoteFlagsChanged();
}

[HarmonyPatch]
internal static class InteractPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var baseType = typeof(Interactable);
        foreach (var type in baseType.Assembly.GetTypes())
        {
            if (!baseType.IsAssignableFrom(type) || type.IsAbstract) continue;
            var method = type.GetMethod("Interact", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
                                        null, new[] { typeof(Tool) }, null);
            if (method != null) yield return method;
        }
    }

    private static bool Prefix(Interactable __instance, Tool tool)
    {
        if (!PatchState.GuestBlocked) return true;
        switch (__instance)
        {
            case InteractableToolEquip equip:
                PatchState.World!.Racks.RequestFromGuest(equip, tool);   // the host decides; only this hand changes
                return false;
            case InteractableWetSponge:
            case InteractableWaterSource:
                return true;   // fills this player's own sponge or the bucket; Postfix tells the host the bucket level
        }
        PatchState.World!.SendAction(WorldEventKind.Interact, EntityIds.For(__instance), __instance.transform.position,
                                      Vector3.up, tool?.name ?? "");
        return false;
    }

    private static void Postfix(Interactable __instance, Tool tool, MethodBase __originalMethod)
    {
        if (PatchState.World is { IsGuest: true, ApplyingNetworkEvent: false } guest)
        {
            if (__instance is InteractableWetSponge or InteractableWaterSource) guest.Racks.ReportLocalFill(__instance);
            return;
        }
        if (PatchState.World is not { IsHost: true, ApplyingNetworkEvent: false } world) return;
        if (__instance is InteractableToolEquip equip) { world.Racks.NoteHostUse(equip); world.Racks.BroadcastNow(equip); return; }
        if (!CoopWorld.MirrorsInteract(__instance)) return;
        if (__instance.GetType() == typeof(Interactable) ||
            __originalMethod.DeclaringType != typeof(Interactable))
            world.BroadcastEvent(WorldEventKind.Interact, EntityIds.For(__instance), __instance.transform.position,
                                 Vector3.up, tool?.name ?? "");
    }
}

/// <summary>The gate only opens from the host (buzzer or dialogue); guests replay the host's opening.</summary>
[HarmonyPatch(typeof(GateController), nameof(GateController.Open))]
internal static class GatePatch
{
    internal static bool ApplyingHostGate;

    private static bool Prefix() => PatchState.World is not { IsGuest: true } || ApplyingHostGate;

    private static void Postfix(GateController __instance, float duration)
    {
        if (PatchState.World is { IsHost: true } world) world.NoteGateOpened(__instance, duration);
    }
}

/// <summary>
/// Rack performances that put props on Ryan (the egg, the penetrator link). Guests replay the host's
/// trigger for the visuals, as their own code: dialogue, flags and paint still come from the host.
/// </summary>
[HarmonyPatch]
internal static class PerformancePatch
{
    internal static bool Replaying;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(DragonPerformanceTriggerEggAnimation), nameof(DragonPerformanceTrigger.TriggerPerformance));
        yield return AccessTools.Method(typeof(DragonPerformanceTriggerPenetrated), nameof(DragonPerformanceTrigger.TriggerPerformance));
    }

    private static bool Prefix() => PatchState.World is not { IsGuest: true } || Replaying;

    private static void Postfix(DragonPerformanceTrigger __instance)
    {
        if (PatchState.World is { IsHost: true } world)
            world.BroadcastEvent(WorldEventKind.Performance, EntityIds.For(__instance), __instance.transform.position,
                                 Vector3.zero, __instance.GetType().Name, force: true);
    }
}

/// <summary>
/// The mount performance waits on a dialogue that can refuse it, so guests do not replay it; they copy
/// the host's penetrator link while the act runs instead.
/// </summary>
[HarmonyPatch(typeof(DragonBarkSexAct), nameof(DragonBarkSexAct.OnStart))]
internal static class SexActStartPatch
{
    private static void Postfix(DPG.Penetrable ___penetrable)
    {
        if (PatchState.World is { IsHost: true } world) world.NoteSexAct(___penetrable);
    }
}

[HarmonyPatch(typeof(DragonBarkSexAct), nameof(DragonBarkSexAct.OnEnd))]
internal static class SexActEndPatch
{
    private static void Postfix()
    {
        if (PatchState.World is { IsHost: true } world) world.NoteSexAct(null);
    }
}

/// <summary>Ryan cums when the host's Ryan does (sex act, dialogue, stimulation), not on his own on guests.</summary>
[HarmonyPatch(typeof(WalkNWashDragonBoner), nameof(WalkNWashDragonBoner.Cum))]
internal static class CumPatch
{
    internal static bool Replaying;

    private static bool Prefix() => PatchState.World is not { IsGuest: true } || Replaying;

    private static void Postfix(float volumeMult)
    {
        if (PatchState.World is { IsHost: true } world)
            world.BroadcastEvent(WorldEventKind.Cum, "", Vector3.zero, Vector3.zero,
                                 volumeMult.ToString("R", System.Globalization.CultureInfo.InvariantCulture), force: true);
    }
}

/// <summary>Marks paint from a player's own sponge or sprayer: the only paint a guest sends to the host.</summary>
[HarmonyPatch]
internal static class PlayerPaintPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(WalkNWashParticleSystemSoap), "OnFluidCollision");
        yield return AccessTools.Method(typeof(WalkNWashParticleSystemWater), "OnFluidCollision");
        yield return AccessTools.Method(typeof(WalkNWashParticleSystemSparkleClean), "OnFluidCollision");
    }

    private static bool Prefix(out bool __state)
    {
        __state = !SprayScopePatch.Remote;   // another player's stream doesn't paint: its paint comes from them
        if (__state) MaskSync.PlayerPaint++;
        return __state;
    }

    private static Exception? Finalizer(Exception? __exception, bool __state)
    {
        if (__state) MaskSync.PlayerPaint--;
        return __exception;
    }
}

/// <summary>Cum on Ryan is painted through the water settings; that is Ryan's paint, not the player's.</summary>
[HarmonyPatch(typeof(WalkNWashParticleSystemCum), nameof(WalkNWashParticleSystemCum.OnFluidCollision))]
internal static class CumPaintPatch
{
    private static void Prefix() => MaskSync.PlayerPaint -= 1000;

    private static Exception? Finalizer(Exception? __exception)
    {
        MaskSync.PlayerPaint += 1000;
        return __exception;
    }
}

/// <summary>
/// Host: the fade to black between levels. In the game it follows Ryan's walk out, which is the host's alone
/// (guests' Ryan is moved by the host), so guests fade when told to.
/// </summary>
[HarmonyPatch(typeof(IntermissionFade), "OnIntermission")]
internal static class IntermissionPatch
{
    private static void Postfix()
    {
        if (PatchState.World is { IsHost: true } world)
            world.BroadcastEvent(WorldEventKind.Intermission, "", Vector3.zero, Vector3.zero, force: true);
    }
}

/// <summary>
/// Host: moved to the next level's start after the fade. Guests are moved there too, the same way; only the
/// level-load move counts (not the one when the scene loads, nor "unstick", which each player does alone).
/// </summary>
[HarmonyPatch(typeof(WalkNWashSceneDescription), "RespawnPlayer")]
internal static class LevelRespawnPatch
{
    private static void Postfix(Coroutine? ___levelLoadRoutine)
    {
        if (___levelLoadRoutine == null || PatchState.World is not { IsHost: true } world) return;
        // where the game just put the host: the level's named spawn, else the scene's default one
        string name = WalkNWashSceneState.GetPlayerSpawnTransformName();
        Transform? spawn = !string.IsNullOrEmpty(name) && GameObject.Find(name) is { } named ? named.transform : null;
        if (spawn == null) spawn = WalkNWashSceneDescription.GetSceneDescription().playerSpawnPoint;   // it just ran
        if (spawn == null) return;
        world.BroadcastEvent(WorldEventKind.LevelRespawn, EntityIds.For(spawn.gameObject), spawn.position, spawn.forward, name,
                             force: true);
    }
}

/// <summary>
/// Whose water stream is being updated (it paints, splashes and hits things in its Update). Active: on a
/// guest, this player's own sprayer (not, say, the tap's, which runs on every copy of the game anyway).
/// Remote: another player's, shown for looks: its paint, Ryan's reactions and bucket water come from that
/// player, so here it only splashes.
/// </summary>
[HarmonyPatch(typeof(WalkNWashParticleSpline), "Update")]
internal static class SprayScopePatch
{
    private static readonly System.Reflection.FieldInfo FireField = AccessTools.Field(typeof(WalkNWashParticleSpline), "fireTransform");
    private static readonly System.Reflection.FieldInfo SplashEvent = AccessTools.Field(typeof(WalkNWashParticleSpline), "splashHit");
    internal static bool Active;
    internal static bool Remote;
    private static Delegate? mutedSplash;

    private static void Prefix(WalkNWashParticleSpline __instance)
    {
        Active = false;
        Remote = RemoteHands.IsRemoteStream(__instance);
        if (Remote)
        {
            mutedSplash = SplashEvent.GetValue(null) as Delegate;   // Ryan's reactions to it come from its player
            SplashEvent.SetValue(null, null);
            return;
        }
        if (PatchState.World is not { IsGuest: true } || !com.gatordragongames.washnwalk.tools.ToolManager.IsValid()) return;
        var model = com.gatordragongames.washnwalk.tools.ToolManager.GetCurrentTool()?.GetModel();
        Active = model != null && FireField.GetValue(__instance) is Transform fire && fire != null && fire.IsChildOf(model.transform);
    }

    private static Exception? Finalizer(Exception? __exception)
    {
        if (Remote) SplashEvent.SetValue(null, Delegate.Combine(mutedSplash, SplashEvent.GetValue(null) as Delegate));
        mutedSplash = null;
        Active = Remote = false;
        return __exception;
    }
}

/// <summary>
/// A bucket has its own Hit (it doesn't reach HitboxTrigger.Hit): a guest's spray into it goes to the host,
/// which fills the shared bucket.
/// </summary>
[HarmonyPatch(typeof(BucketWaterHitbox), nameof(BucketWaterHitbox.Hit))]
internal static class BucketSprayPatch
{
    private static readonly System.Reflection.FieldInfo BucketField = AccessTools.Field(typeof(BucketWaterHitbox), "bucketModel");

    private static bool Prefix(BucketWaterHitbox __instance, HitboxTrigger.HitType hitType)
    {
        if (SprayScopePatch.Remote) return false;
        if (!PatchState.GuestBlocked || hitType != HitboxTrigger.HitType.Spray) return true;
        if (SprayScopePatch.Active && BucketField.GetValue(__instance) is com.gatordragongames.washnwalk.tools.ToolModelBucket bucket)
            PatchState.World!.Racks.QueueSpray(bucket, .1f);   // the amount the game adds per hit
        return false;
    }
}

[HarmonyPatch(typeof(HitboxTrigger), nameof(HitboxTrigger.Hit))]
internal static class HitboxPatch
{
    private static bool Prefix(HitboxTrigger __instance, FluidRenderingForGames.FluidParticleSystemSettings fluid,
                               HitboxTrigger.HitType type)
    {
        if (SprayScopePatch.Remote) return false;
        if (!PatchState.GuestBlocked) return true;
        PatchState.World!.SendAction(WorldEventKind.SprayContact, EntityIds.For(__instance),
            __instance.transform.position, Vector3.up, ((int)type) + "|" + (fluid?.name ?? ""));
        return false;
    }

    private static void Postfix(HitboxTrigger __instance, FluidRenderingForGames.FluidParticleSystemSettings fluid,
                                HitboxTrigger.HitType type)
    {
        if (!SprayScopePatch.Remote && PatchState.World is { IsHost: true, ApplyingNetworkEvent: false } world)
            world.BroadcastEvent(WorldEventKind.SprayContact, EntityIds.For(__instance),
                __instance.transform.position, Vector3.up, ((int)type) + "|" + (fluid?.name ?? ""));
    }
}

[HarmonyPatch]
internal static class PaintDecalPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(PaintDecal)))
        {
            var parameters = method.GetParameters();
            if (method.Name == nameof(PaintDecal.QueueDecal) && parameters.Length == 5 &&
                parameters[0].ParameterType == typeof(Renderer)) yield return method;
        }
    }

    private static bool Prefix(Renderer __0, DecalProjector __1, DecalProjection __2, DecalSettings? __3)
        => !SprayScopePatch.Remote && (PatchState.World?.Masks.AllowLocal(__0, __1, __2, __3) ?? true);

    private static void Postfix(Renderer __0, DecalProjector __1, DecalProjection __2, DecalSettings? __3)
    {
        if (!SprayScopePatch.Remote) PatchState.World?.Masks.RecordLocal(__0, __1, __2, __3);
    }
}

[HarmonyPatch(typeof(MenuManager), nameof(MenuManager.TriggerEvent))]
internal static class GuestStoryInputPatch
{
    private static bool Prefix(MenuEvent e)
    {
        if (!PatchState.GuestBlocked || e is not MenuEventUserIntent intent) return true;
        switch (intent.name)
        {
            // this player's own menus, settings and getting unstuck
            case "Pause": case "Resume": case "Options": case "Back": case "Save": case "Unstick": return true;
            case "Cancel":
                return MenuManager.GetCurrentMenuName() is "Menu_Pause" or "Menu_Options" or "Menu_Main";
            case "Quit":
                CoopPlugin.Instance.Session.Leave(returnToMenu: false);   // the game's Quit loads the menu itself
                return true;
            default: return false;
        }
    }

    private static void Postfix(MenuEvent e)
    {
        if (e is not MenuEventUserIntent intent ||
            PatchState.World is not { IsHost: true, ApplyingNetworkEvent: false } world) return;
        // menus, settings and unstick are each player's own; "Play" (leaving a sex scene) reaches guests as the
        // scene change that follows
        if (intent.name is "Options" or "Back" or "Quit" or "Continue" or "Pause" or "Resume" or "Cancel" or
            "Save" or "Unstick" or "Play" or
            "NewGame" or "LoadGame" or "RyanSexScene" or "ConradSexScene" or
            "AlexanderSexScene" or "ConradRyanSexScene" or "FinishGame" ||
            intent.name.StartsWith("LoadSlot", StringComparison.Ordinal)) return;
        world.BroadcastEvent(WorldEventKind.StoryAdvance, "", Vector3.zero, Vector3.zero, intent.name);
    }
}

/// <summary>
/// Guest: Ryan reacts to sponges and spray only as the host tells (the contacts come back from the host), so
/// each copy reacts once, to everybody's washing.
/// </summary>
[HarmonyPatch]
internal static class GuestDragonWashPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var name in new[] { "OnSpongeHit", "OnSpongeRubRetrigger", "OnSplashHit" })
            if (AccessTools.Method(typeof(DragonBarkFeaturePlapResponse), name) is { } method) yield return method;
    }

    private static bool Prefix() => !PatchState.GuestBlocked;
}

/// <summary>
/// Ryan's short lines while he's washed (barks). The host's copy picks them; guests show the same ones instead
/// of picking their own (some are random, and guests' reactions only follow the host's).
/// </summary>
[HarmonyPatch(typeof(DialogCommands), nameof(DialogCommands.StartBark))]
internal static class BarkStartPatch
{
    private static bool Prefix() => !PatchState.GuestBlocked;

    private static void Postfix(string nodeName)
    {
        if (PatchState.World is { IsHost: true } world && !string.IsNullOrEmpty(nodeName))
            world.BroadcastEvent(WorldEventKind.Bark, nodeName, Vector3.zero, Vector3.zero, force: true);
    }
}

[HarmonyPatch(typeof(DialogCommands), nameof(DialogCommands.EndBark))]
internal static class BarkEndPatch
{
    private static bool Prefix(out string __state)
    {
        __state = string.Empty;
        if (PatchState.GuestBlocked) return false;
        try { if (DialogCommands.IsDialogueRunning) __state = DialogCommands.CurrentNode ?? string.Empty; }
        catch (NullReferenceException) { }
        return true;
    }

    private static void Postfix(string __state)
    {
        if (PatchState.World is { IsHost: true } world && __state.Length > 0)
            world.BroadcastEvent(WorldEventKind.BarkEnd, __state, Vector3.zero, Vector3.zero, force: true);
    }
}

[HarmonyPatch(typeof(DragonBarkFeaturePlapResponse), "OnPlapRub")]
internal static class GuestDragonRubPatch
{
    private static bool Prefix() => !PatchState.GuestBlocked;
}

[HarmonyPatch(typeof(DragonBarkFeaturePlapResponse), "OnPlapRubRetrigger")]
internal static class GuestDragonRubRetriggerPatch
{
    private static bool Prefix() => !PatchState.GuestBlocked;
}

[HarmonyPatch(typeof(PlapResponseDispatcher), "OnPlap")]
internal static class GuestPlapPatch
{
    private static bool Prefix() => !PatchState.GuestBlocked;
}

[HarmonyPatch(typeof(DragonCharacterController), "ExecuteWalkPath")]
internal static class GuestDragonPathPatch
{
    private static bool Prefix() => PatchState.World is not { IsGuest: true };
}

[HarmonyPatch(typeof(DialogCommands), nameof(DialogCommands.StartDialogue))]
internal static class DialogueStartPatch
{
    private static bool Prefix() => !PatchState.GuestBlocked;
    private static void Postfix(string nodeName, bool disablePlayerInput)
    {
        if (PatchState.World is { IsGuest: true } guest) guest.NoteDialogueStarted();
        if (PatchState.World is { IsHost: true } host) host.NoteHostDialogueStarted();
        if (PatchState.World is { IsHost: true, ApplyingNetworkEvent: false } world)
            world.BroadcastEvent(WorldEventKind.DialogueStart, "", Vector3.zero, Vector3.zero,
                                 (disablePlayerInput ? "1" : "0") + nodeName);
    }
}

[HarmonyPatch(typeof(LineAdvancer), nameof(LineAdvancer.RequestNextLine))]
internal static class DialogueNextPatch
{
    private static bool Prefix()
    {
        if (!PatchState.GuestBlocked) return true;
        PatchState.World!.RequestDialogueNext();   // the host advances everyone's dialogue together
        return false;
    }

    private static void Postfix()
    {
        if (PatchState.World is { IsHost: true, ApplyingNetworkEvent: false } world) world.BroadcastDialogueNext();
    }
}

[HarmonyPatch(typeof(OptionItem), nameof(OptionItem.InvokeOptionSelected))]
internal static class DialogueChoicePatch
{
    private static bool Prefix() => !PatchState.GuestBlocked;
    private static void Postfix(OptionItem __instance)
    {
        if (PatchState.World is not { IsHost: true, ApplyingNetworkEvent: false } world ||
            !__instance.Option.IsAvailable) return;
        world.BroadcastEvent(WorldEventKind.DialogueOption, "", Vector3.zero, Vector3.zero,
                             __instance.Option.DialogueOptionID.ToString());
    }
}
