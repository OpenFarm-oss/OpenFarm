using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace DatabaseAccess.Models;

public partial class OpenFarmContext : DbContext
{
    public OpenFarmContext(DbContextOptions<OpenFarmContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Color> Colors { get; set; }

    public virtual DbSet<Email> Emails { get; set; }

    public virtual DbSet<EmailMessage> EmailMessages { get; set; }

    public virtual DbSet<Emailautoreplyrule> Emailautoreplyrules { get; set; }

    public virtual DbSet<Maintenance> Maintenances { get; set; }

    public virtual DbSet<Material> Materials { get; set; }

    public virtual DbSet<MaterialPricePeriod> MaterialPricePeriods { get; set; }

    public virtual DbSet<MaterialType> MaterialTypes { get; set; }

    public virtual DbSet<Print> Prints { get; set; }

    public virtual DbSet<PrintJob> PrintJobs { get; set; }

    public virtual DbSet<Printer> Printers { get; set; }

    public virtual DbSet<PrinterModel> PrinterModels { get; set; }

    public virtual DbSet<PrinterModelPricePeriod> PrinterModelPricePeriods { get; set; }

    public virtual DbSet<PrintersLoadedMaterial> PrintersLoadedMaterials { get; set; }

    public virtual DbSet<Thread> Threads { get; set; }

    public virtual DbSet<Thumbnail> Thumbnails { get; set; }

    public virtual DbSet<User> Users { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Color>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("colors_pkey");

            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
        });

        modelBuilder.Entity<Email>(entity =>
        {
            entity.HasKey(e => new { e.UserId, e.EmailAddress }).HasName("emails_pkey");

            entity.HasOne(d => d.User).WithMany(p => p.Emails).HasConstraintName("emails_user_id_fkey");
        });

        modelBuilder.Entity<EmailMessage>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("email_messages_pkey");

            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.MessageStatus).HasDefaultValueSql("'unseen'::character varying");
            entity.Property(e => e.SenderType).HasDefaultValueSql("'user'::character varying");

            entity.HasOne(d => d.Thread).WithMany(p => p.EmailMessages).HasConstraintName("email_messages_thread_id_fkey");
        });

        modelBuilder.Entity<Emailautoreplyrule>(entity =>
        {
            entity.HasKey(e => e.Emailautoreplyruleid).HasName("emailautoreplyrules_pkey");

            entity.Property(e => e.Emailautoreplyruleid).UseIdentityAlwaysColumn();
        });

        modelBuilder.Entity<Maintenance>(entity =>
        {
            entity.HasKey(e => e.MaintenanceReportId).HasName("maintenance_pkey");

            entity.Property(e => e.MaintenanceReportId).ValueGeneratedNever();

            entity.HasOne(d => d.MaintenanceReport).WithOne(p => p.Maintenance).HasConstraintName("maintenance_maintenance_report_id_fkey");
        });

        modelBuilder.Entity<Material>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("materials_pkey");

            entity.Property(e => e.Id).UseIdentityAlwaysColumn();

            entity.HasOne(d => d.MaterialColor).WithMany(p => p.Materials).HasConstraintName("materials_material_color_id_fkey");

            entity.HasOne(d => d.MaterialType).WithMany(p => p.Materials).HasConstraintName("materials_material_type_id_fkey");
        });

        modelBuilder.Entity<MaterialPricePeriod>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("material_price_periods_pkey");

            entity.HasIndex(e => e.MaterialId, "uq_material_one_active_price")
                .IsUnique()
                .HasFilter("(ended_at IS NULL)");

            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(d => d.Material).WithOne(p => p.MaterialPricePeriod).HasConstraintName("material_price_periods_material_id_fkey");
        });

        modelBuilder.Entity<MaterialType>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("material_types_pkey");

            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
        });

        modelBuilder.Entity<Print>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("prints_pkey");

            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.PrintStatus).HasDefaultValueSql("'pending'::character varying");

            entity.HasOne(d => d.PrintJob).WithMany(p => p.Prints)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("prints_print_job_id_fkey");

            entity.HasOne(d => d.Printer).WithMany(p => p.Prints)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("prints_printer_id_fkey");
        });

        modelBuilder.Entity<PrintJob>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("print_jobs_pkey");

            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.JobStatus).HasDefaultValueSql("'received'::character varying");
            entity.Property(e => e.NumCopies).HasDefaultValue(1);

            entity.HasOne(d => d.Material).WithMany(p => p.PrintJobs)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("print_jobs_material_id_fkey");

            entity.HasOne(d => d.PrinterModel).WithMany(p => p.PrintJobs)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("print_jobs_printer_model_id_fkey");

            entity.HasOne(d => d.User).WithMany(p => p.PrintJobs)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("print_jobs_user_id_fkey");
        });

        modelBuilder.Entity<Printer>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("printers_pkey");

            entity.Property(e => e.Id).UseIdentityAlwaysColumn();

            entity.HasOne(d => d.PrinterModel).WithMany(p => p.Printers).HasConstraintName("printers_printer_model_id_fkey");
        });

        modelBuilder.Entity<PrinterModel>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("printer_models_pkey");

            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
        });

        modelBuilder.Entity<PrinterModelPricePeriod>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("printer_model_price_periods_pkey");

            entity.HasIndex(e => e.PrinterModelId, "uq_printer_one_active_price")
                .IsUnique()
                .HasFilter("(ended_at IS NULL)");

            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(d => d.PrinterModel).WithOne(p => p.PrinterModelPricePeriod)
                .OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("printer_model_price_periods_printer_model_id_fkey");
        });

        modelBuilder.Entity<PrintersLoadedMaterial>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("printers_loaded_materials_pkey");

            entity.Property(e => e.Id).UseIdentityAlwaysColumn();

            entity.HasOne(d => d.Material).WithMany(p => p.PrintersLoadedMaterials).HasConstraintName("printers_loaded_materials_material_id_fkey");

            entity.HasOne(d => d.Printer).WithMany(p => p.PrintersLoadedMaterials).HasConstraintName("printers_loaded_materials_printer_id_fkey");
        });

        modelBuilder.Entity<Thread>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("threads_pkey");

            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.ThreadStatus).HasDefaultValueSql("'unresolved'::character varying");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            entity.HasOne(d => d.Job).WithMany(p => p.Threads)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("threads_job_id_fkey");

            entity.HasOne(d => d.User).WithMany(p => p.Threads).HasConstraintName("threads_user_id_fkey");
        });

        modelBuilder.Entity<Thumbnail>(entity =>
        {
            entity.HasKey(e => e.PrintJobId).HasName("thumbnails_pkey");

            entity.Property(e => e.PrintJobId).ValueGeneratedNever();

            entity.HasOne(d => d.PrintJob).WithOne(p => p.Thumbnail).HasConstraintName("thumbnails_print_job_id_fkey");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("users_pkey");

            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
