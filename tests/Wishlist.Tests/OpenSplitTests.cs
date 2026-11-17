using Wishlist.Contracts;
using Wishlist.Core;

namespace Wishlist.Tests;

public class OpenSplitTests
{
    readonly Guid owner = Guid.NewGuid(), first = Guid.NewGuid(), second = Guid.NewGuid(), third = Guid.NewGuid();
    Gift NewSplit()
    {
        var gift = Gift.New(Guid.NewGuid(), owner, new("Подарок", Price: 12000));
        gift.Act(first, new("split"), 3, true);
        return gift;
    }

    [Fact] public void EnrollmentHasNoFixedLimit()
    {
        var gift = NewSplit();
        gift.Act(second, new("join"), 3, true);
        gift.Act(third, new("join"), 3, true);
        Assert.Equal(3, gift.Shares.Count);
    }

    [Fact] public void CoordinatorExitPreservesOtherSharesAndUsesJoinOrder()
    {
        var gift = NewSplit();
        gift.Act(third, new("join"), 3, true);
        gift.Act(second, new("join"), 3, true);
        gift.Shares.Reverse(); // DB navigation order is unspecified.
        gift.Act(first, new("leave"), 3, true);
        Assert.Equal(third, gift.DonorId);
        Assert.Equal(2, gift.Shares.Count);
    }

    [Fact] public void LegacyCancelOnlyRemovesCoordinator()
    {
        var gift = NewSplit();
        gift.Act(second, new("join"), 3, true);
        gift.Act(first, new("cancel"), 3, true);
        Assert.Equal("split", gift.Mode);
        Assert.Equal(second, gift.DonorId);
        Assert.Single(gift.Shares);
    }

    [Fact] public void LastExitReleasesGift()
    {
        var gift = NewSplit();
        gift.Act(first, new("leave"), 3, true);
        Assert.Equal("free", gift.Mode);
        Assert.Null(gift.DonorId);
        Assert.Empty(gift.Shares);
    }

    [Fact] public void ClosedCollectionRejectsJoinAndCanBeBought()
    {
        var gift = NewSplit();
        gift.Act(second, new("join"), 3, true);
        Assert.Throws<RuleException>(() => gift.Act(first, new("buy"), 3, true));
        Assert.Throws<RuleException>(() => gift.Act(second, new("close-split"), 3, true));
        gift.Act(first, new("close-split"), 3, true);
        Assert.Throws<RuleException>(() => gift.Act(third, new("join"), 3, true));
        gift.Act(first, new("buy"), 3, true);
        Assert.Throws<RuleException>(() => gift.Act(first, new("leave"), 3, true));
    }

    [Fact] public void CannotCloseWithOneParticipant()
    {
        Assert.Throws<RuleException>(() => NewSplit().Act(first, new("close-split"), 3, true));
    }

    [Fact] public void LeavingClosedCollectionReopensAndPreservesOwnerPrivacy()
    {
        var gift = NewSplit();
        gift.Act(second, new("join"), 3, true);
        gift.Act(first, new("close-split"), 3, true);
        gift.Act(first, new("leave"), 3, true);
        Assert.True(gift.SplitOpen);
        Assert.Equal(second, gift.DonorId);
        var view = gift.View(owner, new Dictionary<Guid, PersonDto> { [owner] = new(owner, "Получатель") });
        Assert.Null(view.Participation);
        Assert.Null(view.Original);
    }
}
