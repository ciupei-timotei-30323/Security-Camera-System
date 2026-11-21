using Microsoft.EntityFrameworkCore;

namespace Security_Camera_Backend.Models;

public class SecurityCameraBackendContext : DbContext
{
    public DbSet<DetectionEvent> DetectionEvents { get; set; }

    public DbSet<User> Users { get; set; }

    public DbSet<RevokedToken> RevokedTokens { get; set; }

    public DbSet<CameraSystem> CameraSystems { get; set; }

    public DbSet<PiDevice> PiDevices { get; set; }

    public DbSet<AwayModeSetting> AwayModeSettings { get; set; }

    public SecurityCameraBackendContext(DbContextOptions options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CameraSystem>(cameraSystem =>
        {
            cameraSystem.ToTable("CameraSystems");
            cameraSystem.HasKey(s => s.SystemId);

            cameraSystem.Property(s => s.SystemId)
                .IsRequired()
                .HasMaxLength(64);

            cameraSystem.Property(s => s.DisplayName)
                .HasMaxLength(128);

            cameraSystem.Property(s => s.CreatedAtUtc)
                .IsRequired()
                .HasDefaultValueSql("timezone('utc', now())");
        });

        modelBuilder.Entity<PiDevice>(device =>
        {
            device.ToTable("PiDevices");
            device.HasKey(d => d.Id);

            device.Property(d => d.SystemId)
                .IsRequired()
                .HasMaxLength(64);

            device.Property(d => d.ApiKeyHash)
                .IsRequired()
                .HasMaxLength(64);

            device.HasIndex(d => d.ApiKeyHash)
                .IsUnique();

            device.HasIndex(d => d.SystemId);

            device.Property(d => d.Name)
                .HasMaxLength(128);

            device.Property(d => d.DeviceType)
                .IsRequired()
                .HasMaxLength(32)
                .HasConversion<string>()
                .HasDefaultValue(DeviceType.Camera);

            device.Property(d => d.LastSeenAt);

            device.HasIndex(d => new { d.SystemId, d.DeviceType });

            device.Property(d => d.CreatedAtUtc)
                .IsRequired()
                .HasDefaultValueSql("timezone('utc', now())");

            device.HasOne(d => d.System)
                .WithMany(s => s.PiDevices)
                .HasForeignKey(d => d.SystemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AwayModeSetting>(setting =>
        {
            setting.ToTable("AwayModeSettings");
            setting.HasKey(s => s.SystemId);

            setting.Property(s => s.SystemId)
                .IsRequired()
                .HasMaxLength(64);

            setting.Property(s => s.IsArmed)
                .IsRequired();

            setting.Property(s => s.Mode)
                .IsRequired();

            setting.Property(s => s.TimeZone)
                .IsRequired()
                .HasMaxLength(64);

            setting.Property(s => s.AlertEmail)
                .HasMaxLength(256);

            setting.Property(s => s.UpdatedAtUtc)
                .IsRequired()
                .HasDefaultValueSql("timezone('utc', now())");
        });

        modelBuilder.Entity<User>(user =>
        {
            user.ToTable("Users");
            user.HasKey(u => u.Id);

            user.Property(u => u.SystemId)
                .IsRequired()
                .HasMaxLength(64);

            user.Property(u => u.Username)
                .IsRequired()
                .HasMaxLength(64);

            // Sub-users are unique per camera system, not globally: two systems may both have
            // a user called "alice". Callers that omit systemId must disambiguate at login.
            user.HasIndex(u => new { u.SystemId, u.Username })
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

        modelBuilder.Entity<DetectionEvent>(detectionEvent =>
        {
            detectionEvent.ToTable("DetectionEvents");
            detectionEvent.HasKey(e => e.Id);

            detectionEvent.Property(e => e.SystemId)
                .IsRequired()
                .HasMaxLength(64);

            detectionEvent.HasIndex(e => new { e.SystemId, e.Timestamp });

            // Idempotency key supplied by the Pi. PostgreSQL treats nulls as distinct, so events
            // without a key do not conflict.
            detectionEvent.HasIndex(e => new { e.SystemId, e.ClientEventId })
                .IsUnique();
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
