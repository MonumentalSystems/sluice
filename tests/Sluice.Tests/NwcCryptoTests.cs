using Sluice.Nostr;
using NBitcoin.Secp256k1;
using Xunit;

namespace Sluice.Tests;

/// <summary>NWC nostr crypto: deterministic pubkey derivation, NIP-44 v2 round-trip (symmetric ECDH), and a
/// signed event whose id + BIP-340 schnorr signature verify. Pure crypto — fast.</summary>
public sealed class NwcCryptoTests
{
    // secp256k1 generator G — private key 1 ⇒ pubkey x = G.x. A stable, well-known vector.
    private const string Priv1 = "0000000000000000000000000000000000000000000000000000000000000001";
    private const string Pub1 = "79be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
    private const string PrivA = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string PrivB = "2222222222222222222222222222222222222222222222222222222222222222";

    [Fact]
    public void PubKey_derivation_matches_the_known_generator_vector()
    {
        Assert.Equal(Pub1, NwcCrypto.PubKeyHex(Priv1));
    }

    // ── NIP-44 v2 (the authenticated encryption NWC requires — NIP-04 is not shipped) ───────────────
    // Official NIP-44 v2 test vector: sec1=…01, sec2=…02, plaintext "a", nonce …01.
    private const string VecSec1 = "0000000000000000000000000000000000000000000000000000000000000001";
    private const string VecSec2 = "0000000000000000000000000000000000000000000000000000000000000002";
    private const string VecConvKey = "c41c775356fd92eadc63ff5a0dc1da211b268cbea22316767095b2871ea1412d";
    private const string VecNonce = "0000000000000000000000000000000000000000000000000000000000000001";
    private const string VecPayload = "AgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABee0G5VSK0/9YypIObAtDKfYEAjD35uVkHyB0F4DwrcNaCXlCWZKaArsGrY6M9wnuTMxWfp1RTN9Xga8no+kF5Vsb";

    [Fact]
    public void Nip44_conversation_key_matches_the_official_vector()
    {
        var pub2 = NwcCrypto.PubKeyHex(VecSec2);
        var conv = Nip44.ConversationKey(VecSec1, pub2);
        Assert.Equal(VecConvKey, Convert.ToHexString(conv).ToLowerInvariant());
    }

    [Fact]
    public void Nip44_encrypt_matches_the_official_vector_and_round_trips()
    {
        var conv = Convert.FromHexString(VecConvKey);
        var nonce = Convert.FromHexString(VecNonce);
        Assert.Equal(VecPayload, Nip44.Encrypt(conv, nonce, "a"));
        Assert.Equal("a", Nip44.Decrypt(conv, VecPayload));
    }

    [Fact]
    public void Nip44_round_trips_a_realistic_payload_with_a_random_nonce()
    {
        var pubA = NwcCrypto.PubKeyHex(PrivA);
        var pubB = NwcCrypto.PubKeyHex(PrivB);
        const string msg = "{\"result_type\":\"get_balance\",\"result\":{\"balance\":110000}} — ünïcøde ✓";

        // A encrypts to B; B decrypts with its key + A's pubkey (the conversation key is symmetric).
        var cipher = Nip44.Encrypt(PrivA, pubB, msg);
        Assert.Equal(msg, Nip44.Decrypt(PrivB, pubA, cipher));

        // A tampered/garbage payload fails the MAC ⇒ null, not a throw.
        Assert.Null(Nip44.Decrypt(PrivB, pubA, "AgABBADno+t/garbage=="));
    }

    [Theory]
    [InlineData(16, 32)]
    [InlineData(32, 32)]
    [InlineData(33, 64)]
    [InlineData(37, 64)]
    [InlineData(45, 64)]
    [InlineData(49, 64)]
    public void Nip44_padding_matches_the_vectors(int unpadded, int padded)
    {
        Assert.Equal(padded, Nip44.CalcPaddedLen(unpadded));
    }

    [Fact]
    public void Signed_event_has_a_valid_id_and_schnorr_signature()
    {
        var evt = new NostrEvent
        {
            CreatedAt = 1_700_000_000,
            Kind = 23195,
            Tags = new List<string[]> { new[] { "p", NwcCrypto.PubKeyHex(PrivB) }, new[] { "e", "deadbeef" } },
            Content = "encrypted-blob?iv=abc",
        };
        NwcCrypto.Sign(PrivA, evt);

        Assert.Equal(NwcCrypto.PubKeyHex(PrivA), evt.Pubkey);
        Assert.Equal(64, evt.Id.Length); // 32-byte id, hex
        Assert.Equal(128, evt.Sig.Length); // 64-byte schnorr sig, hex

        // The id is exactly sha256 of the canonical serialization…
        var id = Convert.FromHexString(evt.Id);
        Assert.Equal(Convert.ToHexString(NwcCrypto.ComputeId(evt)).ToLowerInvariant(), evt.Id);

        // …and the schnorr signature verifies against the author's x-only pubkey.
        Assert.True(ECXOnlyPubKey.TryCreate(Convert.FromHexString(evt.Pubkey), out var xonly));
        Assert.True(SecpSchnorrSignature.TryCreate(Convert.FromHexString(evt.Sig), out var sig));
        Assert.True(xonly!.SigVerifyBIP340(sig!, id));
    }
}
