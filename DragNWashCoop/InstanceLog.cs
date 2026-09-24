using System;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;

namespace DragNWashCoop;

/// <summary>
/// Copies every BepInEx log line into this copy's own file. Copies of the game started for a local
/// test all write the same LogOutput.log, which mixes their lines.
/// </summary>
internal sealed class InstanceLog : ILogListener
{
    private readonly StreamWriter writer;
    private bool closed;

    internal string FilePath { get; }

    private InstanceLog(string path)
    {
        FilePath = path;
        writer = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
    }

    internal static InstanceLog? Start(string name)
    {
        try
        {
            var log = new InstanceLog(Path.Combine(Paths.BepInExRootPath, $"LogOutput.local-{name}.log"));
            BepInEx.Logging.Logger.Listeners.Add(log);
            return log;
        }
        catch (Exception) { return null; }
    }

    public void LogEvent(object sender, LogEventArgs eventArgs)
    {
        lock (writer)
            if (!closed) writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {eventArgs}");
    }

    public void Dispose()
    {
        BepInEx.Logging.Logger.Listeners.Remove(this);
        lock (writer)
        {
            closed = true;
            writer.Dispose();
        }
    }
}
