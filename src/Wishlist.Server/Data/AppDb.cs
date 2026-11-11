using Microsoft.EntityFrameworkCore;
using Wishlist.Core;
namespace Wishlist.Server.Data;
public class AppUser {
 public Guid Id {get;set;}=Guid.NewGuid(); public long TelegramId {get;set;} public string Name {get;set;}="";
 public string? Username {get;set;} public string? Contact {get;set;} public Guid? CompanyId {get;set;}
 public string? TelegramAvatarUrl {get;set;}
 public Guid? OhmywishesAttempt {get;set;}
}
public class Company {public Guid Id {get;set;}=Guid.NewGuid();public string Name {get;set;}="";public Guid OrganizerId {get;set;}public string InviteHash {get;set;}="";public int? InviteRemaining {get;set;}}
public class CompanyMembership {public Guid UserId {get;set;} public Guid CompanyId {get;set;}}
public class Notice {public Guid Id {get;set;}=Guid.NewGuid();public Guid UserId {get;set;}public string Text {get;set;}="";public DateTimeOffset Created {get;set;}=DateTimeOffset.UtcNow;public bool Read {get;set;}}
public class GiftPhoto
{
 public Guid GiftId { get; set; }
 public required byte[] Data { get; set; }
 public required string ContentType { get; set; }
}
public class AppDb(DbContextOptions<AppDb> options):DbContext(options) {
 public DbSet<AppUser> Users=>Set<AppUser>(); public DbSet<Company> Companies=>Set<Company>();public DbSet<Gift> Gifts=>Set<Gift>();
 public DbSet<GiftShare> Shares=>Set<GiftShare>();public DbSet<Notice> Notices=>Set<Notice>();
 public DbSet<OhmywishesConnection> OhmywishesConnections=>Set<OhmywishesConnection>();
 public DbSet<GiftPhoto> GiftPhotos=>Set<GiftPhoto>();
 public DbSet<CompanyMembership> Memberships=>Set<CompanyMembership>();
 public DbSet<GiftPublication> GiftPublications=>Set<GiftPublication>();
 protected override void OnModelCreating(ModelBuilder b) {
  WishlistSharingModel.Configure(b);
  b.Entity<AppUser>().HasIndex(x=>x.TelegramId).IsUnique();b.Entity<AppUser>().Property(x=>x.Name).HasMaxLength(100);
  b.Entity<AppUser>().HasOne<Company>().WithMany().HasForeignKey(x=>x.CompanyId).OnDelete(DeleteBehavior.Restrict);
  b.Entity<Company>().Property(x=>x.Name).HasMaxLength(100);b.Entity<Company>().HasIndex(x=>x.InviteHash).IsUnique();
  b.Entity<CompanyMembership>().HasKey(x=>new{x.UserId,x.CompanyId});
  b.Entity<CompanyMembership>().HasOne<AppUser>().WithMany().HasForeignKey(x=>x.UserId).OnDelete(DeleteBehavior.Cascade);
  b.Entity<CompanyMembership>().HasOne<Company>().WithMany().HasForeignKey(x=>x.CompanyId).OnDelete(DeleteBehavior.Cascade);
  b.Entity<GiftPublication>().HasKey(x=>new{x.GiftId,x.CompanyId});
  b.Entity<GiftPublication>().HasOne<Company>().WithMany().HasForeignKey(x=>x.CompanyId).OnDelete(DeleteBehavior.Cascade);
  b.Entity<Gift>().HasMany(x=>x.Publications).WithOne().HasForeignKey(x=>x.GiftId).OnDelete(DeleteBehavior.Cascade);
  b.Entity<Gift>().Ignore(x=>x.Details);b.Entity<Gift>().Property(x=>x.Title).HasMaxLength(160);b.Entity<Gift>().Property(x=>x.Price).HasPrecision(12,2);
  b.Entity<Gift>().HasIndex(x=>new{x.CompanyId,x.OwnerId});b.Entity<Gift>().HasOne<Company>().WithMany().HasForeignKey(x=>x.CompanyId).OnDelete(DeleteBehavior.Restrict);
  b.Entity<Gift>().HasIndex(x=>new{x.OwnerId,x.OhmywishesKey}).IsUnique();
  b.Entity<GiftPhoto>().HasKey(x=>x.GiftId);
  b.Entity<GiftPhoto>().HasOne<Gift>().WithOne().HasForeignKey<GiftPhoto>(x=>x.GiftId).OnDelete(DeleteBehavior.Cascade);
  b.Entity<OhmywishesConnection>().HasKey(x=>x.UserId);
  b.Entity<OhmywishesConnection>().HasOne<AppUser>().WithOne().HasForeignKey<OhmywishesConnection>(x=>x.UserId).OnDelete(DeleteBehavior.Cascade);
  b.Entity<OhmywishesConnection>().HasIndex(x=>x.NextSync);
  b.Entity<OhmywishesConnection>().HasOne<Company>().WithMany().HasForeignKey(x=>x.DefaultCompanyId).OnDelete(DeleteBehavior.SetNull);
  b.Entity<Gift>().HasOne<AppUser>().WithMany().HasForeignKey(x=>x.OwnerId).OnDelete(DeleteBehavior.Restrict);
  b.Entity<Gift>().HasOne<AppUser>().WithMany().HasForeignKey(x=>x.DonorId).OnDelete(DeleteBehavior.Restrict);
  b.Entity<Gift>().HasMany(x=>x.Shares).WithOne().HasForeignKey(x=>x.GiftId).OnDelete(DeleteBehavior.Cascade);
  b.Entity<GiftShare>().HasKey(x=>new{x.GiftId,x.UserId});b.Entity<GiftShare>().HasOne<AppUser>().WithMany().HasForeignKey(x=>x.UserId).OnDelete(DeleteBehavior.Restrict);
  b.Entity<Notice>().HasIndex(x=>new{x.UserId,x.Created});b.Entity<Notice>().HasOne<AppUser>().WithMany().HasForeignKey(x=>x.UserId).OnDelete(DeleteBehavior.Cascade);
  b.Entity<Gift>().ToTable(t=> {t.HasCheckConstraint("CK_Gift_Mode","\"Mode\" IN ('free','solo','split')");t.HasCheckConstraint("CK_Gift_State","(\"Mode\" = 'free' AND \"DonorId\" IS NULL AND \"Slots\" = 0 AND NOT \"Bought\") OR (\"Mode\" = 'solo' AND \"DonorId\" IS NOT NULL AND \"Slots\" = 0) OR (\"Mode\" = 'split' AND \"DonorId\" IS NOT NULL AND \"Slots\" >= 2)");});
 }
}
