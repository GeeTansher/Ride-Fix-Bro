using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

namespace RideFixBro.Data.Entities;

public partial class RideFixBroDbContext : DbContext
{
    public RideFixBroDbContext(DbContextOptions<RideFixBroDbContext> options)
        : base(options)
    {
    }

    public virtual DbSet<ChatSession> ChatSessions { get; set; }

    public virtual DbSet<MasterBike> MasterBikes { get; set; }

    public virtual DbSet<MasterUserRole> MasterUserRoles { get; set; }

    public virtual DbSet<Message> Messages { get; set; }

    public virtual DbSet<User> Users { get; set; }

    public virtual DbSet<UserBike> UserBikes { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChatSession>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__ChatSess__3214EC07DA5E4153");

            entity.ToTable("ChatSessions", "RideFix");

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getutcdate())");
            entity.Property(e => e.IsActive).HasDefaultValue(true);

            entity.HasOne(d => d.UserBike).WithMany(p => p.ChatSessionUserBikes)
                .HasForeignKey(d => d.UserBikeId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_ChatSessions_UserBikes");

            entity.HasOne(d => d.User).WithMany(p => p.ChatSessions)
                .HasForeignKey(d => d.UserId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_ChatSessions_Users");

            entity.HasOne(d => d.UserBikeNavigation).WithMany(p => p.ChatSessionUserBikeNavigations)
                .HasPrincipalKey(p => new { p.Id, p.UserId })
                .HasForeignKey(d => new { d.UserBikeId, d.UserId })
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_ChatSessions_UserBikeOwner");
        });

        modelBuilder.Entity<MasterBike>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__MasterBi__3214EC0791B8DB38");

            entity.ToTable("MasterBikes", "RideFix_Customs");

            entity.Property(e => e.Make).HasMaxLength(100);
            entity.Property(e => e.Model).HasMaxLength(100);
        });

        modelBuilder.Entity<MasterUserRole>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__MasterUs__3214EC077C3C047F");

            entity.ToTable("MasterUserRoles", "RideFix_Customs");

            entity.HasIndex(e => e.RoleName, "UQ__MasterUs__8A2B616056531D2F").IsUnique();

            entity.Property(e => e.Description).HasMaxLength(255);
            entity.Property(e => e.RoleName).HasMaxLength(100);
        });

        modelBuilder.Entity<Message>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Messages__3214EC072D22A476");

            entity.ToTable("Messages", "RideFix");

            entity.HasIndex(e => new { e.ChatSessionId, e.SequenceNumber }, "UQ_Messages_Session_Sequence").IsUnique();

            entity.Property(e => e.Role).HasMaxLength(50);
            entity.Property(e => e.Timestamp).HasDefaultValueSql("(getutcdate())");

            entity.HasOne(d => d.ChatSession).WithMany(p => p.Messages)
                .HasForeignKey(d => d.ChatSessionId)
                .HasConstraintName("FK_Messages_ChatSessions");
        });

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__Users__3214EC078CB89933");

            entity.ToTable("Users", "RideFix");

            entity.HasIndex(e => e.SupabaseUserId, "UQ__Users__9B391302428B1FDB").IsUnique();

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getutcdate())");
            entity.Property(e => e.Email).HasMaxLength(255);

            entity.HasOne(d => d.Role).WithMany(p => p.Users)
                .HasForeignKey(d => d.RoleId)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("FK_Users_MasterUserRoles");
        });

        modelBuilder.Entity<UserBike>(entity =>
        {
            entity.HasKey(e => e.Id).HasName("PK__UserBike__3214EC074743F86A");

            entity.ToTable("UserBikes", "RideFix");

            entity.HasIndex(e => new { e.Id, e.UserId }, "UQ_UserBikes_Id_UserId").IsUnique();

            entity.Property(e => e.CreatedAt).HasDefaultValueSql("(getutcdate())");

            entity.HasOne(d => d.Bike).WithMany(p => p.UserBikes)
                .HasForeignKey(d => d.BikeId)
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
