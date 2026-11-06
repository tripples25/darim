using Wishlist.Contracts;
using Wishlist.Core;
namespace Wishlist.Tests;
public class GiftTests {
 readonly Guid owner=Guid.NewGuid(), alice=Guid.NewGuid(), bob=Guid.NewGuid(), outsider=Guid.NewGuid();
 Gift New() => Gift.New(Guid.NewGuid(),owner,new("Кофемолка", Price:12000));
 Dictionary<Guid,PersonDto> People() => new(){[owner]=new(owner,"Получатель"),[alice]=new(alice,"Алиса","@alice"),[bob]=new(bob,"Боб","@bob"),[outsider]=new(outsider,"Другой")};
 [Fact] public void OwnerContactIsNotExposedInPublicGiftView(){var people=People();people[owner]=new(owner,"Получатель","private-contact");Assert.Null(New().View(bob,people).Owner.Contact);}
 [Fact] public void SplitCoordinatorVisibleOnlyToParticipantsAndChangesAfterTransfer(){var g=New();g.Act(alice,new("split",2),3,true);g.Act(bob,new("join"),3,true);Assert.Equal(alice,g.View(bob,People()).Participation!.CoordinatorId);Assert.Null(g.View(outsider,People()).Participation!.CoordinatorId);g.Act(alice,new("transfer",UserId:bob),3,true);Assert.Equal(bob,g.View(alice,People()).Participation!.CoordinatorId);}
 [Fact] public void OwnerCannotReserveOwnGift() => Assert.Throws<RuleException>(()=>New().Act(owner,new("reserve"),3,true));
 [Fact] public void SoloReservationExcludesSplit() { var g=New();g.Act(alice,new("reserve"),3,true);Assert.Throws<RuleException>(()=>g.Act(bob,new("split",2),3,true)); }
 [Fact] public void OwnerNeverReceivesParticipationOrOriginal() {var g=New();g.Act(alice,new("reserve"),3,true);var v=g.View(owner,People());Assert.Null(v.Participation);Assert.Null(v.Original);}
 [Fact] public void OtherPeopleCannotSeeSoloIdentity() {var g=New();g.Act(alice,new("reserve"),3,true);var v=g.View(bob,People());Assert.Equal("solo",v.Participation!.Mode);Assert.Null(v.Participation.Participants);Assert.False(v.Participation.IsMine);Assert.Null(v.Original);}
 [Fact] public void SplitCreatorOccupiesOneSlot() {var g=New();g.Act(alice,new("split",2),3,true);Assert.Single(g.Shares);Assert.Equal(alice,g.Shares[0].UserId);}
 [Fact] public void SplitCannotExceedAvailableMembers() => Assert.Throws<RuleException>(()=>New().Act(alice,new("split",4),3,true));
 [Fact] public void SplitRequiresContact() => Assert.Throws<RuleException>(()=>New().Act(alice,new("split",2),3,false));
 [Fact] public void CannotJoinTwice() {var g=New();g.Act(alice,new("split",2),3,true);Assert.Throws<RuleException>(()=>g.Act(alice,new("join"),3,true));}
 [Fact] public void FullSplitCannotBeJoined() {var g=New();g.Act(alice,new("split",2),3,true);g.Act(bob,new("join"),3,true);Assert.Throws<RuleException>(()=>g.Act(outsider,new("join"),3,true));}
 [Fact] public void ParticipantIdentityOnlyAvailableInsideSplit() {var g=New();g.Act(alice,new("split",2),3,true);Assert.Single(g.View(alice,People()).Participation!.Participants!);Assert.Null(g.View(bob,People()).Participation!.Participants);}
 [Fact] public void CannotBuyUnfilledSplit() {var g=New();g.Act(alice,new("split",2),3,true);Assert.Throws<RuleException>(()=>g.Act(alice,new("buy"),3,true));}
 [Fact] public void PurchasedSplitCannotBeLeft() {var g=New();g.Act(alice,new("split",2),3,true);g.Act(bob,new("join"),3,true);g.Act(alice,new("buy"),3,true);Assert.Throws<RuleException>(()=>g.Act(bob,new("leave"),3,true));}
 [Fact] public void CoordinatorMustTransferBeforeLeaving() {var g=New();g.Act(alice,new("split",2),3,true);g.Act(bob,new("join"),3,true);Assert.Throws<RuleException>(()=>g.Act(alice,new("leave"),3,true));g.Act(alice,new("transfer",UserId:bob),3,true);g.Act(alice,new("leave"),3,true);Assert.Equal(bob,g.DonorId);Assert.Single(g.Shares);}
 [Fact] public void SplitCancelFreesGift() {var g=New();g.Act(alice,new("split",2),3,true);g.Act(bob,new("join"),3,true);g.Act(alice,new("cancel"),3,true);Assert.Equal("free",g.Mode);Assert.Empty(g.Shares);}
 [Fact] public void OnlyDonorCanCancelSolo() {var g=New();g.Act(alice,new("reserve"),3,true);Assert.Throws<RuleException>(()=>g.Act(bob,new("cancel"),3,true));g.Act(alice,new("cancel"),3,true);Assert.Equal("free",g.Mode);}
 [Fact] public void EditingOccasionPreservesReservationAndOriginal() {var g=New();g.Act(alice,new("reserve"),3,true);g.Edit(owner,new("Новая кофемолка",Occasion:"День рождения"));var v=g.View(alice,People());Assert.Equal("solo",v.Participation!.Mode);Assert.Equal("Кофемолка",v.Original!.Title);Assert.Equal("День рождения",v.Details.Occasion);}
 [Fact] public void ArchiveKeepsDonorHistory() {var g=New();g.Act(alice,new("reserve"),3,true);g.Archive(owner);Assert.True(g.Archived);Assert.True(g.View(alice,People()).Participation!.IsMine);Assert.Throws<RuleException>(()=>g.Act(bob,new("split",2),3,true));}
 [Theory][InlineData("")][InlineData("   ")] public void TitleRequired(string title) => Assert.Throws<RuleException>(()=>Gift.New(Guid.NewGuid(),owner,new(title)));
 [Fact] public void UnsafeLinksRejected() => Assert.Throws<RuleException>(()=>Gift.New(Guid.NewGuid(),owner,new("Подарок",Url:"javascript:alert(1)")));
 [Fact] public void NegativePriceRejected() => Assert.Throws<RuleException>(()=>Gift.New(Guid.NewGuid(),owner,new("Подарок",Price:-1)));
 [Fact] public void EditingRequiresOwner() => Assert.Throws<RuleException>(()=>New().Edit(bob,new("Другое")));
}
