using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace DragNWashCoop;

/// <summary>
/// The co-op player avatar (the kobold from KoboldKare, CC0, by naelstrof), exported by avatar/export_avatar.py to
/// "KBAV" v1 and embedded in the DLL. Everything is already in Unity space, metres, facing +Z at rest.
/// </summary>
internal sealed class AvatarData
{
    internal float EyeHeight, HipHeight, LegLength, ArmLength;
    internal string[] BoneNames = Array.Empty<string>();
    internal int[] Parents = Array.Empty<int>();
    internal Vector3[] LocalPositions = Array.Empty<Vector3>();
    internal Quaternion[] LocalRotations = Array.Empty<Quaternion>();
    internal readonly Dictionary<string, int> Roles = new();
    internal Mesh Mesh = null!;
    internal Texture2D?[] Textures = Array.Empty<Texture2D?>();
    internal Color[] Colors = Array.Empty<Color>();
    internal float[] Smoothness = Array.Empty<float>();
    internal byte[]?[] TextureBytes = Array.Empty<byte[]?>();

    private static AvatarData? cached;
    private static bool tried;

    /// <summary>The embedded avatar, loaded once (null if missing or unreadable: remote players fall back to hands).</summary>
    internal static AvatarData? Get(BepInEx.Logging.ManualLogSource log)
    {
        if (tried) return cached;
        tried = true;
        try
        {
            using var stream = typeof(AvatarData).Assembly.GetManifestResourceStream("DragNWashCoop.kobold_avatar.bin");
            if (stream == null) { log.LogWarning("No embedded kobold avatar; friends are drawn as hands"); return null; }
            cached = Read(new BinaryReader(stream));
            log.LogInfo($"Kobold avatar loaded: {cached.BoneNames.Length} bones, {cached.Mesh.vertexCount} vertices");
        }
        catch (Exception e) { log.LogWarning($"Kobold avatar failed to load ({e.Message}); friends are drawn as hands"); }
        return cached;
    }

    private static string Str(BinaryReader r) => Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()));
    private static Vector3 V3(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    private static AvatarData Read(BinaryReader r)
    {
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "KBAV") throw new InvalidDataException("not a KBAV file");
        if (r.ReadUInt32() != 1) throw new InvalidDataException("unsupported KBAV version");
        var d = new AvatarData { EyeHeight = r.ReadSingle(), HipHeight = r.ReadSingle(), LegLength = r.ReadSingle(), ArmLength = r.ReadSingle() };
        int nb = (int)r.ReadUInt32();
        d.BoneNames = new string[nb]; d.Parents = new int[nb];
        d.LocalPositions = new Vector3[nb]; d.LocalRotations = new Quaternion[nb];
        for (int i = 0; i < nb; i++)
        {
            d.BoneNames[i] = Str(r);
            d.Parents[i] = r.ReadInt32();
            d.LocalPositions[i] = V3(r);
            d.LocalRotations[i] = new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            V3(r);   // scale (always 1)
        }
        int nr = (int)r.ReadUInt32();
        for (int i = 0; i < nr; i++) { var role = Str(r); int bone = r.ReadInt32(); if (bone >= 0) d.Roles[role] = bone; }

        int nv = (int)r.ReadUInt32();
        var pos = new Vector3[nv]; var nrm = new Vector3[nv]; var uv = new Vector2[nv]; var bw = new BoneWeight[nv];
        for (int i = 0; i < nv; i++) pos[i] = V3(r);
        for (int i = 0; i < nv; i++) nrm[i] = V3(r);
        for (int i = 0; i < nv; i++) uv[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
        for (int i = 0; i < nv; i++)
        {
            int a = r.ReadUInt16(), b = r.ReadUInt16(), c = r.ReadUInt16(), e = r.ReadUInt16();
            bw[i] = new BoneWeight { boneIndex0 = a, boneIndex1 = b, boneIndex2 = c, boneIndex3 = e,
                                     weight0 = r.ReadSingle(), weight1 = r.ReadSingle(), weight2 = r.ReadSingle(), weight3 = r.ReadSingle() };
        }
        var mesh = new Mesh { name = "KoboldAvatar", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(pos); mesh.SetNormals(nrm); mesh.SetUVs(0, uv);
        mesh.boneWeights = bw;
        int ns = (int)r.ReadUInt32();
        var subs = new List<(int mat, int[] idx)>();
        for (int s = 0; s < ns; s++)
        {
            int mat = (int)r.ReadUInt32();
            int n = (int)r.ReadUInt32();
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = (int)r.ReadUInt32();
            subs.Add((mat, idx));
        }
        mesh.subMeshCount = subs.Count;
        for (int s = 0; s < subs.Count; s++) mesh.SetTriangles(subs[s].idx, s);
        int nshape = (int)r.ReadUInt32();
        for (int s = 0; s < nshape; s++)
        {
            var name = Str(r);
            var delta = new Vector3[nv];
            int n = (int)r.ReadUInt32();
            for (int i = 0; i < n; i++) { int vi = (int)r.ReadUInt32(); delta[vi] = V3(r); }
            mesh.AddBlendShapeFrame(name, 100f, delta, null, null);
        }
        int nm = (int)r.ReadUInt32();
        var colors = new Color[nm]; var smooth = new float[nm]; var bytes = new byte[]?[nm];
        for (int m = 0; m < nm; m++)
        {
            Str(r);
            colors[m] = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            smooth[m] = r.ReadSingle();
            int len = (int)r.ReadUInt32();
            bytes[m] = len > 0 ? r.ReadBytes(len) : null;
        }
        // materials per submesh, in submesh order
        d.Colors = new Color[subs.Count]; d.Smoothness = new float[subs.Count]; d.TextureBytes = new byte[]?[subs.Count];
        for (int s = 0; s < subs.Count; s++)
        {
            d.Colors[s] = colors[subs[s].mat]; d.Smoothness[s] = smooth[subs[s].mat]; d.TextureBytes[s] = bytes[subs[s].mat];
        }
        d.Mesh = mesh;
        // bind poses from the rest hierarchy (vertices are in the root's space at rest)
        var world = new Matrix4x4[nb];
        for (int i = 0; i < nb; i++)
        {
            var local = Matrix4x4.TRS(d.LocalPositions[i], d.LocalRotations[i], Vector3.one);
            world[i] = d.Parents[i] >= 0 ? world[d.Parents[i]] * local : local;
        }
        var bind = new Matrix4x4[nb];
        for (int i = 0; i < nb; i++) bind[i] = world[i].inverse;
        mesh.bindposes = bind;
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        return d;
    }

    /// <summary>One colour recoloured the same way as <see cref="Recolour"/> (e.g. a player's colour swatch).</summary>
    internal static Color Shift(Color c, float hueDegrees, float brightness = 0f, float saturation = 1f)
    {
        float a = hueDegrees * Mathf.Deg2Rad, r = c.r, g = c.g, b = c.b;
        Shift(ref r, ref g, ref b, Mathf.Cos(a), Mathf.Sin(a), brightness, saturation);
        return new Color(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b), c.a);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static void Shift(ref float r, ref float g, ref float b, float cs, float sn, float brightness, float saturation)
    {
        // Rodrigues: v cos + (k x v) sin + k (k.v)(1 - cos), k = (1,1,1)/sqrt3
        const float k = .57735f;
        float kv = k * (r + g + b) * (1 - cs);
        float cr = k * (b - g), cg = k * (r - b), cb = k * (g - r);
        float nr = r * cs + cr * sn + k * kv, ng = g * cs + cg * sn + k * kv, nb = b * cs + cb * sn + k * kv;
        nr += brightness; ng += brightness; nb += brightness;
        float lum = nr * .299f + ng * .587f + nb * .114f;
        r = lum + (nr - lum) * saturation; g = lum + (ng - lum) * saturation; b = lum + (nb - lum) * saturation;
    }

    /// <summary>
    /// A kobold's body texture recoloured like KoboldKare does it (hue rotation about the grey axis, then brightness /
    /// contrast / saturation), so each player can be their own colour.
    /// </summary>
    internal static Texture2D Recolour(byte[] png, float hueDegrees, float brightness = 0f, float saturation = 1f)
    {
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, false);
        tex.LoadImage(png, false);
        if (Mathf.Abs(hueDegrees) > .5f || Mathf.Abs(brightness) > .001f || Mathf.Abs(saturation - 1f) > .001f)
        {
            var px = tex.GetPixels32();
            float a = hueDegrees * Mathf.Deg2Rad, cs = Mathf.Cos(a), sn = Mathf.Sin(a);
            for (int i = 0; i < px.Length; i++)
            {
                if (px[i].a < 128) { px[i].a = 255; continue; }   // alpha 0 marks the gear (collar, gloves): not recoloured
                float r = px[i].r / 255f, g = px[i].g / 255f, b = px[i].b / 255f;
                Shift(ref r, ref g, ref b, cs, sn, brightness, saturation);
                px[i] = new Color32((byte)(Mathf.Clamp01(r) * 255), (byte)(Mathf.Clamp01(g) * 255), (byte)(Mathf.Clamp01(b) * 255), 255);
            }
            tex.SetPixels32(px);
        }
        else
        {
            var px = tex.GetPixels32();
            for (int i = 0; i < px.Length; i++) px[i].a = 255;
            tex.SetPixels32(px);
        }
        tex.Apply(true, true);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.anisoLevel = 4;
        return tex;
    }
}
