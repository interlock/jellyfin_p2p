using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.P2PShare.Core.Sharing;

/// <summary>
/// Builds ffmpeg arguments for a peer transcode: H.264 + AAC in MPEG-TS on stdout.
/// </summary>
public static class TranscodeArguments
{
    /// <summary>Audio bitrate reserved from the target (bits/s).</summary>
    public const long AudioBitrate = 192_000;

    /// <param name="inputPath">Media file path.</param>
    /// <param name="targetBitrate">Total target bitrate in bits/s.</param>
    /// <param name="startTimeTicks">Start position in 100 ns ticks, or 0.</param>
    /// <param name="isAudioOnly">True for audio items (encodes AAC only into ADTS).</param>
    public static IReadOnlyList<string> Build(string inputPath, long targetBitrate, long startTimeTicks, bool isAudioOnly)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };

        if (startTimeTicks > 0)
        {
            args.Add("-ss");
            args.Add(TimeSpan.FromTicks(startTimeTicks).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }

        args.Add("-i");
        args.Add(inputPath);

        if (isAudioOnly)
        {
            var audio = Math.Clamp(targetBitrate, 64_000, 320_000);
            args.AddRange(new[] { "-map", "0:a:0", "-vn", "-c:a", "aac", "-b:a", Kbps(audio), "-f", "adts", "pipe:1" });
            return args;
        }

        var video = Math.Max(300_000, targetBitrate - AudioBitrate);
        args.AddRange(new[]
        {
            "-map", "0:v:0", "-map", "0:a:0?", "-sn",
            "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "high", "-pix_fmt", "yuv420p",
            "-b:v", Kbps(video), "-maxrate", Kbps(video), "-bufsize", Kbps(video * 2),
            "-c:a", "aac", "-ac", "2", "-b:a", Kbps(AudioBitrate),
            "-f", "mpegts", "pipe:1",
        });
        return args;
    }

    private static string Kbps(long bitsPerSecond)
        => (bitsPerSecond / 1000).ToString(CultureInfo.InvariantCulture) + "k";
}
