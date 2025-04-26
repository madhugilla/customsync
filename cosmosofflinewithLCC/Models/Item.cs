namespace cosmosofflinewithLCC.Models
{
    public class Item
    {
        public string Id { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime LastModified { get; set; }
    }
}