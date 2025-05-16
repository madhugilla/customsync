namespace cosmosofflinewithLCC.Models
{
    public class Order
    {
        public string Id { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public DateTime LastModified { get; set; }

        public string UserId { get; set; } = string.Empty;
        public string Type { get; set; } = "Order";

    }
}