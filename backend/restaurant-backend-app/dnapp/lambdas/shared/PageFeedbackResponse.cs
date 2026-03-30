using System.Collections.Generic;

namespace shared;

public class PageFeedbackResponse
{
    public List<FeedbackDto> items { get; set; }
    public int page { get; set; }
    public int size { get; set; }
    public int totalItems { get; set; }
    public int totalPages { get; set; }
}