using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AoS2.Backend.Data;

/// <summary>
/// Die Datenbank des Backends. Tabellen und Spalten heißen in PostgreSQL wie die Klassen,
/// nur in snake_case (<c>LoginAttempt.ExpiresAt</c> → <c>login_attempts.expires_at</c>).
/// <para>Schema ändern: Klasse anpassen, dann <c>dotnet ef migrations add Name --project backend/AoS2.Backend</c>.</para>
/// </summary>
public sealed class BackendDb(DbContextOptions<BackendDb> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<LoginAttempt> LoginAttempts => Set<LoginAttempt>();
    public DbSet<ListedServer> ListedServers => Set<ListedServer>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Account>(account =>
        {
            account.HasIndex(a => a.DiscordId).IsUnique();
            account.Property(a => a.DiscordId).HasMaxLength(32);
            account.Property(a => a.DiscordUsername).HasMaxLength(64);
            account.Property(a => a.DisplayName).HasMaxLength(64);
            account.Property(a => a.AvatarHash).HasMaxLength(64);
        });

        model.Entity<Session>(session =>
        {
            session.HasIndex(s => s.TokenHash).IsUnique();
            session.HasIndex(s => s.ExpiresAt);
            session.Property(s => s.TokenHash).HasMaxLength(64);
            session.HasOne(s => s.Account).WithMany().HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<LoginAttempt>(attempt =>
        {
            attempt.HasIndex(a => a.ExchangeCodeHash).IsUnique();
            attempt.Property(a => a.Id).HasMaxLength(64);
            attempt.Property(a => a.LauncherState).HasMaxLength(128);
            attempt.Property(a => a.CodeChallenge).HasMaxLength(64);
            attempt.Property(a => a.ExchangeCodeHash).HasMaxLength(64);
            attempt.HasOne<Account>().WithMany().HasForeignKey(a => a.AccountId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ListedServer>(server =>
        {
            server.HasIndex(s => s.Address).IsUnique();
            server.HasIndex(s => s.LastHeartbeatAt);
            server.HasIndex(s => s.RegisteredFromIp);
            server.Property(s => s.Address).HasMaxLength(ListedServer.MaxAddressLength);
            server.Property(s => s.Name).HasMaxLength(64);
            server.Property(s => s.Map).HasMaxLength(64);
            server.Property(s => s.GameVersion).HasMaxLength(32);
            server.Property(s => s.SecretHash).HasMaxLength(64);
            server.Property(s => s.RegisteredFromIp).HasMaxLength(64);
        });
    }
}

/// <summary>Ein Spieler. Entsteht beim ersten Discord-Login.</summary>
public sealed class Account
{
    public Guid Id { get; set; }

    /// <summary>Discords Benutzer-ID (Snowflake). Ändert sich nie, anders als der Name.</summary>
    public required string DiscordId { get; set; }

    public required string DiscordUsername { get; set; }

    /// <summary>Wird bei jedem Login aus Discord übernommen.</summary>
    public required string DisplayName { get; set; }

    /// <summary>Kennung des Discord-Profilbilds, <c>null</c> ohne eigenes Bild.</summary>
    public string? AvatarHash { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime LastLoginAt { get; set; }
}

/// <summary>Eine Anmeldung im Launcher. Das Token selbst wird nicht gespeichert, nur seine SHA-256.</summary>
public sealed class Session
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public Account Account { get; set; } = null!;
    public required string TokenHash { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>Verschiebt sich bei Benutzung nach hinten (siehe <c>SessionAuthenticationHandler</c>).</summary>
    public DateTime ExpiresAt { get; set; }

    public DateTime LastUsedAt { get; set; }
}

/// <summary>
/// Eine laufende Anmeldung: entsteht beim Aufruf von <c>/auth/discord/login</c>, bekommt nach dem
/// Discord-Login einen Einmal-Code und wird gelöscht, sobald der Launcher ihn eintauscht.
/// </summary>
public sealed class LoginAttempt
{
    /// <summary>Zufallswert, geht als <c>state</c> an Discord und als Cookie an den Browser.</summary>
    public required string Id { get; set; }

    /// <summary>Port, auf dem der Launcher unter 127.0.0.1 wartet.</summary>
    public int Port { get; set; }

    public required string LauncherState { get; set; }

    /// <summary>PKCE: nur wer den passenden <c>code_verifier</c> kennt (der Launcher), kann den Einmal-Code eintauschen.</summary>
    public required string CodeChallenge { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>Gesetzt, sobald Discord den Spieler bestätigt hat.</summary>
    public Guid? AccountId { get; set; }

    public string? ExchangeCodeHash { get; set; }
}

/// <summary>
/// Ein Spielserver im Server-Browser. Entsteht beim ersten Heartbeat, sobald die Erreichbarkeitsprüfung bestanden ist;
/// jeder weitere Heartbeat aktualisiert ihn. Ohne Heartbeat zeigt die Liste ihn nach kurzer Zeit nicht mehr, später wird er gelöscht.
/// </summary>
public sealed class ListedServer
{
    public const int MaxAddressLength = 270; // Domainname (253) + [ ] : Port

    public Guid Id { get; set; }

    /// <summary>Adresse, wie Spieler sie eingeben (klein geschrieben, mit Port). Eindeutig.</summary>
    public required string Address { get; set; }

    public required string Name { get; set; }
    public required string Map { get; set; }
    public int Players { get; set; }
    public int MaxPlayers { get; set; }
    public int ProtocolVersion { get; set; }
    public required string GameVersion { get; set; }

    /// <summary>SHA-256 des Geheimnisses aus dem Heartbeat. Nur wer es kennt, kann den Eintrag ändern oder entfernen.</summary>
    public required string SecretHash { get; set; }

    /// <summary>Mit dem Schlüssel für offizielle Server angemeldet.</summary>
    public bool Official { get; set; }

    /// <summary>IP-Adresse, von der der Heartbeat kam (für die Grenze pro IP).</summary>
    public required string RegisteredFromIp { get; set; }

    public DateTime RegisteredAt { get; set; }
    public DateTime LastHeartbeatAt { get; set; }
}

/// <summary>Für <c>dotnet ef</c>: erzeugt die Datenbank ohne den Rest des Backends (Konfiguration, Discord, …).</summary>
internal sealed class BackendDbDesignTimeFactory : IDesignTimeDbContextFactory<BackendDb>
{
    public BackendDb CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BackendDb>();
        options.UseBackendDatabase("Host=localhost;Database=aos2");
        return new BackendDb(options.Options);
    }
}

internal static class BackendDbOptions
{
    public static DbContextOptionsBuilder UseBackendDatabase(this DbContextOptionsBuilder options, string connectionString) =>
        options.UseNpgsql(ToNpgsql(connectionString)).UseSnakeCaseNamingConvention();

    /// <summary>
    /// Nimmt auch die URL-Form <c>postgres://user:passwort@host:5432/datenbank</c>, wie Coolify sie anzeigt,
    /// und macht daraus die Form, die Npgsql versteht (<c>Host=…;Username=…</c>).
    /// </summary>
    public static string ToNpgsql(string connectionString)
    {
        if (!connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return connectionString;

        var uri = new Uri(connectionString);
        var user = uri.UserInfo.Split(':', 2);
        var database = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        return new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
            Database = database.Length > 0 ? database : "postgres",
            Username = Uri.UnescapeDataString(user[0]),
            Password = user.Length > 1 ? Uri.UnescapeDataString(user[1]) : null,
        }.ConnectionString;
    }
}
