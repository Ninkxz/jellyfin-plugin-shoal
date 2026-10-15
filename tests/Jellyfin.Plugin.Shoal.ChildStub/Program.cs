using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Jellyfin.Plugin.Shoal.ChildStub;

/// <summary>
/// A stand-in for ffmpeg. Modes (first argument):
/// <c>jpeg W H</c> reads stdin, writes a JPEG header of that size; <c>text</c> writes HTML; <c>sleep</c> never answers;
/// <c>flood</c> writes 10 MB; <c>exit1</c> fails; <c>spawn PIDFILE</c> starts a grandchild and sleeps; <c>pid PIDFILE</c> writes its pid then sleeps;
/// <c>probe OUTFILE</c> writes the environment names and the working directory's entry count to OUTFILE and answers a JPEG;
/// <c>log LOGFILE MS</c> logs start and end times around a pause of MS and answers a JPEG;
/// <c>truncated</c> writes the first bytes of a JPEG only.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "jpeg";
        switch (mode)
        {
            case "jpeg":
                Drain();
                WriteJpeg(args.Length > 2 ? int.Parse(args[1]) : 32, args.Length > 2 ? int.Parse(args[2]) : 32);
                return 0;
            case "text":
                Drain();
                Out("<html><script>alert(1)</script></html>"u8.ToArray());
                return 0;
            case "truncated":
                Drain();
                Out([0xFF, 0xD8, 0xFF, 0xE0, 0x00]);
                return 0;
            case "sleep":
                Thread.Sleep(TimeSpan.FromMinutes(5));
                return 0;
            case "flood":
                using (var stdout = Console.OpenStandardOutput())
                {
                    var chunk = new byte[64 * 1024];
                    for (var i = 0; i < 160; i++)
                    {
                        stdout.Write(chunk);
                    }
                }

                return 0;
            case "exit1":
                return 1;
            case "spawn":
                {
                    var grandchild = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, [Path.Combine(AppContext.BaseDirectory, "Jellyfin.Plugin.Shoal.ChildStub.dll"), "sleep"]) { UseShellExecute = false });
                    File.WriteAllText(args[1], grandchild!.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Thread.Sleep(TimeSpan.FromMinutes(5));
                    return 0;
                }

            case "pid":
                File.WriteAllText(args[1], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                Thread.Sleep(TimeSpan.FromMinutes(5));
                return 0;
            case "probe":
                {
                    var keys = new StringBuilder();
                    foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
                    {
                        keys.Append(entry.Key).Append(',');
                    }

                    var entries = Directory.GetFileSystemEntries(Directory.GetCurrentDirectory()).Length;
                    File.WriteAllText(args[1], keys + "|" + entries.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + Directory.GetCurrentDirectory());
                    Drain();
                    WriteJpeg(32, 32);
                    return 0;
                }

            case "log":
                {
                    File.AppendAllText(args[1], "start " + DateTime.UtcNow.Ticks + "\n");
                    Drain();
                    Thread.Sleep(int.Parse(args[2]));
                    File.AppendAllText(args[1], "end " + DateTime.UtcNow.Ticks + "\n");
                    WriteJpeg(32, 32);
                    return 0;
                }

            default:
                return 2;
        }
    }

    private static void Drain()
    {
        using var stdin = Console.OpenStandardInput();
        var buffer = new byte[64 * 1024];
        while (stdin.Read(buffer) > 0)
        {
        }
    }

    private static void Out(byte[] bytes)
    {
        using var stdout = Console.OpenStandardOutput();
        stdout.Write(bytes);
        stdout.Flush();
    }

    private static void WriteJpeg(int width, int height)
    {
        var jpeg = new byte[]
        {
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0,
            0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)(height & 0xFF), (byte)(width >> 8), (byte)(width & 0xFF), 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1,
            0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00,
            0x55, 0x55, 0x55, 0x55,
            0xFF, 0xD9,
        };
        Out(jpeg);
    }
}
