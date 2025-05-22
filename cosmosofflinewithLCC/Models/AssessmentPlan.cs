using System.Text.Json.Serialization;

namespace cosmosofflinewithLCC.Models
{
    public class AssessmentPlan

    {

        [JsonPropertyName("id")]

        public string ID { get; set; } = Guid.NewGuid().ToString();

        [JsonPropertyName("type")]

        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("uid")]

        public string UID { get; set; }

        [JsonPropertyName("oiid")]

        public string OIID { get; set; }

        [JsonPropertyName("startdate")]

        public string StartDate { get; set; }

        [JsonPropertyName("enddate")]

        public string EndDate { get; set; }

        [JsonPropertyName("planname")]

        public string PlanName { get; set; }

        [JsonPropertyName("isdeleted")]

        public bool IsDeleted { get; set; }

        public DateTime LastModified { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("partitionKey")]

        public string PartitionKey => $"{OIID}:AssessmentPlan";



        [JsonPropertyName("domainLevels")]

        public DomainLevel[] DomainsList { get; set; }



    }

    public class DomainLevel

    {

        [JsonPropertyName("DomainName")]

        public string DomainName { get; set; }



        [JsonPropertyName("Levels")]

        public List<AssessmentLevels> Levels { get; set; } = new List<AssessmentLevels>();

    }

    public class AssessmentLevels //: ObservableCollection<LevelStep>

    {

        [JsonPropertyName("LevelName")]

        public string LevelName { get; set; }



        [JsonPropertyName("Steps")]

        public List<LevelStep> Steps { get; set; } = new List<LevelStep>();



        public AssessmentLevels(string levelName, List<LevelStep> steps)// : base(steps)

        {

            LevelName = levelName;

            Steps = new List<LevelStep>(steps);

        }

        public class LevelStep

        {

            public string Text { get; set; }



            public LevelStep(string text)

            {

                Text = text;


            }






        }






    }
}