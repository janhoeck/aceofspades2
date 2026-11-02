using System.Security.Cryptography;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;

namespace AoS2.JoinTickets.Tests;

public sealed class JoinTicketTests : IDisposable
{
    private const string Backend = "https://api.example.org";
    private const string Server = "play.example.org:32887";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
    private readonly JoinTicketSigningKey _key = JoinTicketSigningKey.Generate();
    private readonly Guid _account = Guid.NewGuid();

    public void Dispose() => _key.Dispose();

    private string Issue(string server = Server, string issuer = Backend, JoinTicketSigningKey? key = null) =>
        new JoinTicketIssuer(key ?? _key, issuer, _time).Issue(_account, "Jan", server).Ticket;

    private Task<JoinTicket?> ValidateAsync(string ticket) =>
        new JoinTicketValidator(_key.ExportPublicKeySet(), Backend, Server, _time).ValidateAsync(ticket);

    [Fact]
    public async Task ValidTicket_ReturnsAccountAndName()
    {
        var (ticket, expires) = new JoinTicketIssuer(_key, Backend, _time).Issue(_account, "Jan", Server);

        var result = await ValidateAsync(ticket);

        Assert.NotNull(result);
        Assert.Equal(_account, result.AccountId);
        Assert.Equal("Jan", result.DisplayName);
        Assert.Equal(Server, result.Server);
        Assert.Equal(_time.GetUtcNow() + JoinTicketIssuer.Lifetime, expires);
        Assert.Equal(expires, result.ExpiresAt);
        Assert.False(string.IsNullOrEmpty(result.TicketId));
    }

    [Fact]
    public async Task ServerName_IgnoresCaseAndSpaces() =>
        Assert.NotNull(await ValidateAsync(Issue("  PLAY.Example.org:32887 ")));

    [Fact]
    public async Task EachTicket_HasOwnId()
    {
        var first = await ValidateAsync(Issue());
        var second = await ValidateAsync(Issue());

        Assert.NotEqual(first!.TicketId, second!.TicketId);
    }

    [Fact]
    public async Task OtherServer_IsRejected() => Assert.Null(await ValidateAsync(Issue(server: "other.example.org:32887")));

    [Fact]
    public async Task OtherIssuer_IsRejected() => Assert.Null(await ValidateAsync(Issue(issuer: "https://evil.example.org")));

    [Fact]
    public async Task SignedWithOtherKey_IsRejected()
    {
        using var otherKey = JoinTicketSigningKey.Generate();

        Assert.Null(await ValidateAsync(Issue(key: otherKey)));
    }

    [Fact]
    public async Task ExpiredTicket_IsRejected_AfterClockSkew()
    {
        var ticket = Issue();

        _time.Advance(JoinTicketIssuer.Lifetime + JoinTicketValidator.ClockSkew - TimeSpan.FromSeconds(1));
        Assert.NotNull(await ValidateAsync(ticket));

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(await ValidateAsync(ticket));
    }

    [Fact]
    public async Task TicketFromFuture_IsRejected()
    {
        // Backend-Uhr geht vor.
        var backendTime = new FakeTimeProvider(_time.GetUtcNow() + JoinTicketValidator.ClockSkew + TimeSpan.FromSeconds(1));
        var (ticket, _) = new JoinTicketIssuer(_key, Backend, backendTime).Issue(_account, "Jan", Server);

        Assert.Null(await ValidateAsync(ticket));
    }

    [Fact]
    public async Task TamperedPayload_IsRejected()
    {
        var parts = Issue().Split('.');
        var payload = Base64UrlEncoder.Decode(parts[1]).Replace("\"Jan\"", "\"Admin\"");

        Assert.Null(await ValidateAsync($"{parts[0]}.{Base64UrlEncoder.Encode(payload)}.{parts[2]}"));
    }

    [Fact]
    public async Task UnsignedTicket_IsRejected()
    {
        var parts = Issue().Split('.');
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");

        Assert.Null(await ValidateAsync($"{header}.{parts[1]}."));
    }

    [Theory]
    [InlineData("")]
    [InlineData("kein-jwt")]
    [InlineData("a.b.c")]
    public async Task Garbage_IsRejected(string ticket) => Assert.Null(await ValidateAsync(ticket));

    [Fact]
    public async Task ExportedPrivateKey_CanBeImportedAgain()
    {
        using var imported = JoinTicketSigningKey.Import(_key.ExportPrivateKey());

        Assert.Equal(_key.KeyId, imported.KeyId);
        Assert.NotNull(await ValidateAsync(Issue(key: imported)));
    }

    [Fact]
    public void Import_AcceptsPem()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var imported = JoinTicketSigningKey.Import(ecdsa.ExportPkcs8PrivateKeyPem());

        Assert.False(string.IsNullOrEmpty(imported.KeyId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nicht base64!")]
    [InlineData("AAAA")]
    public void Import_RejectsGarbage(string value) => Assert.Throws<FormatException>(() => JoinTicketSigningKey.Import(value));

    [Fact]
    public void Import_RejectsOtherCurve()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        Assert.Throws<FormatException>(() => JoinTicketSigningKey.Import(ecdsa.ExportPkcs8PrivateKeyPem()));
    }

    [Fact]
    public void PublicKeySet_ContainsNoPrivateKey()
    {
        var json = _key.ExportPublicKeySet();

        Assert.Contains($"\"kid\":\"{_key.KeyId}\"", json);
        Assert.Contains("\"alg\":\"ES256\"", json);
        Assert.DoesNotContain("\"d\"", json);
    }
}
