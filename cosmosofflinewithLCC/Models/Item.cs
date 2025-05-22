using System.Text.Json.Serialization;

namespace cosmosofflinewithLCC.Models
{
    public class Item
    {
        [JsonPropertyName("id")]
        public string ID { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime LastModified { get; set; }
        [JsonPropertyName("oiid")]
        public string OIID { get; set; } = string.Empty;
        [JsonPropertyName("type")]
        public string Type { get; set; } = "Item";

        [JsonPropertyName("partitionKey")]
        public string PartitionKey => $"{OIID}:{Type ?? GetType().Name}";
    }
}