using Sluice.Nostr;
using Xunit;

namespace Sluice.Tests;

/// <summary>Extra NWC crypto coverage: Verify's reject branches (bad id, bad pubkey, bad sig) and the
/// SharedSecretX invalid-counterparty throw. Fast.</summary>
public sealed class NwcCryptoMoreTests
{
    private const string PrivA = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string PrivB = "2222222222222222222222222222222222222222222222222222222222222222";

    private static NostrEvent Signed() => NwcCrypto.Sign(PrivA, new NostrEvent
    {
        CreatedAt = 1_700_000_000,
        Kind = 23195,
        Tags = new List<string[]> { new[] { "p", "x" } },
        Content = "payload",
    });

    [Fact]
    public void Verify_true_for_a_freshly_signed_event()
    {
        Assert.True(NwcCrypto.Verify(Signed()));
    }

    [Fact]
    public void Verify_false_when_content_is_tampered()
    {
        var evt = Signed();
        evt.Content = "tampered"; // id no longer matches
        Assert.False(NwcCrypto.Verify(evt));
    }

    [Fact]
    public void Verify_false_when_id_does_not_match()
    {
        var evt = Signed();
        evt.Id = new string('0', 64);
        Assert.False(NwcCrypto.Verify(evt));
    }

    [Fact]
    public void Verify_false_on_garbage_pubkey_or_sig()
    {
        var evt = Signed();
        evt.Pubkey = "not-hex"; // Convert.FromHexString throws ⇒ caught ⇒ false
        Assert.False(NwcCrypto.Verify(evt));
    }

    [Fact]
    public void Verify_false_when_sig_is_invalid_for_the_event()
    {
        var a = Signed();
        var b = NwcCrypto.Sign(PrivB, new NostrEvent { CreatedAt = 1, Kind = 1, Content = "other" });
        a.Sig = b.Sig; // valid-form sig, wrong event ⇒ verify fails
        Assert.False(NwcCrypto.Verify(a));
    }

    [Fact]
    public void SharedSecretX_is_symmetric()
    {
        var pubA = NwcCrypto.PubKeyHex(PrivA);
        var pubB = NwcCrypto.PubKeyHex(PrivB);
        var ab = NwcCrypto.SharedSecretX(PrivA, pubB);
        var ba = NwcCrypto.SharedSecretX(PrivB, pubA);
        Assert.Equal(ab, ba);
        Assert.Equal(32, ab.Length);
    }

    [Fact]
    public void SharedSecretX_throws_on_an_invalid_counterparty_pubkey()
    {
        // All-zero x is not a valid curve point ⇒ ECPubKey.TryCreate fails ⇒ throws.
        Assert.Throws<InvalidOperationException>(() => NwcCrypto.SharedSecretX(PrivA, new string('0', 64)));
    }

    [Fact]
    public void Sign_canonicalizes_every_nip01_escape_char()
    {
        // Content + a tag value exercising every escape branch in Canonicalize/AppendStr.
        var evt = NwcCrypto.Sign(PrivA, new NostrEvent
        {
            CreatedAt = 1_700_000_000,
            Kind = 1,
            Tags = new List<string[]> { new[] { "x", "tab\tcr\rbs\\quote\"" } },
            Content = "line1\nline2\ttab\rcr\bback\fff\\slash\"quote",
        });
        // It produced a verifiable id+sig over the escaped canonical form.
        Assert.True(NwcCrypto.Verify(evt));
        Assert.Equal(64, evt.Id.Length);
    }
}
