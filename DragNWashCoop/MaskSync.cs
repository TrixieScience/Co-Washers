using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using SkinnedMeshDecals;
using UnityEngine;
using UnityEngine.Rendering;

namespace DragNWashCoop;

/// <summary>Ordered paint stamps and lossless texture snapshots for mid-wash joining.</summary>
internal sealed class MaskSync
{
    private static readonly string[] Channels = { "_CleaningMask", "_RinsingMask", "_WetMask", "_FluidHeight", "_DirtyMap" };
    private const int ChunkSize = 32 * 1024;
    // The decal library applies only every Nth of a frame's decals once ~62 are queued and drops the rest,
    // so stamps from the network are painted a few per frame.
    private const int PaintPerFrame = 20;
    private const float RepairInterval = 60f;
    private readonly CoopPlugin owner;
    private readonly CoopWorld world;
    private readonly List<byte[]> pendingGuest = new();
    private readonly List<byte[]> pendingHost = new();
    private readonly Queue<(byte[] Bytes, bool Rebroadcast)> toPaint = new();
    private readonly Dictionary<string, RenderTexture> receivedTextures = new();
    private readonly Dictionary<byte, Incoming> incoming = new();
    private readonly Dictionary<string, Texture?> texturesByName = new();
    private readonly List<(uint Seq, List<byte[]> Stamps, float At)> recentHostBatches = new();   // guest
    private readonly Dictionary<Peer, Outgoing> snapshotsInFlight = new();                         // host
    private readonly ConcurrentQueue<Action> onMainThread = new();
    private float nextFlush;
    private float nextRepair;
    private float lastRepair;
    private float receivingSince;
    private float retryAt = float.MaxValue;
    private uint snapshotId;
    private uint receivingId;
    private uint batchSeq;
    private (uint Transfer, int Expected, uint Seq)? finishPending;
    private bool replaying;
    private int snapshotRetries;

    private sealed class Incoming
    {
        internal int Width, Height;
        internal TextureFormat Format;
        internal byte[] Compressed = Array.Empty<byte>();
        internal int Received;
    }

    private sealed class Outgoing
    {
        internal uint Transfer, Epoch, Seq;
        internal int Remaining, Sent;
        internal bool Ended;
    }

    /// <summary>Guest: the host's masks for this scene have arrived (refreshes after that do not reset it).</summary>
    internal bool Ready { get; private set; }
    /// <summary>Above zero while a player's own sponge or sprayer paints (see PlayerPaintPatch).</summary>
    internal static int PlayerPaint;
    internal MaskSync(CoopPlugin owner, CoopWorld world) { this.owner = owner; this.world = world; }

    internal void Tick()
    {
        while (onMainThread.TryDequeue(out var action)) action();
        for (int i = 0; i < PaintPerFrame && toPaint.Count > 0; i++)
        {
            var (bytes, rebroadcast) = toPaint.Dequeue();
            // a guest's stamp goes out to everyone once the host has painted it, so a snapshot taken
            // meanwhile and the stamps that follow it agree
            if (Paint(bytes) && rebroadcast && world.IsHost) pendingHost.Add(bytes);
        }
        if (Time.unscaledTime >= nextFlush)
        {
            nextFlush = Time.unscaledTime + .05f;
            if (world.IsGuest) Flush(pendingGuest, true);
            if (world.IsHost) Flush(pendingHost, false);
        }
        if (world.IsGuest)
        {
            if (finishPending is { } pending && WalkNWashSceneState.TryGetActiveDragon(out _))
            {
                finishPending = null;
                CompleteSnapshot(pending.Transfer, pending.Expected, pending.Seq);
            }
            if (incoming.Count > 0 && Time.unscaledTime - receivingSince > 90f) RetrySnapshot();   // stalled
            if (Time.unscaledTime >= retryAt)
            {
                retryAt = float.MaxValue;
                owner.Session.SendToHost(PacketKind.Ping, world.Epoch, w => w.Write((byte)1));
            }
        }
        if (world.IsHost)
        {
            foreach (var peer in snapshotsInFlight.Keys.ToArray())
                if (!owner.Session.Peers.Contains(peer) ||
                    (snapshotsInFlight[peer].Ended && !owner.Session.HasBulkPending(peer)))
                    snapshotsInFlight.Remove(peer);
            if (Time.unscaledTime >= nextRepair && owner.Session.Peers.Count > 0)
            {
                nextRepair = Time.unscaledTime + RepairInterval;
                lastRepair = Time.unscaledTime;
                foreach (var peer in owner.Session.Peers)
                    if (peer.Authenticated) SendSnapshot(peer);
            }
        }
    }

    internal void Clear()
    {
        pendingGuest.Clear(); pendingHost.Clear(); incoming.Clear(); toPaint.Clear();
        recentHostBatches.Clear(); snapshotsInFlight.Clear();
        foreach (var texture in receivedTextures.Values) if (texture != null) UnityEngine.Object.Destroy(texture);
        receivedTextures.Clear(); Ready = false; receivingId = 0;
        finishPending = null;
        retryAt = float.MaxValue;
        snapshotRetries = 0;
        nextRepair = Time.unscaledTime + RepairInterval;
    }

    /// <returns>False on guests, so their local effects cannot change the authoritative mask.</returns>
    internal bool AllowLocal(Renderer renderer, DecalProjector projector, DecalProjection projection, DecalSettings? settings)
    {
        if (replaying || !world.Started || !IsDragonRenderer(renderer)) return true;
        if (!world.IsGuest) return true;
        if (world.ApplyingNetworkEvent) return false;
        // The dragon's own setup stamps (e.g. the cleaning mask the game creates on spawn) must run;
        // the host's snapshot replaces whatever they paint.
        if (!Ready) return true;
        // Only this player's own tool paint goes to the host. Ryan's own effects (drying, cum, dirt) also
        // run on the host, which sends its copy; sending the guest's too would apply them twice.
        if (PlayerPaint <= 0 || !world.UsedToolRecently) return false;
        if (TrySerialize(renderer, projector, projection, settings, out var stamp)) pendingGuest.Add(stamp);
        return false;
    }

    internal void RecordLocal(Renderer renderer, DecalProjector projector, DecalProjection projection, DecalSettings? settings)
    {
        if (replaying || !world.IsHost || !IsDragonRenderer(renderer)) return;
        if (TrySerialize(renderer, projector, projection, settings, out var stamp)) pendingHost.Add(stamp);
        else nextRepair = Mathf.Min(nextRepair, Mathf.Max(Time.unscaledTime + .25f, lastRepair + 10f));
    }

    private static bool IsDragonRenderer(Renderer renderer)
    {
        if (renderer == null || !WalkNWashSceneState.TryGetActiveDragon(out var dragon)) return false;
        return renderer.transform == dragon.skin.transform || renderer.transform.IsChildOf(dragon.gameObject.transform);
    }

    private static bool TrySerialize(Renderer renderer, DecalProjector projector, DecalProjection projection,
                                     DecalSettings? settings, out byte[] stamp)
    {
        stamp = Array.Empty<byte>();
        if (projector.projectorType == DecalProjectorType.Custom) return false;
        int property = settings?.textureID ?? Shader.PropertyToID("_DirtyMap");
        int channel = Array.FindIndex(Channels, c => Shader.PropertyToID(c) == property);
        if (channel < 0) return false;
        try
        {
            using var stream = new MemoryStream();
            using var w = new BinaryWriter(stream);
            w.WriteShortString(EntityIds.For(renderer));
            w.Write((byte)channel); w.Write((byte)projector.projectorType);
            var material = projector.material;
            var color = material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
            w.Write(color.r); w.Write(color.g); w.Write(color.b); w.Write(color.a);
            w.Write(material.HasProperty("_Power") ? material.GetFloat("_Power") : 1f);
            w.WriteShortString(projector.projectorType <= DecalProjectorType.TextureSubtractive &&
                               material.HasProperty("_MainTex") ? material.GetTexture("_MainTex")?.name : "");
            w.Write(material.IsKeywordEnabled("_BACKFACECULLING_ON"));
            w.Write((byte)projection.type);
            WriteMatrix(w, projection.projection); WriteMatrix(w, projection.view);
            w.Write((byte)(settings?.dilation ?? DilationType.None));
            w.Write((int)(settings?.renderTextureFormat ?? RenderTextureFormat.Default));
            w.Write((byte)(settings?.renderTextureReadWrite ?? RenderTextureReadWrite.Default));
            var resolution = settings?.resolution ?? SkinnedMeshDecalsSettings.DefaultDecalSettings.resolution;
            w.Write((byte)resolution.resolutionType);
            w.Write((ushort)Mathf.Clamp(resolution.size.x, 16, 8192));
            w.Write((ushort)Mathf.Clamp(resolution.size.y, 16, 8192));
            w.Write(resolution.texelsPerMeter);
            stamp = stream.ToArray();
            return stamp.Length < 1024;
        }
        catch { return false; }
    }

    private static void WriteMatrix(BinaryWriter writer, Matrix4x4 matrix)
    { for (int i = 0; i < 16; i++) writer.Write(matrix[i]); }

    private static Matrix4x4 ReadMatrix(BinaryReader reader)
    {
        var matrix = new Matrix4x4();
        for (int i = 0; i < 16; i++) matrix[i] = reader.ReadSingle();
        return matrix;
    }

    private void Flush(List<byte[]> pending, bool guest)
    {
        while (pending.Count > 0)
        {
            int size = 8, count = 0;
            while (count < pending.Count && count < 128 && size + pending[count].Length + 2 < 42 * 1024)
            { size += pending[count].Length + 2; count++; }
            if (count == 0) { pending.RemoveAt(0); continue; }
            var batch = pending.GetRange(0, count);
            pending.RemoveRange(0, count);
            if (guest)
                owner.Session.SendToHost(PacketKind.ActionRequest, world.Epoch, w =>
                {
                    w.Write((byte)WorldEventKind.Decal);
                    w.Write((ushort)batch.Count);
                    foreach (var stamp in batch) { w.Write((ushort)stamp.Length); w.Write(stamp); }
                });
            else
            {
                uint seq = ++batchSeq;   // lets a guest re-apply what its snapshot missed
                owner.Session.Broadcast(PacketKind.WorldEvent, world.Epoch, w =>
                {
                    w.Write((byte)WorldEventKind.Decal);
                    w.Write(seq);
                    w.Write((ushort)batch.Count);
                    foreach (var stamp in batch) { w.Write((ushort)stamp.Length); w.Write(stamp); }
                });
            }
        }
    }

    /// <summary>Host: a guest's paint. Checked now, painted a few per frame, then sent to everyone.</summary>
    internal void ReceiveGuestBatch(Peer peer, BinaryReader reader)
    {
        if (!world.IsHost) return;
        foreach (var bytes in ReadBatch(reader))
            if (TryReadStamp(bytes, out var stamp) && world.ValidateRemotePaint(peer, stamp.Centre))
                toPaint.Enqueue((bytes, true));
    }

    internal void ReceiveHostBatch(BinaryReader reader)
    {
        if (!world.IsGuest) return;
        uint seq = reader.ReadUInt32();
        var stamps = ReadBatch(reader);
        recentHostBatches.Add((seq, stamps, Time.unscaledTime));
        // a snapshot can take a while to arrive; keep what might need re-applying after it
        while (recentHostBatches.Count > 0 &&
               (recentHostBatches.Count > 600 || Time.unscaledTime - recentHostBatches[0].At > 60f))
            recentHostBatches.RemoveAt(0);
        foreach (var bytes in stamps) toPaint.Enqueue((bytes, false));
    }

    private static List<byte[]> ReadBatch(BinaryReader reader)
    {
        int count = reader.ReadUInt16();
        if (count > 128) throw new InvalidDataException("Too many decal stamps");
        var stamps = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            int len = reader.ReadUInt16();
            if (len == 0 || len > 1024) throw new InvalidDataException("Bad decal stamp length");
            var bytes = reader.ReadBytes(len);
            if (bytes.Length != len) throw new EndOfStreamException();
            stamps.Add(bytes);
        }
        return stamps;
    }

    private struct Stamp
    {
        internal Renderer Renderer;
        internal int Channel;
        internal DecalProjectorType Type;
        internal Color Color;
        internal float Power;
        internal string TextureName;
        internal bool Backface;
        internal DecalProjection.DecalProjectionType ProjectionType;
        internal Matrix4x4 Projection, View;
        internal DilationType Dilation;
        internal RenderTextureFormat Format;
        internal RenderTextureReadWrite ReadWrite;
        internal DecalResolutionType ResolutionType;
        internal int Width, Height;
        internal float Texels;
        internal Vector3 Centre;
    }

    private static bool TryReadStamp(byte[] bytes, out Stamp stamp)
    {
        stamp = default;
        using var r = new BinaryReader(new MemoryStream(bytes));
        string target = r.ReadShortString();
        stamp.Channel = r.ReadByte();
        stamp.Type = (DecalProjectorType)r.ReadByte();
        stamp.Color = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        stamp.Power = r.ReadSingle(); stamp.TextureName = r.ReadShortString(); stamp.Backface = r.ReadBoolean();
        stamp.ProjectionType = (DecalProjection.DecalProjectionType)r.ReadByte();
        stamp.Projection = ReadMatrix(r); stamp.View = ReadMatrix(r);
        stamp.Dilation = (DilationType)r.ReadByte();
        stamp.Format = (RenderTextureFormat)r.ReadInt32();
        stamp.ReadWrite = (RenderTextureReadWrite)r.ReadByte();
        stamp.ResolutionType = (DecalResolutionType)r.ReadByte();
        stamp.Width = r.ReadUInt16(); stamp.Height = r.ReadUInt16();
        stamp.Texels = r.ReadSingle();
        if (stamp.Channel >= Channels.Length || stamp.Type > DecalProjectorType.SphereSubtractive ||
            !float.IsFinite(stamp.Power) || stamp.Width > 8192 || stamp.Height > 8192 || !float.IsFinite(stamp.Texels)) return false;
        var obj = EntityIds.Find(target);
        if (obj == null || obj.GetComponent<Renderer>() is not { } renderer || !IsDragonRenderer(renderer)) return false;
        stamp.Renderer = renderer;
        stamp.Centre = stamp.View.inverse.GetColumn(3);
        return float.IsFinite(stamp.Centre.x) && float.IsFinite(stamp.Centre.y) && float.IsFinite(stamp.Centre.z);
    }

    private bool Paint(byte[] bytes)
    {
        if (!TryReadStamp(bytes, out var stamp)) return false;
        DecalProjector projector;
        if (stamp.Type <= DecalProjectorType.TextureSubtractive)
        {
            if (!texturesByName.TryGetValue(stamp.TextureName, out var texture) || texture == null)
                texturesByName[stamp.TextureName] = texture =
                    Resources.FindObjectsOfTypeAll<Texture>().FirstOrDefault(t => t.name == stamp.TextureName);
            if (texture == null) return false;
            projector = new DecalProjector(stamp.Type, texture, stamp.Color, stamp.Backface);
            if (projector.material.HasProperty("_Power")) projector.material.SetFloat("_Power", stamp.Power);
        }
        else projector = new DecalProjector(stamp.Type, stamp.Power, stamp.Color, stamp.Backface);
        var projection = new DecalProjection(stamp.Projection, stamp.View) { type = stamp.ProjectionType };
        var resolution = new DecalResolution(new Vector2Int(stamp.Width, stamp.Height));
        resolution.texelsPerMeter = stamp.Texels;
        resolution.resolutionType = stamp.ResolutionType;
        var settings = new DecalSettings(resolution, stamp.Dilation, Channels[stamp.Channel], stamp.Format, stamp.ReadWrite);
        replaying = true;
        try { PaintDecal.QueueDecal(stamp.Renderer, projector, projection, settings); }
        finally { replaying = false; }
        return true;
    }

    /// <summary>
    /// Host: every mask of the dragon, compressed off the main thread and sent through the paced
    /// channel. One at a time per guest.
    /// </summary>
    internal void SendSnapshot(Peer peer)
    {
        if (!world.IsHost || !WalkNWashSceneState.TryGetActiveDragon(out var dragon) || snapshotsInFlight.ContainsKey(peer)) return;
        Flush(pendingHost, false);   // stamps already on the textures go out before the snapshot's number is taken
        var state = new Outgoing { Transfer = ++snapshotId, Epoch = world.Epoch, Seq = batchSeq, Remaining = Channels.Length };
        snapshotsInFlight[peer] = state;
        for (int i = 0; i < Channels.Length; i++)
        {
            int index = i;
            string name = Channels[i];
            var texture = PaintDecal.GetDecalTexture(dragon.skin, Shader.PropertyToID(name));   // a copy we must free
            if (texture == null) { ChannelDone(peer, state); continue; }
            int width = texture.width, height = texture.height;
            var uploadFormat = texture.format == RenderTextureFormat.R16 ? TextureFormat.R16 :
                               texture.format == RenderTextureFormat.RFloat ? TextureFormat.RFloat : TextureFormat.RGBA32;
            AsyncGPUReadback.Request(texture, 0, uploadFormat, request =>
            {
                byte[]? raw = null;
                try { if (!request.hasError) raw = request.GetData<byte>().ToArray(); }
                catch (Exception e) { owner.Log.LogWarning($"Mask snapshot {name} failed: {e.Message}"); }
                finally { texture.Release(); UnityEngine.Object.Destroy(texture); }
                if (raw == null || world.Epoch != state.Epoch) { ChannelDone(peer, state); return; }
                Task.Run(() =>
                {
                    byte[]? zipped = null;
                    try
                    {
                        using var stream = new MemoryStream();
                        using (var deflate = new DeflateStream(stream, System.IO.Compression.CompressionLevel.Fastest, true))
                            deflate.Write(raw, 0, raw.Length);
                        zipped = stream.ToArray();
                    }
                    catch (Exception e) { onMainThread.Enqueue(() => owner.Log.LogWarning($"Mask snapshot {name} failed: {e.Message}")); }
                    onMainThread.Enqueue(() =>
                    {
                        if (zipped != null && world.Epoch == state.Epoch && owner.Session.Peers.Contains(peer))
                        {
                            for (int offset = 0; offset < zipped.Length; offset += ChunkSize)
                            {
                                int from = offset, length = Math.Min(ChunkSize, zipped.Length - offset);
                                owner.Session.SendBulk(peer, PacketKind.SnapshotChunk, state.Epoch, w =>
                                {
                                    w.Write(state.Transfer); w.Write((byte)index);
                                    w.Write((ushort)width); w.Write((ushort)height);
                                    w.Write((byte)uploadFormat);
                                    w.Write(zipped.Length); w.Write(from);
                                    w.Write((ushort)length); w.Write(zipped, from, length);
                                });
                            }
                            state.Sent++;
                        }
                        ChannelDone(peer, state);
                    });
                });
            });
        }
    }

    private void ChannelDone(Peer peer, Outgoing state)
    {
        if (--state.Remaining > 0) return;
        state.Ended = true;
        if (world.Epoch == state.Epoch && owner.Session.Peers.Contains(peer))
            owner.Session.SendBulk(peer, PacketKind.SnapshotEnd, state.Epoch, w =>
            {
                w.Write(state.Transfer); w.Write((byte)state.Sent); w.Write(state.Seq);
            });
    }

    internal void ReceiveChunk(BinaryReader reader)
    {
        if (!world.IsGuest) return;
        uint transfer = reader.ReadUInt32(); byte channel = reader.ReadByte();
        int width = reader.ReadUInt16(), height = reader.ReadUInt16();
        var format = (TextureFormat)reader.ReadByte();
        int total = reader.ReadInt32(), offset = reader.ReadInt32(), count = reader.ReadUInt16();
        if (channel >= Channels.Length || width < 16 || height < 16 || width > 4096 || height > 4096 ||
            total < 1 || total > 32 * 1024 * 1024 || offset < 0 || count > ChunkSize || offset + count > total)
            throw new InvalidDataException("Invalid mask snapshot chunk");
        if (receivingId != transfer) { receivingId = transfer; incoming.Clear(); receivingSince = Time.unscaledTime; }
        if (!incoming.TryGetValue(channel, out var blob))
        {
            blob = new Incoming { Width = width, Height = height, Format = format, Compressed = new byte[total] };
            incoming[channel] = blob;
        }
        if (blob.Compressed.Length != total || blob.Width != width || blob.Height != height) return;
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count) throw new EndOfStreamException();
        Buffer.BlockCopy(bytes, 0, blob.Compressed, offset, count);
        blob.Received += count;
    }

    internal void FinishSnapshot(BinaryReader reader)
    {
        if (!world.IsGuest) return;
        uint transfer = reader.ReadUInt32(); int expected = reader.ReadByte(); uint seq = reader.ReadUInt32();
        if (expected == 0) { Ready = true; world.SnapshotApplied(); return; }
        // the dragon may still be spawning (e.g. just after a level change): finish once it exists
        if (!WalkNWashSceneState.TryGetActiveDragon(out _)) { finishPending = (transfer, expected, seq); return; }
        CompleteSnapshot(transfer, expected, seq);
    }

    private void CompleteSnapshot(uint transfer, int expected, uint seq)
    {
        if (!WalkNWashSceneState.TryGetActiveDragon(out var dragon)) return;
        if (transfer != receivingId || incoming.Count != expected) { RetrySnapshot(); return; }
        try
        {
            foreach (var entry in incoming)
            {
                var blob = entry.Value;
                if (blob.Received != blob.Compressed.Length) throw new InvalidDataException("Incomplete mask");
                int bytesPerPixel = blob.Format == TextureFormat.R16 ? 2 : 4;
                int expectedRaw = checked(blob.Width * blob.Height * bytesPerPixel);
                using var input = new DeflateStream(new MemoryStream(blob.Compressed), CompressionMode.Decompress);
                using var rawStream = new MemoryStream(expectedRaw);
                var buffer = new byte[8192];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (rawStream.Length + read > expectedRaw) throw new InvalidDataException("Mask decompressed too large");
                    rawStream.Write(buffer, 0, read);
                }
                var raw = rawStream.ToArray();
                if (raw.Length != expectedRaw) throw new InvalidDataException("Mask size mismatch");
                var upload = new Texture2D(blob.Width, blob.Height, blob.Format, false, true);
                upload.LoadRawTextureData(raw); upload.Apply(false, false);
                var rtFormat = blob.Format == TextureFormat.R16 ? RenderTextureFormat.R16 :
                               blob.Format == TextureFormat.RFloat ? RenderTextureFormat.RFloat : RenderTextureFormat.ARGB32;
                var texture = new RenderTexture(blob.Width, blob.Height, 0, rtFormat)
                { useMipMap = true, autoGenerateMips = false };
                texture.Create();
                var active = RenderTexture.active;   // Blit leaves its target active
                Graphics.Blit(upload, texture);
                RenderTexture.active = active;
                texture.GenerateMips();
                UnityEngine.Object.Destroy(upload);
                string name = Channels[entry.Key];
                // hand the new texture over before freeing the previous one the decal system was using
                PaintDecal.OverrideDecalTexture(dragon.skin, texture, Shader.PropertyToID(name), DilationType.Additive);
                if (receivedTextures.TryGetValue(name, out var old) && old != null) UnityEngine.Object.Destroy(old);
                receivedTextures[name] = texture;
            }
        }
        catch (Exception e) { owner.Log.LogWarning($"Bad mask snapshot: {e.Message}"); RetrySnapshot(); return; }
        incoming.Clear(); Ready = true;
        snapshotRetries = 0;
        // stamps painted onto the old textures while the snapshot was on its way: the ones the host sent
        // after taking it are re-applied, the rest are already in it
        toPaint.Clear();
        foreach (var batch in recentHostBatches)
            if (batch.Seq > seq)
                foreach (var bytes in batch.Stamps) toPaint.Enqueue((bytes, false));
        world.SnapshotApplied();
        owner.Log.LogInfo($"Applied co-op mask snapshot {transfer} ({expected} channels)");
    }

    private void RetrySnapshot()
    {
        incoming.Clear();
        if (++snapshotRetries > 5)
        {
            owner.Log.LogError("Could not synchronize dragon paint masks; waiting for the host's next refresh");
            snapshotRetries = 0;
            return;
        }
        retryAt = Time.unscaledTime + 3f;   // not at once: the previous snapshot may still be on its way
    }
}
