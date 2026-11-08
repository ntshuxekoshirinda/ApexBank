using Microsoft.EntityFrameworkCore;

public class AppDbContext : DbContext {
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Transaction> Transactions => Set<Transaction>();

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
            
            entity.Property(a => a.Balance)
                  .HasColumnType("numeric(18, 4)")
                  .IsRequired();

            entity.Property(a => a.CurrencyCode)
                  .IsRequired()
                  .HasMaxLength(3);

            entity.HasOne(a => a.User)
                  .WithMany(u => u.Accounts)
                  .HasForeignKey(a => a.UserId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        // Configure Transaction Ledger constraints
        modelBuilder.Entity<Transaction>(entity => {
            entity.HasKey(t => t.Id);
            
            // Enforce unique idempotency keys at the database level
            entity.HasIndex(t => t.IdempotencyKey).IsUnique();

            entity.Property(t => t.Amount)
                  .HasColumnType("numeric(18, 4)")
                  .IsRequired();

            entity.Property(t => t.CurrencyCode)
                  .IsRequired()
                  .HasMaxLength(3);

            // Configure relationships without cascading deletes to protect financial history
            entity.HasOne(t => t.SourceAccount)
                  .WithMany()
                  .HasForeignKey(t => t.SourceAccountId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(t => t.DestinationAccount)
                  .WithMany()
                  .HasForeignKey(t => t.DestinationAccountId)
                  .OnDelete(DeleteBehavior.Restrict);
        });
    }
}