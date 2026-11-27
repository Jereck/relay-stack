using System.Security.Cryptography;
using RelayStack.Platform.Deliveries;
using RelayStack.Platform.Security;
using RelayStack.Platform.Storage;

namespace RelayStack.UnitTests;

public class SecurityPrimitivesTests
{
    private static readonly string Pepper = Convert.ToBase64String(new byte[32]);

    [Fact]
    public void Api_keys_round_trip_and_only_the_hash_verifies()
    {
        var hasher = new ApiKeyHasher(Pepper);
        var key = hasher.Generate("development");
        Assert.StartsWith("rs_dev_", key.FullKey);
        var parsed = ApiKeyHasher.Parse(key.FullKey)!;
        Assert.Equal(key.Prefix, parsed.Prefix);
        Assert.True(hasher.Verify(parsed.Secret, key.SecretHash));
        Assert.False(hasher.Verify(parsed.Secret[..^1] + (parsed.Secret[^1] == 'A' ? 'B' : 'A'), key.SecretHash));
        Assert.DoesNotContain(parsed.Secret, Convert.ToBase64String(key.SecretHash));
    }

    [Fact]
    public void Api_key_hash_depends_on_the_pepper()
    {
        var a = new ApiKeyHasher(Pepper);
        var b = new ApiKeyHasher(Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()));
        var key = a.Generate("production");
        Assert.False(b.Verify(ApiKeyHasher.Parse(key.FullKey)!.Secret, key.SecretHash));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rs_dev_short_secret")]
    [InlineData("sk_live_abcdefghijkl_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("rs_test_abcdefghijkl_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("rs_dev_ABCDEFGHIJKL_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("rs_dev_abcdefghijkl_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA_")]
    public void Rejects_malformed_api_keys(string? value) => Assert.Null(ApiKeyHasher.Parse(value));

    [Fact]
    public void Short_pepper_is_rejected_at_startup() =>
        Assert.Throws<InvalidOperationException>(() => new ApiKeyHasher(Convert.ToBase64String(new byte[16])));

    [Fact]
    public void Secret_protector_binds_ciphertext_to_tenant_and_purpose()
    {
        var keys = new Dictionary<string, string> { ["k1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
        var p = new SecretProtector(keys, "k1");
        var orgA = Guid.NewGuid();
        var ct = p.Protect("whsec_secret", SecretPurposes.WebhookSigningSecret, orgA);
        Assert.Equal("whsec_secret", p.Unprotect(ct, SecretPurposes.WebhookSigningSecret, orgA));
        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect(ct, SecretPurposes.WebhookSigningSecret, Guid.NewGuid()));
        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect(ct, "other-purpose", orgA));
        ct[^1] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => p.Unprotect(ct, SecretPurposes.WebhookSigningSecret, orgA));
    }

    [Fact]
    public void Secret_protector_decrypts_with_previous_keys_after_rotation()
    {
        var k1 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var k2 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var org = Guid.NewGuid();
        var old = new SecretProtector(new Dictionary<string, string> { ["1"] = k1 }, "1").Protect("s", "p", org);
        var rotated = new SecretProtector(new Dictionary<string, string> { ["1"] = k1, ["2"] = k2 }, "2");
        Assert.Equal("s", rotated.Unprotect(old, "p", org));
    }

    [Fact]
    public void Webhook_signature_matches_standard_webhooks_reference_vector()
    {
        // Test vector from the Standard Webhooks reference implementations.
        const string secret = "whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw";
        const string id = "msg_p5jXN8AQM9LWM0D4loKWxJek";
        const long ts = 1614265330;
        const string body = """{"test": 2432232314}""";
        Assert.Equal("v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=", WebhookSigner.Sign(secret, id, ts, body));
    }

    [Fact]
    public void Webhook_verification_enforces_signature_and_timestamp_tolerance()
    {
        var secret = WebhookSigner.GenerateSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var sig = WebhookSigner.Sign(secret, "evt_1", now.ToUnixTimeSeconds(), "{}");
        Assert.True(WebhookSigner.Verify(secret, "evt_1", "1700000000", sig, "{}", now, TimeSpan.FromMinutes(5)));
        Assert.True(WebhookSigner.Verify(secret, "evt_1", "1700000000", "v1,bogus " + sig, "{}", now, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSigner.Verify(secret, "evt_1", "1700000000", sig, "{ }", now, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSigner.Verify(secret, "evt_2", "1700000000", sig, "{}", now, TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSigner.Verify(secret, "evt_1", "1700000000", sig, "{}", now.AddMinutes(6), TimeSpan.FromMinutes(5)));
        Assert.False(WebhookSigner.Verify(WebhookSigner.GenerateSecret(), "evt_1", "1700000000", sig, "{}", now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Redaction_masks_keys_secrets_and_url_tokens()
    {
        var key = new ApiKeyHasher(Pepper).Generate("development").FullKey;
        var text = Redaction.Text($"failed with {key} and whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw");
        Assert.DoesNotContain(key, text);
        Assert.DoesNotContain("MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw", text);
        Assert.Equal("https://hooks.example.com/in?[redacted]", Redaction.Url("https://u:p@hooks.example.com/in?token=abc"));
        Assert.Equal("https://hooks.example.com/in", Redaction.Url("https://hooks.example.com/in"));
    }

    [Fact]
    public void Retry_policy_classifies_status_codes()
    {
        Assert.Equal(AttemptClassification.Success, RetryPolicy.Classify(System.Net.HttpStatusCode.NoContent));
        Assert.Equal(AttemptClassification.Retryable, RetryPolicy.Classify(System.Net.HttpStatusCode.TooManyRequests));
        Assert.Equal(AttemptClassification.Retryable, RetryPolicy.Classify(System.Net.HttpStatusCode.RequestTimeout));
        Assert.Equal(AttemptClassification.Retryable, RetryPolicy.Classify(System.Net.HttpStatusCode.BadGateway));
        Assert.Equal(AttemptClassification.Permanent, RetryPolicy.Classify(System.Net.HttpStatusCode.BadRequest));
        Assert.Equal(AttemptClassification.Permanent, RetryPolicy.Classify(System.Net.HttpStatusCode.Gone));
        Assert.Equal(AttemptClassification.Permanent, RetryPolicy.Classify(System.Net.HttpStatusCode.Redirect));
    }

    [Fact]
    public void Retry_delays_grow_exponentially_with_bounded_jitter_and_cap()
    {
        var p = new RetryPolicy(baseDelaySeconds: 5, maxDelaySeconds: 3600);
        Assert.Equal(TimeSpan.FromSeconds(2.5), p.NextDelay(2, 0));
        Assert.Equal(TimeSpan.FromSeconds(5), p.NextDelay(2, 1));
        Assert.Equal(TimeSpan.FromSeconds(10), p.NextDelay(4, 0)); // third retry: ceiling 20 s, half fixed
        Assert.Equal(TimeSpan.FromSeconds(20), p.NextDelay(4, 1));
        Assert.Equal(TimeSpan.FromSeconds(1800), p.NextDelay(30, 0));
        Assert.Equal(TimeSpan.FromSeconds(3600), p.NextDelay(30, 1));
        Assert.Equal(TimeSpan.FromSeconds(120), p.NextDelay(2, 0, retryAfter: TimeSpan.FromSeconds(120)));
        Assert.Equal(TimeSpan.FromSeconds(3600), p.NextDelay(2, 0, retryAfter: TimeSpan.FromDays(1)));
    }

    [Fact]
    public void Object_keys_cannot_cross_tenants_or_traverse()
    {
        var orgA = Guid.NewGuid();
        var key = TenantObjectKey.ForUpload(orgA, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(key, TenantObjectKey.Parse(orgA, key.Value));
        Assert.Throws<UnauthorizedAccessException>(() => TenantObjectKey.Parse(Guid.NewGuid(), key.Value));
        Assert.Throws<UnauthorizedAccessException>(() => TenantObjectKey.Parse(orgA, $"org/{orgA:N}/../other/file"));
    }

    [Fact]
    public async Task Filesystem_store_enforces_size_limit_and_leaves_no_partial_object()
    {
        var root = Path.Combine(Path.GetTempPath(), "rs-store-" + Guid.NewGuid().ToString("N"));
        var store = new FileSystemObjectStore(root);
        var key = TenantObjectKey.ForUpload(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await Assert.ThrowsAsync<ObjectTooLargeException>(() => store.PutAsync(key, new MemoryStream(new byte[1001]), 1000, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));

        var stored = await store.PutAsync(key, new MemoryStream("hello"u8.ToArray()), 1000, CancellationToken.None);
        Assert.Equal(5, stored.SizeBytes);
        Assert.Equal(SHA256.HashData("hello"u8.ToArray()), stored.Sha256);
        await using var s = await store.OpenReadAsync(key, CancellationToken.None);
        Assert.Equal("hello", await new StreamReader(s).ReadToEndAsync());
        Directory.Delete(root, true);
    }
}
