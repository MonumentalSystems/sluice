using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NBitcoin.Secp256k1;

namespace Sluice.Nostr;

/// <summary>
/// Minimal nostr crypto for the NWC bridge — BIP-340 schnorr sign + the NIP-01 canonical id and NIP-04
/// encrypt/decrypt (ECDH x-coord + AES-256-CBC, the encryption every NWC client understands). Backed by
/// NBitcoin.Secp256k1.
/// </summary>
public static class NwcCrypto
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static ECPrivKey Priv(string privHex) => ECPrivKey.Create(Convert.FromHexString(privHex));

    /// <summary>The 32-byte x-only public key (nostr pubkey) for a private key, lower-hex.</summary>
    public static string PubKeyHex(string privHex)
    {
        Span<byte> x = stackalloc byte[32];
        Priv(privHex).CreateXOnlyPubKey().WriteToSpan(x);
        return Convert.ToHexString(x).ToLowerInvariant();
    }

    /// <summary>Sign an event in place: compute the NIP-01 id and the BIP-340 schnorr sig over it.</summary>
    public static NostrEvent Sign(string privHex, NostrEvent evt)
    {
        evt.Pubkey = PubKeyHex(privHex);
        var id = ComputeId(evt);
        evt.Id = Convert.ToHexString(id).ToLowerInvariant();
        var sig = Priv(privHex).SignBIP340(id);
        Span<byte> sigBytes = stackalloc byte[64];
        sig.WriteToSpan(sigBytes);
        evt.Sig = Convert.ToHexString(sigBytes).ToLowerInvariant();
        return evt;
    }

    public static byte[] ComputeId(NostrEvent evt) =>
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Canonicalize(evt)));

    /// <summary>Recompute the id and check the BIP-340 schnorr signature (id-binds pubkey/kind/tags/content).</summary>
    public static bool Verify(NostrEvent evt)
    {
        try
        {
            var id = ComputeId(evt);
            if (!Convert.ToHexString(id).Equals(evt.Id, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!ECXOnlyPubKey.TryCreate(Convert.FromHexString(evt.Pubkey), out var xonly) || xonly is null)
                return false;
            if (!SecpSchnorrSignature.TryCreate(Convert.FromHexString(evt.Sig), out var sig) || sig is null)
                return false;
            return xonly.SigVerifyBIP340(sig, id);
        }
        catch
        {
            return false;
        }
    }

    // NIP-01: id = sha256(json([0, pubkey, created_at, kind, tags, content])) with the restricted escaping.
    private static string Canonicalize(NostrEvent evt)
    {
        var sb = new StringBuilder(256);
        sb.Append("[0,\"").Append(evt.Pubkey).Append("\",")
          .Append(evt.CreatedAt).Append(',').Append(evt.Kind).Append(",[");
        for (var i = 0; i < evt.Tags.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append('[');
            var tag = evt.Tags[i];
            for (var j = 0; j < tag.Length; j++)
            {
                if (j > 0)
                    sb.Append(',');
                AppendStr(sb, tag[j]);
            }
            sb.Append(']');
        }
        sb.Append("],");
        AppendStr(sb, evt.Content);
        sb.Append(']');
        return sb.ToString();
    }

    // NIP-01 escaping: only \n \" \\ \r \t \b \f.
    private static void AppendStr(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\n':
                    sb.Append("\\n");
                    break;
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>The raw ECDH shared secret: the 32-byte x-coordinate of ECDH(my priv, their x-only pub),
    /// un-hashed. NIP-44 v2 feeds this into HKDF-extract to derive the conversation key.</summary>
    public static byte[] SharedSecretX(string privHex, string theirPubXonlyHex)
    {
        var theirX = Convert.FromHexString(theirPubXonlyHex);
        Span<byte> compressed = stackalloc byte[33];
        compressed[0] = 0x02; // nostr pubkeys are x-only ⇒ assume even-Y
        theirX.CopyTo(compressed[1..]);
        if (!ECPubKey.TryCreate(compressed, Context.Instance, out _, out var theirPub) || theirPub is null)
            throw new InvalidOperationException("invalid counterparty pubkey");
        var shared = theirPub.GetSharedPubkey(Priv(privHex)); // = priv * theirPub
        Span<byte> sharedComp = stackalloc byte[33];
        shared.WriteToSpan(true, sharedComp, out _); // compressed: parity byte + 32-byte x
        return sharedComp[1..33].ToArray(); // drop the parity byte → 32-byte x
    }
}
