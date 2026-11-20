using Microsoft.EntityFrameworkCore;

namespace Security_Camera_Backend.Models;

public class SecurityCameraBackendContext: DbContext
{
    
    public DbSet<DetectionEvent> DetectionEvents { get; set; }

    public DbSet<User> Users { get; set; }

    public DbSet<RevokedToken> RevokedTokens { get; set; }

    public SecurityCameraBackendContext(DbContextOptions options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(user =>
        {
            user.ToTable("Users");
            user.HasKey(u => u.Id);

            user.Property(u => u.Username)
                .IsRequired()
                .HasMaxLength(64);

            user.HasIndex(u => u.Username)
                .IsUnique();

            user.Property(u => u.PasswordHash)
                .IsRequired();

            user.Property(u => u.Role)
                .IsRequired()
                .HasMaxLength(32);

            user.Property(u => u.CreatedAtUtc)
                .IsRequired()
                .HasDefaultValueSql("timezone('utc', now())");
        });

        modelBuilder.Entity<RevokedToken>(revokedToken =>
        {
            revokedToken.ToTable("RevokedTokens");
            revokedToken.HasKey(t => t.Id);

            revokedToken.Property(t => t.Jti)
                .IsRequired()
                .HasMaxLength(64);

            revokedToken.HasIndex(t => t.Jti)
                .IsUnique();

            revokedToken.Property(t => t.ExpiresAtUtc)
                .IsRequired();

            revokedToken.Property(t => t.RevokedAtUtc)
                .IsRequired()
                .HasDefaultValueSql("timezone('utc', now())");
        });
    }
}
