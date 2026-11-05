using Microsoft.EntityFrameworkCore;

public class AppDbContext : DbContext {
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Account> Accounts => Set<Account>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        base.OnModelCreating(modelBuilder);

        // Configure User constraints
        modelBuilder.Entity<User>(entity => {
            entity.HasKey(u => u.Id);
            entity.HasIndex(u => u.Email).IsUnique();
            entity.Property(u => u.Username).IsRequired().HasMaxLength(100);
        });

        // Configure Account constraints & exact financial decimal mapping
        modelBuilder.Entity<Account>(entity => {
            entity.HasKey(a => a.Id);
            entity.HasIndex(a => a.AccountNumber).IsUnique();
            
            // Critical Fintech Rule: Force NUMERIC(18,4) in PostgreSQL
            entity.Property(a => a.Balance)
                  .HasColumnType("numeric(18, 4)")
                  .IsRequired();

            entity.Property(a => a.CurrencyCode)
                  .IsRequired()
                  .HasMaxLength(3);

            // Relationship: One User has Many Accounts
            entity.HasOne(a => a.User)
                  .WithMany(u => u.Accounts)
                  .HasForeignKey(a => a.UserId)
                  .OnDelete(DeleteBehavior.Restrict); // Prevent cascading user deletion from wiping accounts
        });
    }
}