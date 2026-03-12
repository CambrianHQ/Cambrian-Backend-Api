namespace Cambrian.Api.Tools;

/// <summary>
/// Generates a minimal valid MP3 file containing silence.
/// Used to provide placeholder audio for demo/seeded tracks so the
/// streaming endpoint returns a playable file instead of 404.
/// </summary>
public static class SilentMp3Generator
{
    /// <summary>
    /// Builds ~3 seconds of silent MPEG-1 Layer 3 audio (128 kbps, 44100 Hz, stereo).
    /// Each frame: 4-byte header + 413 zero bytes = 417 bytes per frame.
    /// At 44100 Hz / 1152 samples-per-frame ≈ 38.28 frames/sec → ~115 frames ≈ 3 s.
    /// </summary>
    public static byte[] Generate(int durationSeconds = 3)
    {
        // MPEG-1, Layer 3, 128 kbps, 44100 Hz, stereo, no padding
        // Sync: 0xFFF  |  Version: MPEG1 (11)  |  Layer: III (01)  |  No CRC (1)
        // Bitrate: 128 kbps index=9 (1001)  |  SampleRate: 44100 index=0 (00)
        // Padding: 0  |  Private: 0  |  ChannelMode: Stereo (00)  |  ...
        // Byte layout: FF FB 90 00
        byte[] frameHeader = { 0xFF, 0xFB, 0x90, 0x00 };

        // Frame size = 144 * bitrate / sampleRate + padding
        // = 144 * 128000 / 44100 + 0 = 417 bytes (truncated)
        const int frameSize = 417;
        const double framesPerSecond = 44100.0 / 1152.0; // ≈ 38.28
        int totalFrames = (int)(framesPerSecond * durationSeconds);

        using var ms = new MemoryStream(totalFrames * frameSize);
        var silentBody = new byte[frameSize - frameHeader.Length]; // zeros = silence

        for (int i = 0; i < totalFrames; i++)
        {
            ms.Write(frameHeader, 0, frameHeader.Length);
            ms.Write(silentBody, 0, silentBody.Length);
        }

        return ms.ToArray();
    }
}
