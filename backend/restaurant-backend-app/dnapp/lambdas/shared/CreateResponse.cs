using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;

namespace shared;

public class ResponseCreator
{
    public static APIGatewayProxyResponse CreateResponse(int statusCode, string description, object? value)
    {
        string statusString = statusCode >= 200 && statusCode < 300 ? "success" : "error";
        
        var responseBody = new ApiResponse<object>
        {
            status = statusString,
            description = description,
            value = value
        };

        return new APIGatewayProxyResponse
        {
            StatusCode = statusCode,
            Body = JsonSerializer.Serialize(responseBody),
            Headers = new Dictionary<string, string>
            {
                { "Content-Type", "application/json" },
                { "Access-Control-Allow-Origin", "*" },
                { "Access-Control-Allow-Methods", "OPTIONS,POST,GET,DELETE" },
                { "Access-Control-Allow-Headers", "Content-Type,X-Amz-Date,Authorization,X-Api-Key" }
            }
        };
    }
}