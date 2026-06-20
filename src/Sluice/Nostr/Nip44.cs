using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Sluice.Nostr;

/// <summary>
/// NIP-44 v2 encryption (the authenticated scheme NWC clients require): conversation key =
/// HKDF-Extract(salt="nip44-v2", IKM=ecdh_x); per-message keys = HKDF-Expand(conv_key, info=nonce, 76) →
/// chacha key(32)+nonce(12)+hmac key(32); ciphertext = ChaCha20 over the length-prefixed, padded
/// plaintext; mac = HMAC-SHA256(hmac_key, nonce || ciphertext); payload = base64(0x02 || nonce(32) ||
/// ciphertext || mac(32)). Validated against the official NIP-44 v2 test vectors.
/// </summary>
public static class Nip44
{
    private static readonly byte[] Salt = Encoding.ASCII.GetBytes("nip44-v2");

    /// <summary>conversation_key = HKDF-Extract(SHA256, salt="nip44-v2", IKM = ECDH shared x).</summary>
    public static byte[] ConversationKey(string privHex, string theirPubHex) =>
        HKDF.Extract(HashAlgorithmName.SHA256, NwcCrypto.SharedSecretX(privHex, theirPubHex), Salt);

    public static string Encrypt(string privHex, string theirPubHex, string plaintext) =>
        Encrypt(ConversationKey(privHex, theirPubHex), RandomNumberGenerator.GetBytes(32), plaintext);

    public static string? Decrypt(string privHex, string theirPubHex, string payload) =>
        Decrypt(ConversationKey(privHex, theirPubHex), payload);

    // ── testable cores (fixed conversation key + nonce) ─────────────────────────────────────────────
    public static string Encrypt(byte[] convKey, byte[] nonce, string plaintext)
    {
        var (chachaKey, chachaNonce, hmacKey) = MessageKeys(convKey, nonce);
        var padded = Pad(Encoding.UTF8.GetBytes(plaintext));
        var ct = ChaCha20.Apply(chachaKey, chachaNonce, padded);
        var mac = Mac(hmacKey, nonce, ct);

        var payload = new byte[1 + 32 + ct.Length + 32];
        payload[0] = 0x02;
        nonce.CopyTo(payload, 1);
        ct.CopyTo(payload, 33);
        mac.CopyTo(payload, 33 + ct.Length);
        return Convert.ToBase64String(payload);
    }

    public static string? Decrypt(byte[] convKey, string payloadB64)
    {
        try
        {
            var payload = Convert.FromBase64String(payloadB64);
            if (payload.Length < 1 + 32 + 32 + 2 || payload[0] != 0x02)
                return null;
            var nonce = payload[1..33];
            var ct = payload[33..^32];
            var mac = payload[^32..];
            var (chachaKey, chachaNonce, hmacKey) = MessageKeys(convKey, nonce);
            if (!CryptographicOperations.FixedTimeEquals(mac, Mac(hmacKey, nonce, ct)))
                return null;
            var padded = ChaCha20.Apply(chachaKey, chachaNonce, ct);
            var len = BinaryPrimitives.ReadUInt16BigEndian(padded);
            if (len < 1 || 2 + len > padded.Length)
                return null;
            return Encoding.UTF8.GetString(padded, 2, len);
        }
        catch
        {
            return null;
        }
    }

    private static (byte[] chacha, byte[] nonce, byte[] hmac) MessageKeys(byte[] convKey, byte[] nonce)
    {
        var okm = HKDF.Expand(HashAlgorithmName.SHA256, convKey, 76, nonce);
        return (okm[0..32], okm[32..44], okm[44..76]);
    }

    private static byte[] Mac(byte[] key, byte[] aadNonce, byte[] ciphertext)
    {
        var buf = new byte[aadNonce.Length + ciphertext.Length];
        aadNonce.CopyTo(buf, 0);
        ciphertext.CopyTo(buf, aadNonce.Length);
        return HMACSHA256.HashData(key, buf);
    }

    // NIP-44 padding: [2-byte BE unpadded length] || plaintext || zeros, total content = calc_padded_len.
    private static byte[] Pad(byte[] plaintext)
    {
        if (plaintext.Length < 1 || plaintext.Length > 65535)
            throw new ArgumentException("plaintext length out of range");
        var buf = new byte[2 + CalcPaddedLen(plaintext.Length)];
        BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)plaintext.Length);
        plaintext.CopyTo(buf, 2);
        return buf;
    }

    public static int CalcPaddedLen(int unpadded)
    {
        if (unpadded <= 32)
            return 32;
        var nextPower = 1 << ((int)Math.Floor(Math.Log2(unpadded - 1)) + 1);
        var chunk = nextPower <= 256 ? 32 : nextPower / 8;
        return chunk * ((int)Math.Floor((double)(unpadded - 1) / chunk) + 1);
    }
}

/// <summary>RFC 8439 ChaCha20 stream cipher (counter starts at 0) — the cipher NIP-44 v2 uses (bare, with a
/// separate HMAC, not ChaCha20-Poly1305). Apply = XOR with the keystream, so encrypt and decrypt are one op.</summary>
public static class ChaCha20
{
    public static byte[] Apply(byte[] key, byte[] nonce, byte[] data)
    {
        Span<uint> state = stackalloc uint[16];
        state[0] = 0x61707865;
        state[1] = 0x3320646e;
        state[2] = 0x79622d32;
        state[3] = 0x6b206574;
        for (var i = 0; i < 8; i++)
            state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(i * 4));
        state[12] = 0; // counter
        state[13] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.AsSpan(0));
        state[14] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.AsSpan(4));
        state[15] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.AsSpan(8));

        var output = new byte[data.Length];
        Span<uint> work = stackalloc uint[16];
        Span<byte> block = stackalloc byte[64];
        var offset = 0;
        while (offset < data.Length)
        {
            state.CopyTo(work);
            for (var i = 0; i < 10; i++) // 10 double-rounds = 20 rounds
            {
                QuarterRound(work, 0, 4, 8, 12);
                QuarterRound(work, 1, 5, 9, 13);
                QuarterRound(work, 2, 6, 10, 14);
                QuarterRound(work, 3, 7, 11, 15);
                QuarterRound(work, 0, 5, 10, 15);
                QuarterRound(work, 1, 6, 11, 12);
                QuarterRound(work, 2, 7, 8, 13);
                QuarterRound(work, 3, 4, 9, 14);
            }
            for (var i = 0; i < 16; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(block[(i * 4)..], work[i] + state[i]);
            var n = Math.Min(64, data.Length - offset);
            for (var i = 0; i < n; i++)
                output[offset + i] = (byte)(data[offset + i] ^ block[i]);
            offset += 64;
            state[12]++;
        }
        return output;
    }

    private static void QuarterRound(Span<uint> s, int a, int b, int c, int d)
    {
        s[a] += s[b];
        s[d] = Rotl(s[d] ^ s[a], 16);
        s[c] += s[d];
        s[b] = Rotl(s[b] ^ s[c], 12);
        s[a] += s[b];
        s[d] = Rotl(s[d] ^ s[a], 8);
        s[c] += s[d];
        s[b] = Rotl(s[b] ^ s[c], 7);
    }

    private static uint Rotl(uint x, int n) => (x << n) | (x >> (32 - n));
}
