using System.Text.Json.Serialization;

namespace cosmosofflinewithLCC.Models
{
    public class Item
    {
        [JsonPropertyName("id")]
        public string ID { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime LastModified { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string Type { get; set; } = "Item";
    }
}