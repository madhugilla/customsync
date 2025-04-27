namespace cosmosofflinewithLCC.Models
{
    public class Item
    {
        [Newtonsoft.Json.JsonProperty("id")]
        public string Id { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime LastModified { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string Type { get; set; } = "Item";
    }
}