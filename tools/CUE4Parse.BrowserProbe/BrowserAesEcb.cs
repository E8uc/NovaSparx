using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

// Browser compatibility experiment, not a registered asset parser. Existing
// BouncyCastle implementation avoids .NET's unsupported browser AES provider.
// ECB ONLY: never install this blindly for IoStore AES-CTR containers.
internal static class BrowserAesEcb
{
    public static byte[] Decrypt(byte[] bytes, int offset, int count, byte[] key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32) throw new ArgumentException("AES-256 requires 32 key bytes");
        if (offset < 0 || count < 0 || offset > bytes.Length - count || count % 16 != 0 || count > 4 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(count), "Require complete ECB blocks within the 4 MiB budget");
        cancellationToken.ThrowIfCancellationRequested();
        var output = new byte[count];
        var engine = new AesEngine();
        engine.Init(false, new KeyParameter(key));
        try
        {
            for (var i = 0; i < count; i += 16)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                engine.ProcessBlock(bytes, offset + i, output, i);
            }
            return output;
        }
        catch { Array.Clear(output); throw; }
    }
}
