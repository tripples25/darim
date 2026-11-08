using Microsoft.EntityFrameworkCore;
using Wishlist.Core;

namespace Wishlist.Server.Data;

public sealed class WishlistLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public Guid? CompanyId { get; set; }
    public string TokenHash { get; set; } = "";
    public string? Label { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset? Revoked { get; set; }
}

public sealed class WishlistReservation
{
    public const string Waiting = "waiting";
    public const string Held = "held";
    public const string Confirmed = "confirmed";
    public const string Closed = "closed";

    public Guid Id { get; set; } = Guid.NewGuid();
    public long Sequence { get; set; }
    public Guid LinkId { get; set; }
    public Guid GiftId { get; set; }
    public Guid GuestId { get; set; }
    public DateTimeOffset Created { get; set; }
    public string Status { get; set; } = Waiting;
    public DateTimeOffset Due { get; set; }
    public bool ShareUsername { get; set; }
    public Guid? InterestRecipientId { get; set; }
    public DateTimeOffset? Notified { get; set; }
}

public static class WishlistSharingModel
{
    public static void Configure(ModelBuilder model)
    {
        var link = model.Entity<WishlistLink>();
        link.ToTable("WishlistLinks");
        link.Property(x => x.TokenHash).HasMaxLength(64);
        link.Property(x => x.Label).HasMaxLength(80);
        link.HasIndex(x => x.TokenHash).IsUnique();
        link.HasIndex(x => new { x.OwnerId, x.Created });
        link.HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        link.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);

        var request = model.Entity<WishlistReservation>();
        request.ToTable("WishlistReservations", table =>
        {
            table.HasCheckConstraint("CK_WishlistReservation_Status", """ "Status" IN ('waiting','held','confirmed','closed') """);
        });
        request.Property(x => x.Sequence).UseIdentityByDefaultColumn();
        request.Property(x => x.Status).HasMaxLength(12);
        request.HasIndex(x => new { x.Status, x.Due });
        request.HasIndex(x => new { x.GiftId, x.Sequence });
        request.HasIndex(x => new { x.LinkId, x.Status });
        request.HasIndex(x => new { x.GiftId, x.GuestId }).IsUnique()
            .HasFilter(""" "Status" <> 'closed' """);
        request.HasIndex(x => x.GiftId).IsUnique().HasFilter(""" "Status" = 'held' """);
        request.HasOne<Gift>().WithMany().HasForeignKey(x => x.GiftId).OnDelete(DeleteBehavior.Restrict);
        request.HasOne<AppUser>().WithMany().HasForeignKey(x => x.GuestId).OnDelete(DeleteBehavior.Restrict);
        request.HasOne<WishlistLink>().WithMany().HasForeignKey(x => x.LinkId).OnDelete(DeleteBehavior.Restrict);
    }
}
