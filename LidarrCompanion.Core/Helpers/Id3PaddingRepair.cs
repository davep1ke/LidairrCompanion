namespace LidarrCompanion.Helpers
{
    // Some MP3s have a long run of zero bytes between the end of the ID3v2 tag and the first MPEG
    // frame. Players cope, but TagLib only searches a short distance past the tag for audio and
    // throws "MPEG audio header not found", so such a file can't be opened or tagged at all.
    //
    // ID3v2 allows padding *inside* the tag, so the fix is to grow the tag's size field to cover
    // those zero bytes - a 4-byte in-place edit, the audio and the tag contents aren't touched.
    public static class Id3PaddingRepair
    {
        private const int MaxPaddingScan = 4 * 1024 * 1024;

        // Returns true only if the stream was changed. Anything unexpected (no ID3v2 header, a
        // footer, no padding, no MPEG sync after the padding, size overflow) leaves it alone.
        public static bool TryExtendTagOverPadding(Stream stream)
        {
            if (!stream.CanRead || !stream.CanWrite || !stream.CanSeek) return false;

            stream.Seek(0, SeekOrigin.Begin);
            var header = new byte[10];
            if (ReadFully(stream, header) < 10) return false;

            if (header[0] != 'I' || header[1] != 'D' || header[2] != '3') return false;
            if (header[3] != 3 && header[3] != 4) return false;      // ID3v2.3 / v2.4 only
            if ((header[5] & 0x10) != 0) return false;               // footer present: size semantics differ
            if (((header[6] | header[7] | header[8] | header[9]) & 0x80) != 0) return false; // not synchsafe

            long tagSize = (header[6] << 21) | (header[7] << 14) | (header[8] << 7) | header[9];
            long audioStart = 10 + tagSize;
            if (audioStart >= stream.Length) return false;

            stream.Seek(audioStart, SeekOrigin.Begin);
            long zeros = 0;
            int next = -1;
            var buffer = new byte[8192];
            while (zeros < MaxPaddingScan && next < 0)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) return false;

                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] == 0) { zeros++; continue; }
                    next = buffer[i];
                    if (i + 1 < read) { if (!IsMpegSync(buffer[i], buffer[i + 1])) return false; }
                    else
                    {
                        var following = stream.ReadByte();
                        if (following < 0 || !IsMpegSync((byte)next, (byte)following)) return false;
                    }
                    break;
                }
            }

            if (zeros == 0 || next < 0) return false;

            var newSize = tagSize + zeros;
            if (newSize >= (1L << 28)) return false;

            stream.Seek(6, SeekOrigin.Begin);
            stream.WriteByte((byte)((newSize >> 21) & 0x7F));
            stream.WriteByte((byte)((newSize >> 14) & 0x7F));
            stream.WriteByte((byte)((newSize >> 7) & 0x7F));
            stream.WriteByte((byte)(newSize & 0x7F));
            stream.Flush();
            return true;
        }

        private static bool IsMpegSync(byte first, byte second) => first == 0xFF && (second & 0xE0) == 0xE0;

        private static int ReadFully(Stream stream, byte[] buffer)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = stream.Read(buffer, total, buffer.Length - total);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }
    }
}
