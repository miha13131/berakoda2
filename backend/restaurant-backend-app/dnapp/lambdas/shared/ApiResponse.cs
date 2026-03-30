namespace shared;

public class ApiResponse<T>
{
    public string status { get; set; } = string.Empty;
    public string description { get; set; } = string.Empty;
    public T? value { get; set; }
}