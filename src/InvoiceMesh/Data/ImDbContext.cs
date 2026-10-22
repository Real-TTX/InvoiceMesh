using System.Linq.Expressions;
using InvoiceMesh.Data.Entities;
using InvoiceMesh.Services;
using Microsoft.EntityFrameworkCore;

namespace InvoiceMesh.Data;

public class ImDbContext(DbContextOptions<ImDbContext> options, ICurrentUser currentUser, TimeProvider clock) : DbContext(options)
{
    public DbSet<User> User => Set<User>();

    public DbSet<UserTotp> UserTotp => Set<UserTotp>();

    public DbSet<UserRecoveryCode> UserRecoveryCode => Set<UserRecoveryCode>();

    public DbSet<UserSession> UserSession => Set<UserSession>();

    public DbSet<InvoiceDocument> Invoice => Set<InvoiceDocument>();

    public DbSet<InvoiceExtraction> InvoiceExtraction => Set<InvoiceExtraction>();

    public DbSet<InvoiceCorrection> InvoiceCorrection => Set<InvoiceCorrection>();

    public DbSet<InvoiceRevision> InvoiceRevision => Set<InvoiceRevision>();

    public DbSet<InvoiceValidation> InvoiceValidation => Set<InvoiceValidation>();

    public DbSet<GeneratedFile> GeneratedFile => Set<GeneratedFile>();

    public DbSet<InvoiceEvent> InvoiceEvent => Set<InvoiceEvent>();

    public DbSet<TargetSchema> TargetSchema => Set<TargetSchema>();

    public DbSet<TargetSchemaVersion> TargetSchemaVersion => Set<TargetSchemaVersion>();

    public DbSet<MappingSet> MappingSet => Set<MappingSet>();

    public DbSet<MappingSetVersion> MappingSetVersion => Set<MappingSetVersion>();

    public DbSet<ExportProfile> ExportProfile => Set<ExportProfile>();

    public DbSet<StoredFile> StoredFile => Set<StoredFile>();

    public DbSet<ShareLink> ShareLink => Set<ShareLink>();

    public DbSet<ActivityLog> ActivityLog => Set<ActivityLog>();

    public DbSet<BackupTarget> BackupTarget => Set<BackupTarget>();

    public DbSet<BackupPlan> BackupPlan => Set<BackupPlan>();

    public DbSet<BackupRun> BackupRun => Set<BackupRun>();

    public DbSet<IntakeSource> IntakeSource => Set<IntakeSource>();

    public DbSet<ExportPlan> ExportPlan => Set<ExportPlan>();

    public DbSet<ApiKey> ApiKey => Set<ApiKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // The document of InvoiceMesh is called InvoiceDocument in code (an "Invoice" is everywhere else in .NET); its table is "Invoice".
        b.Entity<InvoiceDocument>().ToTable("Invoice");

        // Tables are PascalCase and named like the entity; enums are stored as text; money has two decimals; JSON documents are jsonb.
        foreach (var entityType in b.Model.GetEntityTypes())
        {
            if (entityType.ClrType != typeof(InvoiceDocument))
            {
                entityType.SetTableName(entityType.ClrType.Name);
            }

            foreach (var property in entityType.GetProperties())
            {
                var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                // UpdateState is a number on purpose (0 = deleted, 1 = created, 2 = updated); DayOfWeek is one too.
                if (type.IsEnum && type != typeof(DayOfWeek) && type != typeof(UpdateState))
                {
                    property.SetProviderClrType(typeof(string));
                }
                else if (type == typeof(decimal) && property.GetColumnType() is null)
                {
                    property.SetColumnType("numeric(18,2)");
                }
                else if (type == typeof(string) && property.Name.EndsWith("Json", StringComparison.Ordinal))
                {
                    property.SetColumnType("jsonb");
                }
            }

            if (typeof(ISoftDeletable).IsAssignableFrom(entityType.ClrType))
            {
                b.Entity(entityType.ClrType).HasQueryFilter(NotDeleted(entityType.ClrType));
            }
        }

        b.Entity<User>().Ignore(x => x.Initials);
        b.Entity<InvoiceDocument>().Ignore(x => x.DisplayName);
        b.Entity<MappingSet>().Ignore(x => x.Specificity);

        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique().HasFilter("\"UpdateState\" <> 0");
            e.HasOne(x => x.Totp).WithOne(x => x.User).HasForeignKey<UserTotp>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<UserTotp>().HasIndex(x => x.UserId).IsUnique();
        b.Entity<UserRecoveryCode>().HasOne(x => x.UserTotp).WithMany(x => x.RecoveryCodes).HasForeignKey(x => x.UserTotpId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<UserSession>(e =>
        {
            e.HasIndex(x => x.Token).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<InvoiceDocument>(e =>
        {
            e.Property(x => x.GrossTotal).HasColumnType("numeric(18,4)");
            e.HasIndex(x => x.ReceivedDate);
            e.HasIndex(x => x.Sha256);
            e.HasIndex(x => x.ProcessingStatus);
            e.HasIndex(x => x.ValidationStatus);
            e.HasIndex(x => x.InvoiceNumber);
            e.HasOne(x => x.OriginalFile).WithMany().HasForeignKey(x => x.OriginalFileId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.EmbeddedXmlFile).WithMany().HasForeignKey(x => x.EmbeddedXmlFileId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<InvoiceExtraction>(e =>
        {
            e.HasIndex(x => new { x.InvoiceId, x.Number }).IsUnique();
            e.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<InvoiceCorrection>(e =>
        {
            e.HasIndex(x => new { x.InvoiceId, x.Scope, x.Key });
            e.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<InvoiceRevision>(e =>
        {
            e.HasIndex(x => new { x.InvoiceId, x.Number }).IsUnique();
            e.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.SchemaVersion).WithMany().HasForeignKey(x => x.SchemaVersionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.MappingVersion).WithMany().HasForeignKey(x => x.MappingVersionId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<InvoiceValidation>().HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<GeneratedFile>(e =>
        {
            e.HasIndex(x => new { x.InvoiceId, x.Kind });
            e.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.StoredFile).WithMany().HasForeignKey(x => x.StoredFileId).OnDelete(DeleteBehavior.Restrict);
        });
        b.Entity<InvoiceEvent>(e =>
        {
            e.HasIndex(x => new { x.InvoiceId, x.OccurredDate });
            e.HasIndex(x => x.OccurredDate);
            e.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TargetSchemaVersion>(e =>
        {
            e.HasIndex(x => new { x.TargetSchemaId, x.Number }).IsUnique();
            e.HasOne(x => x.TargetSchema).WithMany(x => x.Versions).HasForeignKey(x => x.TargetSchemaId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<MappingSet>().HasOne(x => x.TargetSchema).WithMany().HasForeignKey(x => x.TargetSchemaId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<MappingSetVersion>(e =>
        {
            e.HasIndex(x => new { x.MappingSetId, x.Number }).IsUnique();
            e.HasOne(x => x.MappingSet).WithMany(x => x.Versions).HasForeignKey(x => x.MappingSetId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ExportProfile>().HasOne(x => x.TargetSchema).WithMany().HasForeignKey(x => x.TargetSchemaId).OnDelete(DeleteBehavior.Restrict);

        b.Entity<StoredFile>().HasIndex(x => x.Token).IsUnique();
        b.Entity<ShareLink>(e =>
        {
            e.HasIndex(x => x.Token).IsUnique();
            e.HasOne(x => x.Invoice).WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ActivityLog>().HasIndex(x => x.OccurredDate);
        b.Entity<BackupPlan>().HasOne(x => x.Target).WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<BackupRun>().HasOne(x => x.Plan).WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.SetNull);

        b.Entity<ExportPlan>().HasOne(x => x.ExportProfile).WithMany().HasForeignKey(x => x.ExportProfileId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ApiKey>(e =>
        {
            e.HasIndex(x => x.KeyHash).IsUnique();
            e.HasIndex(x => x.Token).IsUnique();
        });
        b.Entity<InvoiceDocument>().Property(x => x.SourceDetail).HasMaxLength(500);
    }

    private static LambdaExpression NotDeleted(Type clrType)
    {
        var parameter = Expression.Parameter(clrType, "e");
        var state = Expression.Property(parameter, nameof(ISoftDeletable.UpdateState));
        var body = Expression.NotEqual(state, Expression.Constant(UpdateState.Deleted));
        return Expression.Lambda(body, parameter);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Stamp();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Stamp();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>Fills the audit columns and turns a delete of a soft-deletable record into a state change.</summary>
    private void Stamp()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var userId = currentUser.UserId;

        foreach (var entry in ChangeTracker.Entries<AuditedEntity>().ToList())
        {
            if (entry.State == EntityState.Deleted && entry.Entity is ISoftDeletable soft)
            {
                entry.State = EntityState.Modified;
                soft.UpdateState = UpdateState.Deleted;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.CreateDate == default)
                    {
                        entry.Entity.CreateDate = now;
                    }

                    entry.Entity.CreateUserId ??= userId;
                    entry.Entity.UpdateDate = entry.Entity.CreateDate;
                    entry.Entity.UpdateUserId = entry.Entity.CreateUserId;
                    if (entry.Entity is ISoftDeletable added && added.UpdateState == UpdateState.Deleted)
                    {
                        added.UpdateState = UpdateState.Created;
                    }

                    break;

                case EntityState.Modified:
                    entry.Entity.UpdateDate = now;
                    entry.Entity.UpdateUserId = userId;
                    entry.Property(nameof(AuditedEntity.CreateDate)).IsModified = false;
                    entry.Property(nameof(AuditedEntity.CreateUserId)).IsModified = false;
                    if (entry.Entity is ISoftDeletable modified && modified.UpdateState == UpdateState.Created)
                    {
                        modified.UpdateState = UpdateState.Updated;
                    }

                    break;
            }
        }
    }
}
