using Microsoft.EntityFrameworkCore;

namespace DeliAi.Backend;

public class DeliDbContext : DbContext
{
  public DeliDbContext(DbContextOptions<DeliDbContext> options) : base(options) { }

  public DbSet<FactEntity> Facts => Set<FactEntity>();
  public DbSet<ArchivedFactEntity> ArchivedFacts => Set<ArchivedFactEntity>();
  public DbSet<UnansweredQuestionEntity> UnansweredQuestions => Set<UnansweredQuestionEntity>();
  public DbSet<FlaggedMessageEntity> FlaggedMessages => Set<FlaggedMessageEntity>();
  public DbSet<WrongInfoReportEntity> WrongInfoReports => Set<WrongInfoReportEntity>();

  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
    modelBuilder.Entity<FactEntity>().Property(f => f.Info).IsRequired();
    modelBuilder.Entity<ArchivedFactEntity>().Property(f => f.Info).IsRequired();
    modelBuilder.Entity<UnansweredQuestionEntity>().Property(f => f.Info).IsRequired();
    modelBuilder.Entity<FlaggedMessageEntity>().Property(f => f.Question).IsRequired();
    modelBuilder.Entity<WrongInfoReportEntity>().Property(f => f.Response).IsRequired();
  }
}