using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

// Experimental browser adapter. Unreal CTR is a 12-byte IV followed by a
// big-endian 32-bit counter starting at zero per compression block/index.
// Uses BouncyCastle's existing CTR implementation; never routes CTR through ECB.
internal static class BrowserAesCtr
{
    public static byte[] Transform(byte[] bytes, byte[] key, byte[] iv, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(iv);
        if (key.Length != 32 || iv.Length != 12 || bytes.Length > 4 * 1024 * 1024)
            throw new ArgumentException("Unreal CTR requires key=32, IV=12 and at most 4 MiB");
        cancellationToken.ThrowIfCancellationRequested();
        var counter = new byte[16];
        iv.CopyTo(counter, 0);
        var cipher = new BufferedBlockCipher(new SicBlockCipher(new AesEngine()));
        cipher.Init(false, new ParametersWithIV(new KeyParameter(key), counter));
        var output = new byte[cipher.GetOutputSize(bytes.Length)];
        try
        {
            var written = 0;
            for (var offset = 0; offset < bytes.Length; offset += 4096)
            {
                cancellationToken.ThrowIfCancellationRequested();
                written += cipher.ProcessBytes(bytes, offset, Math.Min(4096, bytes.Length - offset), output, written);
            }
            written += cipher.DoFinal(output, written);
            cancellationToken.ThrowIfCancellationRequested();
            if (written != bytes.Length) throw new InvalidDataException("CTR changed the byte count");
            return output;
        }
        catch { Array.Clear(output); throw; }
    }
}
