namespace shared;

public class ApiResponse<T>
{
    public string Status { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public T? Value { get; set; }
}