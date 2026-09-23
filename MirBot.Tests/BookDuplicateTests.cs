using Xunit;

namespace MirBot.Tests
{
    public sealed class BookDuplicateTests
    {
        [Theory]
        [InlineData(BookVerdict.TooEarly, true)]
        [InlineData(BookVerdict.Wanted, true)]
        [InlineData(BookVerdict.AlreadyKnown, false)]
        [InlineData(BookVerdict.WrongClass, false)]
        [InlineData(BookVerdict.Junk, false)]
        [InlineData(BookVerdict.NotABook, false)]
        public void OnlyUnlearnedUsableClassBooksAreProtectedFromSelling(
            BookVerdict verdict, bool expected)
        {
            Assert.Equal(expected, Backpack.RetainUnlearnedBook(verdict));
        }
    }
}
