using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using shared;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SimpleLambdaFunction;

public class Function
{
    private readonly IAmazonDynamoDB _client;

    public Function()
    {
        _client = new AmazonDynamoDBClient();
    }
    
    public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        if (request.PathParameters == null || !request.PathParameters.TryGetValue("id", out var locationId))
        {
            return ResponseCreator.CreateResponse(400, "Invalid request", "Location id is missing in path");
        }
        
        var query = new QueryRequest
        {
            TableName = "Slots",
            KeyConditionExpression = "location_id = :locationId",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":locationId"] = new AttributeValue { N = locationId }
            }
        };

        var response = await _client.QueryAsync(query);

        var result = response.Items.Select(item => new SlotDto
        {
            id = item["id"].N,
            slot = item["slot"].S,
            isAvaliable = item["is_available"].BOOL,
            locationId = item["location_id"].N
        }).ToList();

        return ResponseCreator.CreateResponse(200, "Successful return of the list of slots!", result);
    }
}
