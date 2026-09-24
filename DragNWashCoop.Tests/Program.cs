using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using DragNWashCoop;

namespace UnityEngine
{
    public readonly struct Vector3(float x, float y, float z)
    {
        public readonly float x = x, y = y, z = z;
    }
    public readonly struct Quaternion(float x, float y, float z, float w)
    {
        public readonly float x = x, y = y, z = z, w = w;
    }
}

internal static class Program
{
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception("FAIL " + name);
        Console.WriteLine("PASS " + name);
    }

    private static void Main()
    {
        var bytes = Envelope.Encode(PacketKind.Pose, 19, 83, w =>
        {
            w.WriteShortString("Räyn 🐉");
            w.Write(new UnityEngine.Vector3(1.5f, -2f, 3.25f));
            w.Write(new UnityEngine.Quaternion(0, .5f, 0, .8660254f));
        });
        Check(Envelope.TryDecode(bytes, out var packet), "valid packet");
        using (packet.Reader)
        {
            Check(packet.Kind == PacketKind.Pose && packet.SceneEpoch == 19 && packet.Sequence == 83,
                  "header preserves kind, scene, and sequence");
            Check(packet.Reader.ReadShortString() == "Räyn 🐉", "UTF-8 name roundtrip");
            var point = packet.Reader.ReadVector3();
            var rotation = packet.Reader.ReadQuaternion();
            Check(point.x == 1.5f && point.y == -2f && point.z == 3.25f && rotation.w > .86f,
                  "pose data roundtrip");
        }

        var badMagic = (byte[])bytes.Clone(); badMagic[0] ^= 1;
        Check(!Envelope.TryDecode(badMagic, out _), "reject wrong magic");
        var badVersion = (byte[])bytes.Clone(); badVersion[4] ^= 1;
        Check(!Envelope.TryDecode(badVersion, out _), "reject different protocol version");
        Check(!Envelope.TryDecode(bytes[..12], out _), "reject truncated header");
        Check(!Envelope.TryDecode(new byte[Envelope.MaxPacketBytes + 1], out _), "reject oversized packet");

        var state = new string('x', 20_000);
        var snapshot = Envelope.Encode(PacketKind.WorldState, 3, 4, w => w.WriteLongString(state));
        Check(Envelope.TryDecode(snapshot, out var decoded), "large state packet");
        using (decoded.Reader) Check(decoded.Reader.ReadLongString() == state, "large state roundtrip");
        bool bounded = false;
        try { Envelope.Encode(PacketKind.WorldState, 1, 1, w => w.WriteLongString(new string('z', 41_000))); }
        catch (InvalidDataException) { bounded = true; }
        Check(bounded, "state size limit");

        LocalLinkTests();
    }

    private static (LocalLink Guest, LocalLink Host, TcpListener Listener) Pair()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var guest = LocalLink.Connect(port, out _) ?? throw new Exception("FAIL local connect");
        return (guest, LocalLink.Accept(listener.AcceptSocket()), listener);
    }

    private static List<byte[]> PollUntil(LocalLink from, LocalLink to, int count)
    {
        var got = new List<byte[]>();
        var clock = Stopwatch.StartNew();
        while (got.Count < count && clock.ElapsedMilliseconds < 10_000)
        {
            from.Poll(new List<byte[]>());   // keeps flushing what the socket could not take yet
            to.Poll(got);
        }
        return got;
    }

    private static void LocalLinkTests()
    {
        var (guest, host, listener) = Pair();
        var packets = new List<byte[]> { new byte[] { 1 }, new byte[Envelope.MaxPacketBytes], Array.Empty<byte>() };
        var random = new Random(7);
        foreach (var packet in packets) random.NextBytes(packet);
        // a mid-wash snapshot burst: far more than a socket buffer holds at once
        for (int i = 0; i < 400; i++)
        {
            var chunk = new byte[40 * 1024];
            random.NextBytes(chunk);
            packets.Add(chunk);
        }
        foreach (var packet in packets) guest.Send(packet);
        var got = PollUntil(guest, host, packets.Count);
        bool same = got.Count == packets.Count;
        for (int i = 0; same && i < got.Count; i++) same = got[i].AsSpan().SequenceEqual(packets[i]);
        Check(same, $"local link delivers {packets.Count} packets ({packets.Sum(p => p.Length) / 1024} KB) whole and in order");

        host.Send(Envelope.Encode(PacketKind.Welcome, 0, 1, w => w.Write(42UL)));
        var reply = PollUntil(host, guest, 1);
        Check(reply.Count == 1 && Envelope.TryDecode(reply[0], out var welcome) && welcome.Kind == PacketKind.Welcome,
              "local link carries envelopes both ways");

        guest.Dispose();
        var clock = Stopwatch.StartNew();
        bool open = true;
        while (open && clock.ElapsedMilliseconds < 5000) open = host.Poll(new List<byte[]>());
        Check(!open, "local link notices the other copy closing");
        host.Dispose();

        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var victim = LocalLink.Connect(port, out _)!;
        var raw = listener.AcceptSocket();
        raw.Send(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F });   // claims a 2 GB packet
        clock.Restart();
        open = true;
        while (open && clock.ElapsedMilliseconds < 5000) open = victim.Poll(new List<byte[]>());
        Check(!open, "local link drops a connection that sends an oversized packet");
        raw.Close();
        listener.Stop();
        int freePort = port;
        Check(LocalLink.Connect(freePort, out var error) == null && error.Length > 0,
              "joining with no local host fails with a reason");
    }
}
