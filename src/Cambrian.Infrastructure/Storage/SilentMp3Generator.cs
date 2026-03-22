namespace Cambrian.Infrastructure.Storage;

/// <summary>
/// Generates a minimal valid MPEG-1 Layer 3 (MP3) file containing silence.
/// The output is 38 identical MPEG audio frames (~15 846 bytes, ~1 s of silence)
/// so that media players show a reasonable duration.
/// Used as a placeholder for demo tracks that have no real audio uploaded.
/// </summary>
public static class SilentMp3Generator
{
    /// <summary>
    /// Generate a MemoryStream containing a valid silent MP3 file.
    /// </summary>
    public static MemoryStream Generate()
    {
        // MPEG-1 Layer 3, 128 kbps, 44100 Hz, stereo
        // Frame header: 0xFF 0xFB 0x90 0x00
        //   - Sync word:        0xFFF (12 bits)
        //   - MPEG version 1:   0b11  (2 bits) -> MPEG1
        //   - Layer III:        0b01  (2 bits)
        //   - No CRC:           0b1   (1 bit)
        //   - Bitrate 128k:     0b1001 (4 bits)
        //   - SampleRate 44100: 0b00   (2 bits)
        //   - No padding:       0b0    (1 bit)
        //   - Private:          0b0    (1 bit)
        //   - Stereo:           0b00   (2 bits) -> Joint stereo
        //   - Rest:             0b000000 (6 bits)
        //
        // Frame size = 144 * bitrate / sampleRate + padding
        //            = 144 * 128000 / 44100 + 0 = 417 bytes

        const int frameSize = 417;
        var frame = new byte[frameSize];

        // Frame header
        frame[0] = 0xFF; // Sync
        frame[1] = 0xFB; // MPEG1, Layer III, no CRC
        frame[2] = 0x90; // 128kbps, 44100Hz, no padding
        frame[3] = 0x00; // Joint stereo, no emphasis

        // Side information for stereo MPEG-1 Layer III = 32 bytes
        // Leave as zeros -> signals silence (no Huffman-coded data)
        // Main data area (rest of frame) is also zeros -> silence

        // Repeat the frame several times to create ~1 second of silence
        // so media players show a reasonable duration.
        const int repeatCount = 38; // ~1 second at 44100 Hz
        var ms = new MemoryStream(frameSize * repeatCount);
        for (var i = 0; i < repeatCount; i++)
            ms.Write(frame, 0, frameSize);

        ms.Position = 0;
        return ms;
    }
}
