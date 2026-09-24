using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace DragNWashCoop;

/// <summary>
/// Length-prefixed TCP connection on 127.0.0.1 for the local test mode, so two copies of the game
/// on one PC can connect without Steam. Non-blocking; polled from Unity's main thread.
/// </summary>
internal sealed class LocalLink : IDisposable
{
    private const int HeaderBytes = 4;
    private readonly Socket socket;
    private readonly byte[] receiveBuffer = new byte[HeaderBytes + Envelope.MaxPacketBytes];
    private int received;
    private readonly Queue<byte[]> outgoing = new();
    private byte[]? sending;
    private int sendOffset;
    private int queuedBytes;
    private const int DropBacklog = 512 * 1024;   // beyond this, stale poses are not worth queueing

    internal bool IsOpen { get; private set; } = true;

    private LocalLink(Socket socket)
    {
        this.socket = socket;
        socket.Blocking = false;
        socket.NoDelay = true;
    }

    internal static LocalLink Accept(Socket socket) => new(socket);

    /// <summary>A loopback connect is refused or accepted immediately, so this does not stall a frame.</summary>
    internal static LocalLink? Connect(int port, out string error)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Connect(IPAddress.Loopback, port);
            error = string.Empty;
            return new LocalLink(socket);
        }
        catch (SocketException e)
        {
            socket.Close();
            error = e.SocketErrorCode.ToString();
            return null;
        }
    }

    /// <param name="droppable">Skipped while the other copy is not keeping up (poses: a newer one follows).</param>
    internal void Send(byte[] payload, bool droppable = false)
    {
        if (!IsOpen || (droppable && queuedBytes > DropBacklog)) return;
        if (payload.Length > Envelope.MaxPacketBytes) throw new ArgumentException("Local test packet too large");
        var frame = new byte[HeaderBytes + payload.Length];
        for (int i = 0; i < HeaderBytes; i++) frame[i] = (byte)(payload.Length >> (8 * i));
        Buffer.BlockCopy(payload, 0, frame, HeaderBytes, payload.Length);
        outgoing.Enqueue(frame);
        queuedBytes += frame.Length;
        Flush();
    }

    /// <summary>Writes what the socket takes now; the rest waits for the next poll.</summary>
    private void Flush()
    {
        while (IsOpen)
        {
            if (sending == null)
            {
                if (outgoing.Count == 0) return;
                sending = outgoing.Dequeue();
                queuedBytes -= sending.Length;
                sendOffset = 0;
            }
            int sent = socket.Send(sending, sendOffset, sending.Length - sendOffset, SocketFlags.None, out var error);
            if (error == SocketError.WouldBlock) return;
            if (error != SocketError.Success) { Close(); return; }
            sendOffset += sent;
            if (sendOffset == sending.Length) sending = null;
        }
    }

    /// <summary>Adds each complete packet to <paramref name="packets"/>. Returns false once the link has closed.</summary>
    internal bool Poll(List<byte[]> packets)
    {
        Flush();
        while (IsOpen)
        {
            // a frame never exceeds the buffer, so after compaction there is always room to read
            int count = socket.Receive(receiveBuffer, received, receiveBuffer.Length - received, SocketFlags.None, out var error);
            if (error == SocketError.WouldBlock) break;
            if (error != SocketError.Success || count == 0) { Close(); break; }
            received += count;
            int start = 0;
            while (received - start >= HeaderBytes)
            {
                int length = 0;
                for (int i = 0; i < HeaderBytes; i++) length |= receiveBuffer[start + i] << (8 * i);
                if (length < 0 || length > Envelope.MaxPacketBytes) { Close(); return false; }
                if (received - start < HeaderBytes + length) break;
                var packet = new byte[length];
                Buffer.BlockCopy(receiveBuffer, start + HeaderBytes, packet, 0, length);
                packets.Add(packet);
                start += HeaderBytes + length;
            }
            if (start > 0)
            {
                Buffer.BlockCopy(receiveBuffer, start, receiveBuffer, 0, received - start);
                received -= start;
            }
        }
        return IsOpen;
    }

    private void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        outgoing.Clear();
        queuedBytes = 0;
        sending = null;
        socket.Close();
    }

    public void Dispose() => Close();
}
