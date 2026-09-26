using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace RideFixBro.Data.Entities;

public partial class RideFixBroDbContext : DbContext
{
    public RideFixBroDbContext()
    {
    }

    public RideFixBroDbContext(DbContextOptions<RideFixBroDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<ChatSession> ChatSessions { get; set; }

    public virtual DbSet<MasterBike> MasterBikes { get; set; }

    public virtual DbSet<Message> Messages { get; set; }

    public virtual DbSet<User> Users { get; set; }

    public virtual DbSet<UserBike> UserBikes { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChatSession>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__ChatSess__3214EC07161E62A7");

            entity.ToTable("ChatSessions", "RideFix");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getutcdate())");
            entity.Property(e => e.IsActive).HasDefaultValue(true);

            entity.HasOne(d => d.UserBike).WithMany(p => p.ChatSessions)
                .HasForeignKey(d => d.UserBikeId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_ChatSessions_UserBikes");

            entity.HasOne(d => d.User).WithMany(p => p.ChatSessions)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_ChatSessions_Users");
        });

        modelBuilder.Entity<MasterBike>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__MasterBi__3214EC0763CF5AAE");

            entity.ToTable("MasterBikes", "RideFix_Customs");

            entity.Property(e => e.Make).HasMaxLength(100);
            entity.Property(e => e.Model).HasMaxLength(100);
        });

        modelBuilder.Entity<Message>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Messages__3214EC07BB14B429");

            entity.ToTable("Messages", "RideFix");

            entity.Property(e => e.Role).HasMaxLength(50);
            entity.Property(e => e.Timestamp).HasDefaultValueSql("(getutcdate())");

            entity.HasOne(d => d.ChatSession).WithMany(p => p.Messages)
                .HasForeignKey(d => d.ChatSessionId)
                .HasConstraintName("FK_Messages_ChatSessions");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Users__3214EC07A390C4ED");

            entity.ToTable("Users", "RideFix");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getutcdate())");
            entity.Property(e => e.Email).HasMaxLength(255);
            entity.Property(e => e.Role)
                .HasMaxLength(50)
                .HasDefaultValue("User");
        });

        modelBuilder.Entity<UserBike>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__UserBike__3214EC07B5CE33ED");

            entity.ToTable("UserBikes", "RideFix");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getutcdate())");

            entity.HasOne(d => d.MasterBike).WithMany(p => p.UserBikes)
                .HasForeignKey(d => d.MasterBikeId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_UserBikes_MasterBikes");

            entity.HasOne(d => d.User).WithMany(p => p.UserBikes)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_UserBikes_Users");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
