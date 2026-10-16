using Microsoft.EntityFrameworkCore;
using SignDesk.Domain;

namespace SignDesk.Infrastructure.Persistence;

public sealed class SignDeskDbContext(DbContextOptions<SignDeskDbContext> options) : DbContext(options)
{
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<Signer> Signers => Set<Signer>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Document>(b =>
        {
            b.HasKey(d => d.Id);
            b.Property(d => d.Id).ValueGeneratedNever();
            b.Property(d => d.Title).HasMaxLength(200).IsRequired();
            b.Property(d => d.OwnerEmail).HasMaxLength(320).IsRequired();
            b.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(d => d.Version).IsRowVersion();
            b.HasMany(d => d.Signers).WithOne().HasForeignKey(s => s.DocumentId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(d => d.Signers).HasField("_signers").UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Ignore(d => d.Events);
            b.Ignore(d => d.CurrentSigner);
            b.HasIndex(d => new { d.Status, d.ExpiresAt });
            b.HasIndex(d => new { d.OwnerEmail, d.CreatedAt });
        });

        modelBuilder.Entity<Signer>(b =>
        {
            b.HasKey(s => s.Id);
            b.Property(s => s.Id).ValueGeneratedNever();
            b.Property(s => s.Email).HasMaxLength(320).IsRequired();
            b.Property(s => s.Name).HasMaxLength(200).IsRequired();
            b.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(s => s.DeclineReason).HasMaxLength(1000);
            b.Property<uint>("Version").IsRowVersion();
            b.HasIndex(s => new { s.DocumentId, s.Email }).IsUnique();
        });

        modelBuilder.Entity<AuditEntry>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.Action).HasMaxLength(64).IsRequired();
            b.Property(a => a.Actor).HasMaxLength(320).IsRequired();
            b.Property(a => a.Details).HasMaxLength(1000);
            b.HasIndex(a => new { a.DocumentId, a.At });
        });

        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.HasKey(o => o.Id);
            b.Property(o => o.Id).ValueGeneratedNever();
            b.Property(o => o.Kind).HasConversion<string>().HasMaxLength(40);
            b.Property(o => o.Recipient).HasMaxLength(320).IsRequired();
            b.Property(o => o.LastError).HasMaxLength(500);
            b.HasIndex(o => o.NextAttemptAt).HasFilter("sent_at IS NULL AND dead_lettered_at IS NULL");
        });

        modelBuilder.Entity<WebhookEvent>(b =>
        {
            b.HasKey(w => w.Id);
            b.Property(w => w.EventId).HasMaxLength(100).IsRequired();
            b.Property(w => w.Type).HasMaxLength(64).IsRequired();
            b.HasIndex(w => w.EventId).IsUnique();
        });
    }
}
