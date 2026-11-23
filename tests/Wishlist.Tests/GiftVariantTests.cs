using Wishlist.Contracts;
using Wishlist.Core;
using Xunit;

namespace Wishlist.Tests;

public class GiftVariantTests
{
    [Fact]
    public void VariantWishesCannotChangeToOrFromUnlimited()
    {
        var owner = Guid.NewGuid();
        var donor = Guid.NewGuid();
        var unlimited = Gift.New(null, owner, new GiftInput("Flowers"));
        unlimited.Unlimited = true;
        Assert.Throws<RuleException>(() => unlimited.Edit(owner,
            new GiftWriteInput("Flowers", Variants: [new(null, "Roses")])));

        var gift = Gift.New(null, owner, new GiftWriteInput("Mouse", Variants: [new(null, "G305")]));
        gift.Act(donor, new("reserve", VariantId: gift.Variants[0].Id, VariantsVersion: gift.VariantsVersion), 1, true);
        Assert.Throws<RuleException>(() => gift.Edit(owner, new GiftWriteInput("Mouse", Unlimited: true, Variants: [])));
        gift.Edit(owner, new GiftWriteInput("Mouse", Variants: []));
        Assert.Throws<RuleException>(() => gift.Edit(owner, new GiftWriteInput("Mouse", Unlimited: true)));
    }

    [Fact]
    public void SplitKeepsSelectedVariantThroughEditsAndCoordinatorChanges()
    {
        var owner = Guid.NewGuid();
        var donor = Guid.NewGuid();
        var participant = Guid.NewGuid();
        var gift = Gift.New(null, owner, new GiftWriteInput("Mouse",
            Variants: [new(null, "G305", "https://example.com/mouse", 3500)]));
        var variant = Assert.Single(gift.Variants);
        var revision = gift.VariantsVersion;
        gift.Act(donor, new("split", VariantId: variant.Id, VariantsVersion: revision), 2, true);
        gift.Edit(owner, new GiftWriteInput("Mouse", Variants: [new(variant.Id, "G305", Price: 4000)]));
        gift.Act(participant, new("join"), 2, true);
        gift.Act(donor, new("transfer", UserId: participant), 2, true);
        var people = new[] { owner, donor, participant }.ToDictionary(id => id, id => new PersonDto(id, "Person"));
        Assert.Equal(3500, gift.View(participant, people).SelectedVariant?.Price);
        Assert.Null(gift.View(owner, people).SelectedVariant);
        Assert.Equal(4000, Assert.Single(gift.View(owner, people).Variants!).Price);
        gift.Act(donor, new("leave"), 2, true);
        gift.Act(participant, new("cancel"), 2, true);
        Assert.Throws<RuleException>(() => gift.Act(donor,
            new("reserve", VariantId: variant.Id, VariantsVersion: revision), 2, true));
        gift.Act(donor, new("reserve", VariantId: variant.Id, VariantsVersion: gift.VariantsVersion), 2, true);
        Assert.Equal(4000, gift.View(donor, people).SelectedVariant?.Price);
        Assert.Null(gift.View(participant, people).SelectedVariant);
    }
}
