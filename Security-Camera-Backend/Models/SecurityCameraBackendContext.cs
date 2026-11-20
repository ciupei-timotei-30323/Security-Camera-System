using Microsoft.EntityFrameworkCore;

namespace Security_Camera_Backend.Models;

public class SecurityCameraBackendContext: DbContext
{
    
    public DbSet<DetectionEvent> DetectionEvents { get; set; }

    public SecurityCameraBackendContext(DbContextOptions options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        
        
    }
}

