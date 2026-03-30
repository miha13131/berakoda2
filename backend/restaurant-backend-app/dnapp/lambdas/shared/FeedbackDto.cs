namespace shared;

public class FeedbackDto
{
    public string id { get; set; }
    public string username { get; set; }
    public string date { get; set; }
    public string image { get; set; }
    public string description { get; set; }
    public required int rating { get; set; }
    public string type { get; set; }
    public int locationId { get; set; }
    public string reservationId { get; set; }
    public string userId { get; set; }
}