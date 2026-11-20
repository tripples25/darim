using Wishlist.Server.Services;

namespace Wishlist.Tests;

public class RussianNameTests
{
    [Theory]
    [InlineData("Юлия", "Юлии")]
    [InlineData("Павел Лукоянов", "Павла Лукоянова")]
    [InlineData("Анна Иванова", "Анны Ивановой")]
    [InlineData("Саша", "Саши")]
    [InlineData("Максим Шевченко", "Максима Шевченко")]
    [InlineData("АЛЕКСЕЙ", "АЛЕКСЕЯ")]
    [InlineData("@sharphurt", "@sharphurt")]
    [InlineData("Юлия 🎁", "Юлия 🎁")]
    public void DeclinesKnownNamesAndPreservesAmbiguousOnes(string name, string expected)
        => Assert.Equal(expected, RussianName.Genitive(name));
}
