using Xunit;

namespace MirBot.Tests
{
    public sealed class MapTripMemoryTests
    {
        [Fact]
        public void TripRecordsChoiceArrivalCreditedAwardsAndDeparture()
        {
            var memory = new MapTripMemory(null);
            MapTripEntry entry = memory.Start("Mirbot3", "Jill", "Taoist", 136,
                "Deserted Mine Lv 1", "looking for Summon Skeleton");

            memory.Observe(entry, 1, 0); // travelling through another map
            Assert.Null(memory.Snapshot()[0].ArrivedUtc);

            memory.Observe(entry, 136, 2);
            memory.Close(entry, "town trip: restocking", 3);

            MapTripEntry row = Assert.Single(memory.Snapshot());
            Assert.NotNull(row.ArrivedUtc);
            Assert.Equal(3, row.CreditedKills);
            Assert.Equal("town trip: restocking", row.LeavingReason);
            Assert.NotNull(row.LeftUtc);
            Assert.Equal("looking for Summon Skeleton", row.SelectionReason);

            memory.Observe(entry, 136, 9); // closed trips must stay closed
            Assert.Equal(3, memory.Snapshot()[0].CreditedKills);
        }
    }
}
