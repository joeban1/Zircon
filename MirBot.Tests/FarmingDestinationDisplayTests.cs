using System.Text.Json;
using Xunit;

namespace MirBot.Tests
{
    public sealed class FarmingDestinationDisplayTests
    {
        [Fact]
        public void StatusCarriesChoiceSeparatelyFromCurrentLocation()
        {
            var status = new BotStatus
            {
                MapName = "Deserted Mine Lv 1",
                FarmingDestinationName = "Deserted Mine Lv 2",
                FarmingDestinationReason = "looking for 2 skill books: Book A, Book B"
            };

            string json = JsonSerializer.Serialize(status,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            Assert.Contains("\"mapName\":\"Deserted Mine Lv 1\"", json);
            Assert.Contains("\"farmingDestinationName\":\"Deserted Mine Lv 2\"", json);
            Assert.Contains("\"farmingDestinationReason\":\"looking for 2 skill books", json);
            Assert.Contains("data-r=\"farmdest\"", StatusPage.Html);
            Assert.Contains("data-r=\"farmreason\"", StatusPage.Html);
        }
    }
}
