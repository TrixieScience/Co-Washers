using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.VFX;

namespace DragNWashCoop;

/// <summary>
/// Who looks and sounds like what. The host is always the KoboldKare-red kobold; the host gives each guest who joins
/// the first colour nobody else has and tells everyone, so every screen (kobolds, name tags, lobby rings) agrees and
/// no two friends match. Each colour also has its own voice: its yip is pitched a little differently.
/// </summary>
internal static class PlayerLooks
{
    private static readonly Dictionary<ulong, int> slots = new();
    private static readonly float[] Pitches = { 1f, 1.1f, .92f, 1.16f, .86f, 1.04f };
    private static readonly Color Scales = new(213f / 255f, 59f / 255f, 81f / 255f);   // the kobold's red, off its texture

    /// <summary>Changes whenever the colour table does (so kobolds already built can be recoloured).</summary>
    internal static int Version { get; private set; }

    internal static int SlotOf(ulong id, ulong hostId)
    {
        if (id == hostId) return 0;
        if (slots.TryGetValue(id, out int slot)) return slot;
        ulong h = id * 0x9E3779B97F4A7C15UL;             // not told yet: a stable guess
        return 1 + (int)((h >> 33) % (ulong)(KoboldAvatar.Palette.Length - 1));
    }

    /// <summary>The colour of that kobold's scales.</summary>
    internal static Color Swatch(int slot)
    {
        var p = KoboldAvatar.Palette[slot % KoboldAvatar.Palette.Length];
        return AvatarData.Shift(Scales, p.Hue, p.Brightness, p.Saturation);
    }

    /// <summary>The swatch lifted toward cream, for lettering over the game's scenery.</summary>
    internal static Color Lettering(int slot) => Color.Lerp(Swatch(slot), LobbyLook.Cream, .3f);

    internal static float Pitch(int slot) => Pitches[slot % Pitches.Length];

    /// <summary>Host: a guest has joined; keep the colours of everyone still here and give the newcomer a free one.</summary>
    internal static void Assign(ulong id, ICollection<ulong> present)
    {
        foreach (var gone in slots.Keys.Where(k => !present.Contains(k)).ToList()) slots.Remove(gone);
        if (!slots.ContainsKey(id))
        {
            int slot = 1;
            while (slot < KoboldAvatar.Palette.Length - 1 && slots.ContainsValue(slot)) slot++;
            slots[id] = slot;
        }
        Version++;
    }

    internal static void Forget(ulong id) { if (slots.Remove(id)) Version++; }

    internal static void Clear() { if (slots.Count > 0) { slots.Clear(); Version++; } }

    internal static void Write(BinaryWriter w)
    {
        w.Write((byte)slots.Count);
        foreach (var pair in slots) { w.Write(pair.Key); w.Write((byte)pair.Value); }
    }

    internal static void Read(BinaryReader r)
    {
        int count = r.ReadByte();
        if (count > 16) throw new InvalidDataException("Too many player colours");
        var table = new Dictionary<ulong, int>();
        for (int i = 0; i < count; i++)
        {
            ulong id = r.ReadUInt64();
            int slot = r.ReadByte();
            if (slot < 1 || slot >= KoboldAvatar.Palette.Length) throw new InvalidDataException("Bad player colour");
            table[id] = slot;
        }
        slots.Clear();
        foreach (var pair in table) slots[pair.Key] = pair.Value;
        Version++;
    }

    // ---------------------------------------------------------------- sounds

    private static readonly System.Reflection.FieldInfo SceneInstance = AccessTools.Field(typeof(WalkNWashSceneState), "instance");
    private static readonly System.Reflection.FieldInfo YarnSfx = AccessTools.Field(typeof(WalkNWashSceneState), "yarnSFX");
    private static AudioResource? yip;

    /// <summary>The player's "Yip!" from the story (the sound the game plays when you answer "Yip!").</summary>
    private static AudioResource? YipSound
    {
        get
        {
            if (yip != null) return yip;
            if (SceneInstance?.GetValue(null) is WalkNWashSceneState state && YarnSfx?.GetValue(state) is AudioResource[] sounds)
                yip = sounds.FirstOrDefault(s => s != null && s.name == "yip");
            if (yip == null) yip = Resources.FindObjectsOfTypeAll<AudioResource>().FirstOrDefault(s => s.name == "yip");
            return yip;
        }
    }

    /// <summary>A yip in that colour's voice, heard from <paramref name="at"/> (and moving with it, if given).</summary>
    internal static void PlayYip(Vector3 position, int slot, Transform? at = null)
    {
        var sound = YipSound;
        if (sound == null) return;
        var go = new GameObject("CoopYip");
        go.transform.position = position;
        if (at != null) go.transform.SetParent(at, true);
        var source = go.AddComponent<AudioSource>();
        AudioHelper.SetupAudioSourceForSFX(source);
        source.resource = sound;
        source.pitch = Pitch(slot) * Random.Range(.97f, 1.03f);
        source.Play();
        Object.Destroy(go, source.clip != null ? source.clip.length / source.pitch + .1f : 2f);
    }

    private static readonly RaycastHit[] hits = new RaycastHit[16];
    internal static int Footsteps, FootstepSounds;   // (developer check)

    /// <summary>
    /// A footstep where a friend's kobold put its foot down: the floor's own step sound (and splash, on wet floors),
    /// found the same way the game finds yours, a little quieter when they're only shuffling.
    /// </summary>
    internal static void Footstep(Vector3 foot, float volume)
    {
        var database = PhysicsMaterialExtensionDatabase.GetDatabase();
        if (database == null) return;
        int n = Physics.RaycastNonAlloc(new Ray(foot + Vector3.up * .5f, Vector3.down), hits, 1f, 129, QueryTriggerInteraction.Ignore);
        int best = -1;
        for (int i = 0; i < n; i++) if (best < 0 || hits[i].distance < hits[best].distance) best = i;
        Footsteps++;
        if (best < 0) return;
        var hit = hits[best];
        if (!database.TryGetImpactInfo(hit.collider.sharedMaterial, PhysicsMaterialExtension.PhysicMaterialInfoType.Soft,
                                       PhysicsMaterialExtension.PhysicsResponseType.Footstep, out var info) || info == null) return;
        if (info.soundEffect != null)
        {
            FootstepSounds++;
            var go = new GameObject("CoopFootstep");
            go.transform.position = hit.point;
            var source = go.AddComponent<AudioSource>();
            AudioHelper.SetupAudioSourceForSFX(source);
            source.resource = info.soundEffect;
            source.volume = volume;
            source.Play();
            Object.Destroy(go, source.clip != null ? source.clip.length + .1f : 3f);
        }
        if (info.visualEffects != null && info.visualEffects.Count > 0)
        {
            var go = new GameObject("CoopFootstepFx", typeof(VisualEffect));
            go.transform.SetPositionAndRotation(hit.point, Quaternion.LookRotation(hit.normal));
            var effect = go.GetComponent<VisualEffect>();
            effect.visualEffectAsset = info.visualEffects[Random.Range(0, info.visualEffects.Count)];
            effect.Play();
            Object.Destroy(go, 3f);
        }
    }
}
