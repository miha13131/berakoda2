using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TablesAvailableHandler;

public class TableDto
{
    public string TableId { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public int Capacity { get; set; }
}

public class TableResponseDto
{
    [JsonPropertyName("tableId")]
    public string TableId { get; set; } = string.Empty;

    [JsonPropertyName("tableName")]
    public string TableName { get; set; } = string.Empty;

    [JsonPropertyName("capacity")]
    public int Capacity { get; set; }

    [JsonPropertyName("availableSlots")]
    public List<string> AvailableSlots { get; set; } = new();
}